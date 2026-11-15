// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/LoopUtils.cpp formDedicatedExitBlocks, no cached analysis arguments.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    public static bool FormDedicatedExitBlocks(LlvmLoop loop, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return DedicatedExits(loop, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.dedicated-exits", (Loop: loop, Trace: trace),
            static state => DedicatedExits(state.Loop, state.Trace), attributes: Attributes(loop.Header));
    }
    public static bool FormDedicatedExitBlocks(LlvmLoop loop, LlvmDominatorTree tree, LlvmLoopInfo loops,
        bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return DedicatedExits(loop, tree, loops, preserveLcssa, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.dedicated-exits", (Loop: loop, Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
            static state => DedicatedExits(state.Loop, state.Tree, state.Loops, state.Preserve, state.Trace), attributes: Attributes(loop.Header));
    }
    private static bool DedicatedExits(LlvmLoop loop, CompilationTrace? trace)
        => DedicatedExits(loop, null, null, false, trace);
    private static bool DedicatedExits(LlvmLoop loop, LlvmDominatorTree? tree, LlvmLoopInfo? loops,
        bool preserveLcssa, CompilationTrace? trace)
    {
        bool changed = false;
        var seen = new HashSet<LlvmBasicBlock>();
        var inside = new List<LlvmBasicBlock>();
        foreach (var block in loop.Blocks)
            foreach (var successor in LlvmDominatorTree.Successors(block))
            {
                trace?.Metric("dedicated_exit.successor_edges", 1);
                if (loop.Contains(successor) || !seen.Add(successor)) continue;
                bool dedicated = true;
                foreach (var predecessor in LlvmDominatorTree.Predecessors(successor))
                {
                    trace?.Metric("dedicated_exit.predecessor_edges", 1);
                    if (loop.Contains(predecessor)) inside.Add(predecessor);
                    else dedicated = false;
                }
                if (!dedicated)
                {
                    SplitPredecessors(successor, inside, ".loopexit", tree, loops, preserveLcssa, trace);
                    changed = true; trace?.Metric("dedicated_exit.blocks_split", 1);
                }
                else trace?.Metric("dedicated_exit.already_dedicated", 1);
                inside.Clear();
            }
        return changed;
    }
}
