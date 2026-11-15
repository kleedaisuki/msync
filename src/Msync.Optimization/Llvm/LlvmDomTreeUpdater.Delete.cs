// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DomTreeUpdater.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDomTreeUpdater
{
    public void DeleteBlock(LlvmBasicBlock block) => Observe("delete-block", block,
        static (updater, deleted) => updater.DeleteBlockCore(deleted), block);

    private void DeleteBlockCore(LlvmBasicBlock block)
    {
        PrepareDeletedBlock(block);
        if (IsLazy) { deletedBlocks.Add(block); Count("blocks.queued_for_deletion"); return; }
        EraseDeletedBlockNode(block);
        EraseBlock(block);
    }

    public void CallbackDeleteBlock(LlvmBasicBlock block, Action<LlvmBasicBlock> callback) =>
        Observe("callback-delete-block", (Block: block, Callback: callback),
            static (updater, state) => updater.CallbackDeleteBlockCore(state.Block, state.Callback), block);

    private void CallbackDeleteBlockCore(LlvmBasicBlock block, Action<LlvmBasicBlock> callback)
    {
        PrepareDeletedBlock(block);
        if (IsLazy)
        {
            callbacks.Add(new BlockDeletionCallback(block, callback, this));
            deletedBlocks.Add(block);
            Count("callbacks.queued");
            return;
        }
        EraseDeletedBlockNode(block);
        block.RemoveFromParent();
        callback(block);
        Count("callbacks.invoked");
        block.DeleteValue();
        Count("blocks.deleted");
    }

    private void PrepareDeletedBlock(LlvmBasicBlock block)
    {
        Debug.Assert(!LlvmDominatorTree.Predecessors(block).Any(), "Block awaiting deletion has predecessors");
        while (block.LastInstruction is { } instruction)
        {
            if (!instruction.UseEmpty) instruction.ReplaceAllUsesWith(LlvmPoisonValue.Get(instruction.Type));
            instruction.EraseFromParent();
            Count("blocks.dead_instructions_removed");
        }
        block.Append(new LlvmUnreachableInstruction(block.Context));
    }

    private bool ForceFlushDeletedBlocks()
    {
        if (deletedBlocks.Count == 0) return false;
        foreach (var block in deletedBlocks)
        {
            Debug.Assert(block.Instructions.Count == 1 && block.GetTerminator() is LlvmUnreachableInstruction,
                "Block was modified while awaiting deletion");
            EraseDeletedBlockNode(block);
            EraseBlock(block);
        }
        deletedBlocks.Clear();
        callbacks.Clear();
        return true;
    }

    private void EraseDeletedBlockNode(LlvmBasicBlock block)
    {
        if (tree is not null && !recalculatingTree && tree.GetNode(block) is not null) tree.EraseNode(block, metrics);
        if (postTree is not null && !recalculatingPostTree && postTree.GetNode(block) is not null) postTree.EraseNode(block, metrics);
    }

    private void EraseBlock(LlvmBasicBlock block)
    {
        block.EraseFromParent();
        Count("blocks.deleted");
    }

    // DomTreeUpdater.h CallBackOnDeletion: the lazy callback runs during the
    // actual Value destructor notification, after BasicBlock contents are gone.
    private sealed class BlockDeletionCallback(LlvmBasicBlock block, Action<LlvmBasicBlock> callback,
        LlvmDomTreeUpdater updater) : LlvmCallbackValueHandle(block)
    {
        protected override void Deleted()
        {
            callback(block);
            updater.Count("callbacks.invoked");
            base.Deleted();
        }
    }
}
