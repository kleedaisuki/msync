// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/LoopSimplify.cpp InsertPreheaderForLoop/placeSplitBlockCarefully;
// GenericLoopInfoImpl.h getLoopPreheader/getLoopPredecessor/getLoopLatch.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    public static LlvmBasicBlock? GetLoopPreheader(LlvmLoop loop) => loop.GetLoopPreheader();
    public static LlvmBasicBlock? GetLoopLatch(LlvmLoop loop) => loop.GetLoopLatch();
    public static LlvmBasicBlock InsertPreheaderForLoop(LlvmLoop loop, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return InsertPreheader(loop, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.insert-preheader", (Loop: loop, Trace: trace),
            static state => InsertPreheader(state.Loop, state.Trace), attributes: Attributes(loop.Header));
    }
    public static LlvmBasicBlock InsertPreheaderForLoop(LlvmLoop loop, LlvmDominatorTree tree, LlvmLoopInfo loops,
        bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return InsertPreheader(loop, tree, loops, preserveLcssa, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.insert-preheader", (Loop: loop, Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
            static state => InsertPreheader(state.Loop, state.Tree, state.Loops, state.Preserve, state.Trace), attributes: Attributes(loop.Header));
    }
    private static LlvmBasicBlock InsertPreheader(LlvmLoop loop, CompilationTrace? trace)
        => InsertPreheader(loop, null, null, false, trace);
    private static LlvmBasicBlock InsertPreheader(LlvmLoop loop, LlvmDominatorTree? tree, LlvmLoopInfo? loops,
        bool preserveLcssa, CompilationTrace? trace)
    {
        var outside = new List<LlvmBasicBlock>();
        foreach (var predecessor in LlvmDominatorTree.Predecessors(loop.Header))
        {
            trace?.Metric("preheader.predecessors_scanned", 1);
            if (!loop.Contains(predecessor)) outside.Add(predecessor);
        }
        var preheader = SplitPredecessors(loop.Header, outside, ".preheader", tree, loops, preserveLcssa, trace);
        PlaceSplitBlockCarefully(preheader, outside, loop, trace);
        return preheader;
    }
    private static void PlaceSplitBlockCarefully(LlvmBasicBlock split, IReadOnlyList<LlvmBasicBlock> predecessors,
        LlvmLoop loop, CompilationTrace? trace)
    {
        var blocks = split.Parent!.BasicBlocks;
        int position = IndexOf(blocks, split);
        if (predecessors.Contains(blocks[position - 1])) return;
        LlvmBasicBlock? found = null;
        foreach (var predecessor in predecessors)
        {
            int index = IndexOf(blocks, predecessor) + 1;
            if (index < blocks.Count && loop.Contains(blocks[index])) { found = predecessor; break; }
        }
        split.MoveAfter(found ?? predecessors[0]); trace?.Metric("cfg.blocks_repositioned", 1);
    }
    private static int IndexOf(IReadOnlyList<LlvmBasicBlock> blocks, LlvmBasicBlock block)
    {
        for (int i = 0; i < blocks.Count; ++i) if (ReferenceEquals(blocks[i], block)) return i;
        return -1;
    }
}
