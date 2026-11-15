// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Local.cpp changeToUnreachable (DTU/MSSAU=null); BasicBlock.cpp removePredecessor.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    private static void RemovePredecessor(LlvmBasicBlock block, LlvmBasicBlock predecessor,
        bool keepOneInputPhis, CompilationTrace? trace)
    {
        Debug.Assert(block.HasNUsesOrMore(16) || LlvmDominatorTree.Predecessors(block).Contains(predecessor), "Pred is not a predecessor!");
        if (block.FirstInstruction is not LlvmPhiInstruction first) return;
        int originalPreds = first.IncomingCount;
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi;)
        {
            instruction = instruction.Next;
            phi.RemoveIncomingValue(phi.GetBasicBlockIndex(predecessor), !keepOneInputPhis);
            trace?.Metric("cfg.phi_predecessors_removed", 1);
            if (keepOneInputPhis || originalPreds == 1) continue;
            if (phi.HasConstantValue() is not { } constant) continue;
            phi.ReplaceAllUsesWith(constant); phi.EraseFromParent(); trace?.Metric("cfg.constant_phis_removed", 1);
        }
    }
    private static void ChangeToUnreachable(LlvmInstruction first, bool preserveLcssa, CompilationTrace? trace)
    {
        var block = first.Parent!;
        foreach (var successor in LlvmDominatorTree.Successors(block)) RemovePredecessor(successor, block, preserveLcssa, trace);
        block.InsertBefore(new LlvmUnreachableInstruction(block.Context) { Source = first.Source }, first);
        for (var instruction = first; instruction is not null;)
        {
            var current = instruction; instruction = instruction.Next;
            if (!current.UseEmpty) current.ReplaceAllUsesWith(LlvmPoisonValue.Get(current.Type));
            current.EraseFromParent(); trace?.Metric("cfg.unreachable_instructions_removed", 1);
        }
    }
    private static void DeleteBlock(LlvmBasicBlock block, CompilationTrace? trace)
    {
        for (var instruction = block.FirstInstruction; instruction is not null;)
        {
            var current = instruction; instruction = current.Next;
            if (!current.UseEmpty) current.ReplaceAllUsesWith(LlvmPoisonValue.Get(current.Type));
            current.EraseFromParent();
        }
        Debug.Assert(block.UseEmpty);
        block.EraseFromParent();
        trace?.Metric("cfg.blocks_deleted", 1);
    }
}
