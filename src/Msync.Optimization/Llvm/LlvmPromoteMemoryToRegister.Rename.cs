// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// PromoteMemoryToRegister.cpp VectorWithUndo, RenamePassData and RenamePass.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmPromoteMemoryToRegister
{
    private sealed partial class Promotion
    {
        private void SetIncoming(int index, LlvmValue value)
        {
            if (ReferenceEquals(incoming[index], value)) return;
            undo.Add((index, incoming[index])); incoming[index] = value;
            Count(metrics, "rename.undo_records");
        }
        private void Push(LlvmBasicBlock block, LlvmBasicBlock? predecessor) => worklist.Push((block, predecessor, undo.Count));
        private void Rename()
        {
            Push(tree.RootNode!.Block!, null);
            while (worklist.TryPop(out var state))
            {
                while (undo.Count > state.Undo)
                {
                    var record = undo[^1]; undo.RemoveAt(undo.Count - 1);
                    incoming[record.Index] = record.Old; Count(metrics, "rename.undo_restores");
                }
                RenameBlock(state.Block, state.Pred);
            }
        }
        private void RenameBlock(LlvmBasicBlock block, LlvmBasicBlock? predecessor)
        {
            Count(metrics, "rename.block_entries");
            if (block.FirstInstruction is LlvmPhiInstruction first && phiToAlloca.ContainsKey(first))
            {
                int operandCount = first.OperandCount;
                int edges = LlvmDominatorTree.Successors(predecessor!).Count(successor => ReferenceEquals(successor, block));
                Debug.Assert(edges != 0, "Must be at least one edge from Pred to BB!");
                for (LlvmInstruction? instruction = first;
                    instruction is LlvmPhiInstruction phi && phi.OperandCount == operandCount; instruction = instruction.Next)
                {
                    int index = phiToAlloca[phi];
                    for (int i = 0; i < edges; ++i)
                    { phi.AddIncoming(incoming[index], predecessor!); Count(metrics, "rename.phi_edges"); }
                    SetIncoming(index, phi);
                }
            }
            if (!visited.Add(block)) { Count(metrics, "rename.already_visited"); return; }
            Count(metrics, "rename.blocks_visited");
            for (var instruction = block.FirstInstruction; instruction is { IsTerminator: false };)
            {
                var current = instruction; instruction = instruction.Next;
                Count(metrics, "rename.instructions");
                if (current is LlvmLoadInstruction { PointerOperand: LlvmAllocaInstruction source } load &&
                    allocaLookup.TryGetValue(source, out int loadIndex))
                    ReplaceLoad(load, incoming[loadIndex], "rename");
                else if (current is LlvmStoreInstruction { PointerOperand: LlvmAllocaInstruction destination } store &&
                    allocaLookup.TryGetValue(destination, out int storeIndex))
                {
                    SetIncoming(storeIndex, store.ValueOperand);
                    store.EraseFromParent(); Count(metrics, "removed.stores");
                }
            }
            var seen = new HashSet<LlvmBasicBlock>();
            var terminator = block.GetTerminator();
            for (int i = terminator.SuccessorCount - 1; i >= 0; --i)
            {
                var successor = terminator.GetSuccessor(i);
                if (seen.Add(successor)) Push(successor, block);
            }
        }
    }
}
