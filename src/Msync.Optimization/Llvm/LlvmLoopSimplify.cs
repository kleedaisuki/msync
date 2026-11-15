// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LoopSimplify.cpp simplifyLoop/simplifyOneLoop, optional SE/MSSA/TTI/AC absent.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    public static bool Run(LlvmFunction function, LlvmDominatorTree tree, LlvmLoopInfo loops,
        bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return RunFunction(tree, loops, preserveLcssa, null);
        return trace.MeasureDetailed("optimizer.pass.llvm-loop-simplify", (Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
            static state => RunFunction(state.Tree, state.Loops, state.Preserve, state.Trace),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name });
    }
    private static bool RunFunction(LlvmDominatorTree tree, LlvmLoopInfo loops, bool preserveLcssa, CompilationTrace? trace)
    {
        bool changed = false;
        // Index iteration is the managed carrier for the source loop-list iterator:
        // replaceLoop changes a sibling entry in-place while simplifying a nest.
        for (int i = 0; i < loops.TopLevelLoops.Count; ++i)
            changed |= SimplifyLoop(loops.TopLevelLoops[i], tree, loops, preserveLcssa, trace);
        if (preserveLcssa) Debug.Assert(loops.TopLevelLoops.All(loop => loop.IsRecursivelyLCSSAForm(tree, loops)), "LCSSA is broken after loop-simplify.");
        return changed;
    }
    public static bool SimplifyLoop(LlvmLoop loop, LlvmDominatorTree tree, LlvmLoopInfo loops,
        bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (preserveLcssa) Debug.Assert(loop.IsRecursivelyLCSSAForm(tree, loops), "Requested to preserve LCSSA, but it's already broken.");
        var work = new List<LlvmLoop> { loop };
        for (int i = 0; i < work.Count; ++i) work.AddRange(work[i].SubLoops);
        bool changed = false;
        while (work.Count != 0)
        {
            var next = work[^1]; work.RemoveAt(work.Count - 1);
            if (trace is null) changed |= SimplifyOneLoop(next, work, tree, loops, preserveLcssa, null);
            else changed |= trace.MeasureDetailed("optimizer.llvm-loop-simplify.loop", (Loop: next, Work: work, Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
                static state => SimplifyOneLoop(state.Loop, state.Work, state.Tree, state.Loops, state.Preserve, state.Trace),
                attributes: Attributes(next.Header));
        }
        return changed;
    }
    private static bool SimplifyOneLoop(LlvmLoop loop, List<LlvmLoop> work, LlvmDominatorTree tree,
        LlvmLoopInfo loops, bool preserveLcssa, CompilationTrace? trace)
    {
        bool changed = false;
    ReprocessLoop:
        trace?.Metric("loop.process_visits", 1);
        foreach (var block in loop.Blocks)
        {
            if (ReferenceEquals(block, loop.Header)) continue;
            var badPredecessors = new List<LlvmBasicBlock>(); var seen = new HashSet<LlvmBasicBlock>();
            foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                if (!loop.Contains(predecessor) && seen.Add(predecessor)) badPredecessors.Add(predecessor);
            foreach (var predecessor in badPredecessors)
            { ChangeToUnreachable(predecessor.GetTerminator(), preserveLcssa, trace); changed = true; }
        }
        var exiting = new List<LlvmBasicBlock>();
        loop.GetExitingBlocks(exiting);
        foreach (var block in exiting)
        {
            if (block.GetTerminator() is not LlvmCondBrInstruction { Condition: LlvmUndefValue } branch) continue;
            branch.Condition = LlvmConstantInt.GetBool(block.Context, !loop.Contains(branch.GetSuccessor(0)));
            changed = true; trace?.Metric("loop.undef_exit_conditions_resolved", 1);
        }
        var preheader = loop.GetLoopPreheader();
        if (preheader is null)
        { preheader = InsertPreheaderForLoop(loop, tree, loops, preserveLcssa, trace); changed = true; }
        changed |= FormDedicatedExitBlocks(loop, tree, loops, preserveLcssa, trace);
        var latch = loop.GetLoopLatch();
        if (latch is null)
        {
            // Source optional TTI=null: no target branch-divergence policy is selected.
            LlvmLoop? outer = null;
            if (loop.GetNumBackEdges() < 8)
                outer = trace is null ? SeparateNestedLoop(loop, preheader, tree, loops, preserveLcssa, null) :
                    trace.MeasureDetailed("optimizer.llvm-loop-simplify.partition-nested-loop", (Loop: loop, Preheader: preheader, Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
                        static state => SeparateNestedLoop(state.Loop, state.Preheader, state.Tree, state.Loops, state.Preserve, state.Trace));
            if (outer is not null)
            {
                work.Add(outer); changed = true; trace?.Metric("loop.reprocess_after_separation", 1);
                goto ReprocessLoop;
            }
            changed |= SimplifyHeaderPhis(loop, tree, loops, preserveLcssa, trace);
            latch = InsertUniqueBackedgeBlock(loop, preheader, tree, loops, trace);
            if (latch is not null) changed = true;
        }
        changed |= SimplifyHeaderPhis(loop, tree, loops, preserveLcssa, trace);
        if (HasUniqueExit(loop, exiting))
            changed |= trace is null ? MergeExits(loop, exiting, preheader, tree, loops, preserveLcssa, null) :
                trace.MeasureDetailed("optimizer.llvm-loop-simplify.merge-exits", (Loop: loop, Exiting: exiting, Preheader: preheader, Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
                    static state => MergeExits(state.Loop, state.Exiting, state.Preheader, state.Tree, state.Loops, state.Preserve, state.Trace));
        return changed;
    }
    private static bool HasUniqueExit(LlvmLoop loop, IReadOnlyList<LlvmBasicBlock> exiting)
    {
        LlvmBasicBlock? unique = null;
        foreach (var block in exiting)
            foreach (var successor in LlvmDominatorTree.Successors(block))
            {
                if (loop.Contains(successor)) continue;
                if (unique is null) unique = successor;
                else if (!ReferenceEquals(unique, successor)) return false;
            }
        return true;
    }
    private static bool MergeExits(LlvmLoop loop, IReadOnlyList<LlvmBasicBlock> exiting, LlvmBasicBlock? preheader,
        LlvmDominatorTree tree, LlvmLoopInfo loops, bool preserveLcssa, CompilationTrace? trace)
    {
        bool changed = false;
        foreach (var block in exiting)
        {
            if (LlvmDominatorTree.Predecessors(block).Take(2).Count() != 1) continue;
            if (block.GetTerminator() is not LlvmCondBrInstruction { Condition: LlvmCmpInstruction compare } branch ||
                !ReferenceEquals(compare.Parent, block)) continue;
            bool allInvariant = true, anyInvariant = false;
            for (var instruction = block.FirstInstruction; !ReferenceEquals(instruction, branch);)
            {
                var next = instruction!.Next;
                if (!ReferenceEquals(instruction, compare) &&
                    !MakeLoopInvariant(loop, instruction, ref anyInvariant, preheader?.GetTerminator(), trace))
                { allInvariant = false; break; }
                instruction = next;
            }
            changed |= anyInvariant;
            if (!allInvariant || !LlvmSimplifyCfg.FoldBranchToCommonDest(branch, trace)) continue;
            Debug.Assert(!LlvmDominatorTree.Predecessors(block).Any());
            changed = true;
            loops.RemoveBlock(block);
            var node = tree.GetNode(block)!;
            while (node.Children.FirstOrDefault() is { } child) tree.ChangeImmediateDominator(child, node.IDom!);
            tree.EraseNode(block);
            RemovePredecessor(branch.GetSuccessor(0), block, preserveLcssa, trace);
            RemovePredecessor(branch.GetSuccessor(1), block, preserveLcssa, trace);
            DeleteBlock(block, trace);
            trace?.Metric("loop.exiting_blocks_merged", 1);
        }
        return changed;
    }
}
