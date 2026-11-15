// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicBlockUtils.cpp
// MergeBlockIntoPredecessor, represented CFG/DT/LoopInfo/MemorySSA paths.
using System.Diagnostics;
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmBasicBlockUtils
{
    public static bool MergeBlockIntoPredecessor(LlvmBasicBlock block, LlvmDomTreeUpdater? updater = null,
        LlvmLoopInfo? loops = null, LlvmMemorySsaUpdater? memoryUpdater = null,
        bool predecessorWithTwoSuccessors = false, LlvmDominatorTree? tree = null,
        CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        Count(trace, metrics, "cfg.merge_attempts");
        var predecessor = GetUniquePredecessor(block);
        if (predecessor is null || ReferenceEquals(predecessor, block)) return false;
        var predecessorTerminator = predecessor.GetTerminator();
        if (IsSpecialTerminator(predecessorTerminator) || predecessorTerminator.MayHaveSideEffects()) return false;
        if (!predecessorWithTwoSuccessors && !ReferenceEquals(GetUniqueSuccessor(predecessor), block)) return false;
        LlvmCondBrInstruction? conditional = null;
        LlvmBasicBlock? newSuccessor = null;
        int fallThrough = -1;
        if (predecessorWithTwoSuccessors)
        {
            if (predecessorTerminator is not LlvmCondBrInstruction branch || block.GetTerminator() is not LlvmUncondBrInstruction jump)
                return false;
            conditional = branch;
            newSuccessor = jump.Destination;
            fallThrough = ReferenceEquals(branch.GetSuccessor(0), block) ? 0 : 1;
        }
        foreach (var instruction in block.Instructions)
        {
            if (instruction is not LlvmPhiInstruction phi) break;
            for (int i = 0; i < phi.IncomingCount; ++i)
                if (ReferenceEquals(phi.GetIncomingValue(i), phi)) return false;
        }
        FoldSingleEntryPHINodes(block, trace, metrics);
        UpdateMergeChildren(tree, updater, predecessor, block, metrics);
        var updates = updater is null ? null : MergeUpdates(predecessor, block);
        var terminator = block.GetTerminator();
        var start = ReferenceEquals(block.FirstInstruction, terminator) ? predecessorTerminator : block.FirstInstruction!;
        MoveDefinitions(block, predecessor, predecessorTerminator, terminator, trace, metrics);
        memoryUpdater?.MoveAllAfterMergeBlocks(block, predecessor, start);
        block.ReplaceAllUsesWith(predecessor);
        if (predecessorWithTwoSuccessors)
        {
            terminator.EraseFromParent();
            block.Append(new LlvmUnreachableInstruction(block.Context) { Source = terminator.Source });
            conditional!.SetSuccessor(fallThrough, newSuccessor!);
        }
        else
        {
            predecessorTerminator.EraseFromParent();
            block.Remove(terminator);
            predecessor.Append(terminator);
            block.Append(new LlvmUnreachableInstruction(block.Context) { Source = terminator.Source });
            if (memoryUpdater?.MemorySsa.GetMemoryAccess(terminator) is { } access)
                memoryUpdater.MoveToPlace(access, predecessor, LlvmMemorySsaInsertionPlace.End);
        }
        if (!predecessor.HasName) predecessor.TakeName(block);
        loops?.RemoveBlock(block);
        if (updater is not null) updater.ApplyUpdates(updates!);
        tree?.EraseNode(block, metrics);
        DeleteDeadBlock(block, updater, trace: trace, metrics: metrics);
        Count(trace, metrics, "cfg.blocks_merged");
        return true;
    }
    private static bool IsSpecialTerminator(LlvmInstruction instruction) => instruction.Opcode is
        LlvmOpcode.CatchSwitch or LlvmOpcode.CatchRet or LlvmOpcode.CleanupRet or LlvmOpcode.Invoke or LlvmOpcode.Resume or LlvmOpcode.CallBr;

    private static void UpdateMergeChildren(LlvmDominatorTree? tree, LlvmDomTreeUpdater? updater,
        LlvmBasicBlock predecessor, LlvmBasicBlock block, Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(tree is null || updater is null, "Cannot use both DT and DTU updates");
        if (tree?.GetNode(predecessor) is not { } predNode) return;
        var blockNode = tree.GetNode(block)!;
        foreach (var child in blockNode.Children.ToArray()) tree.ChangeImmediateDominator(child, predNode, metrics);
    }
    private static List<LlvmCfgUpdate> MergeUpdates(LlvmBasicBlock predecessor, LlvmBasicBlock block)
    {
        List<LlvmCfgUpdate> updates = [];
        var previousSuccessors = LlvmDominatorTree.Successors(predecessor).ToHashSet();
        HashSet<LlvmBasicBlock> seen = [];
        foreach (var successor in LlvmDominatorTree.Successors(block))
            if (!previousSuccessors.Contains(successor) && seen.Add(successor)) updates.Add(new(LlvmCfgUpdateKind.Insert, predecessor, successor));
        seen.Clear();
        foreach (var successor in LlvmDominatorTree.Successors(block))
            if (seen.Add(successor)) updates.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
        updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
        return updates;
    }
    private static void MoveDefinitions(LlvmBasicBlock from, LlvmBasicBlock to, LlvmInstruction before,
        LlvmInstruction terminator, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        for (var instruction = from.FirstInstruction; instruction is not null && !ReferenceEquals(instruction, terminator);)
        {
            var next = instruction.Next;
            from.Remove(instruction);
            to.InsertBefore(instruction, before);
            Count(trace, metrics, "cfg.merged_instructions_moved");
            instruction = next;
        }
    }
}
