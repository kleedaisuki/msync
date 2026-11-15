// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LoopSimplify.cpp separateNestedLoop/findPHIToPartitionLoops/addBlockAndPredsToSet.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    private static LlvmPhiInstruction? FindPhiToPartitionLoops(LlvmLoop loop, LlvmDominatorTree tree, CompilationTrace? trace)
    {
        for (var instruction = loop.Header.FirstInstruction; instruction is LlvmPhiInstruction phi;)
        {
            instruction = instruction.Next;
            if (LlvmInstructionSimplify.SimplifyPhi(phi, tree) is { } value)
            {
                phi.ReplaceAllUsesWith(value); phi.EraseFromParent();
                trace?.Metric("nesting.degenerate_phis_removed", 1); continue;
            }
            for (int i = 0; i < phi.IncomingCount; ++i)
                if (ReferenceEquals(phi.GetIncomingValue(i), phi) && loop.Contains(phi.GetIncomingBlock(i))) return phi;
        }
        return null;
    }
    private static void AddBlockAndPredsToSet(LlvmBasicBlock input, LlvmBasicBlock stop,
        HashSet<LlvmBasicBlock> blocks, CompilationTrace? trace)
    {
        var work = new Stack<LlvmBasicBlock>(); work.Push(input);
        do
        {
            var block = work.Pop(); trace?.Metric("nesting.predecessor_work_pops", 1);
            if (!blocks.Add(block) || ReferenceEquals(block, stop)) continue;
            foreach (var predecessor in LlvmDominatorTree.Predecessors(block)) work.Push(predecessor);
        } while (work.Count != 0);
    }
    private static LlvmLoop? SeparateNestedLoop(LlvmLoop loop, LlvmBasicBlock? preheader,
        LlvmDominatorTree tree, LlvmLoopInfo loops, bool preserveLcssa, CompilationTrace? trace)
    {
        if (preheader is null) return null;
        foreach (var block in loop.Blocks)
            foreach (var instruction in block.Instructions)
                if (instruction is LlvmCallBase { IsConvergent: true })
                {
                    trace?.Metric("nesting.convergent_call", 1);
                    return null;
                }
        var header = loop.Header;
        var phi = FindPhiToPartitionLoops(loop, tree, trace);
        if (phi is null) return null;
        var outerPredecessors = new List<LlvmBasicBlock>();
        for (int i = 0; i < phi.IncomingCount; ++i)
            if (!ReferenceEquals(phi.GetIncomingValue(i), phi) || !loop.Contains(phi.GetIncomingBlock(i)))
                outerPredecessors.Add(phi.GetIncomingBlock(i));
        var split = SplitPredecessors(header, outerPredecessors, ".outer", tree, loops, preserveLcssa, trace);
        PlaceSplitBlockCarefully(split, outerPredecessors, loop, trace);
        var outer = loops.AllocateLoop();
        loops.ReplaceLoop(loop, outer);
        outer.AddChildLoop(loop);
        foreach (var block in loop.Blocks) outer.AddBlockEntry(block);
        loop.MoveToHeader(header);
        var inside = new HashSet<LlvmBasicBlock>();
        foreach (var predecessor in LlvmDominatorTree.Predecessors(header))
            if (tree.Dominates(header, predecessor)) AddBlockAndPredsToSet(predecessor, header, inside, trace);
        for (int i = 0; i < loop.SubLoops.Count;)
        {
            var child = loop.SubLoops[i];
            if (inside.Contains(child.Header)) ++i;
            else outer.AddChildLoop(loop.RemoveChildLoop(child));
        }
        for (int i = 0; i < loop.Blocks.Count; ++i)
        {
            var block = loop.Blocks[i];
            if (inside.Contains(block)) continue;
            loop.RemoveBlockFromLoop(block);
            if (ReferenceEquals(loops.GetLoopFor(block), loop)) loops.ChangeLoopFor(block, outer);
            --i;
        }
        DedicatedExits(loop, tree, loops, preserveLcssa, trace);
        if (preserveLcssa)
        {
            LlvmLcssa.FormLCSSA(loop, tree, loops, trace);
            Debug.Assert(outer.IsRecursivelyLCSSAForm(tree, loops), "LCSSA is broken after separating nested loops!");
        }
        trace?.Metric("nesting.loops_separated", 1);
        return outer;
    }
}
