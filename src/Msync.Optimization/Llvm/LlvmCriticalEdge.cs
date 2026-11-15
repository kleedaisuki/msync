// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// BasicBlockUtils.h options; BreakCriticalEdges.cpp; CFG.cpp isCriticalEdge.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmCriticalEdgeSplittingOptions(LlvmDominatorTree? tree = null,
    LlvmLoopInfo? loops = null, LlvmMemorySsaUpdater? memoryUpdater = null,
    LlvmDominatorTree? postTree = null)
{
    public LlvmDominatorTree? DominatorTree { get; } = tree;
    public LlvmDominatorTree? PostDominatorTree { get; } = postTree;
    public LlvmLoopInfo? LoopInfo { get; } = loops;
    public LlvmMemorySsaUpdater? MemoryUpdater { get; } = memoryUpdater;
    public bool MergeIdenticalEdges { get; set; }
    public bool KeepOneInputPHIs { get; set; }
    public bool PreserveLCSSA { get; set; }
    public bool IgnoreUnreachableDests { get; set; }
    public bool PreserveLoopSimplify { get; set; } = true;
    public LlvmCriticalEdgeSplittingOptions SetMergeIdenticalEdges() { MergeIdenticalEdges = true; return this; }
    public LlvmCriticalEdgeSplittingOptions SetKeepOneInputPHIs() { KeepOneInputPHIs = true; return this; }
    public LlvmCriticalEdgeSplittingOptions SetPreserveLCSSA() { PreserveLCSSA = true; return this; }
    public LlvmCriticalEdgeSplittingOptions SetIgnoreUnreachableDests() { IgnoreUnreachableDests = true; return this; }
    public LlvmCriticalEdgeSplittingOptions UnsetPreserveLoopSimplify() { PreserveLoopSimplify = false; return this; }
}

public static partial class LlvmBasicBlockUtils
{
    public static bool IsCriticalEdge(LlvmInstruction terminator, int successorIndex, bool allowIdenticalEdges = false)
    {
        Debug.Assert((uint)successorIndex < (uint)terminator.SuccessorCount);
        if (terminator.SuccessorCount == 1) return false;
        var destination = terminator.GetSuccessor(successorIndex);
        using var predecessors = LlvmDominatorTree.Predecessors(destination).GetEnumerator();
        bool found = predecessors.MoveNext();
        Debug.Assert(found);
        var first = predecessors.Current;
        if (!allowIdenticalEdges) return predecessors.MoveNext();
        while (predecessors.MoveNext())
            if (!ReferenceEquals(predecessors.Current, first)) return true;
        return false;
    }

    public static LlvmBasicBlock? SplitCriticalEdge(LlvmInstruction terminator, int successorIndex,
        LlvmCriticalEdgeSplittingOptions? options = null, string blockName = "", CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null)
    {
        options ??= new();
        CriticalCount(metrics, "critical_edge.queries");
        if (!IsCriticalEdge(terminator, successorIndex, options.MergeIdenticalEdges))
        { CriticalCount(metrics, "critical_edge.not_critical"); return null; }
        return SplitKnownCriticalEdge(terminator, successorIndex, options, blockName, trace, metrics);
    }

    public static LlvmBasicBlock? SplitCriticalEdge(LlvmBasicBlock source, LlvmBasicBlock destination,
        LlvmCriticalEdgeSplittingOptions? options = null, CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null)
    {
        var terminator = source.GetTerminator();
        int index = 0;
        while (!ReferenceEquals(terminator.GetSuccessor(index), destination)) ++index;
        return SplitCriticalEdge(terminator, index, options, trace: trace, metrics: metrics);
    }

    public static LlvmBasicBlock? SplitKnownCriticalEdge(LlvmInstruction terminator, int successorIndex,
        LlvmCriticalEdgeSplittingOptions? options = null, string blockName = "", CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null)
    {
        options ??= new();
        var block = terminator.Parent!;
        var destination = terminator.GetSuccessor(successorIndex);
        if (FirstNonPhiForCriticalEdge(destination).IsEHPad || terminator.Opcode == LlvmOpcode.IndirectBr) return null;
        if (options.IgnoreUnreachableDests && FirstNonPhiForCriticalEdge(destination) is LlvmUnreachableInstruction)
        { CriticalCount(metrics, "critical_edge.unreachable_destination"); return null; }
        var loops = options.LoopInfo;
        List<LlvmBasicBlock> loopPredecessors = [];
        if (loops?.GetLoopFor(block) is { } sourceLoop)
        {
            foreach (var predecessor in LlvmDominatorTree.Predecessors(destination))
            {
                if (ReferenceEquals(predecessor, block)) continue;
                if (!ReferenceEquals(loops.GetLoopFor(predecessor), sourceLoop))
                { loopPredecessors.Clear(); break; }
                loopPredecessors.Add(predecessor);
            }
            if (loopPredecessors.Any(predecessor => predecessor.GetTerminator().Opcode == LlvmOpcode.IndirectBr))
            {
                if (options.PreserveLoopSimplify) return null;
                loopPredecessors.Clear();
            }
        }
        string name = blockName.Length != 0 ? blockName : block.Name + "." + destination.Name + "_crit_edge";
        var split = new LlvmBasicBlock(block.Context, name, block.Parent);
        split.MoveAfter(block);
        split.Append(new LlvmUncondBrInstruction(destination) { Source = terminator.Source });
        terminator.SetSuccessor(successorIndex, split);
        int previousIndex = 0;
        foreach (var phi in CriticalPhis(destination))
        {
            if (!ReferenceEquals(phi.GetIncomingBlock(previousIndex), block)) previousIndex = phi.GetBasicBlockIndex(block);
            phi.SetIncomingBlock(previousIndex, split);
            CriticalCount(metrics, "critical_edge.phi_blocks_redirected");
        }
        int splitIdenticalEdges = 1;
        if (options.MergeIdenticalEdges)
        {
            for (int index = successorIndex + 1; index < terminator.SuccessorCount; ++index)
            {
                if (!ReferenceEquals(terminator.GetSuccessor(index), destination)) continue;
                RemovePredecessor(destination, block, options.KeepOneInputPHIs, trace, metrics);
                terminator.SetSuccessor(index, split);
                ++splitIdenticalEdges;
                CriticalCount(metrics, "critical_edge.identical_edges_merged");
            }
        }
        options.MemoryUpdater?.WireOldPredecessorsToNewImmediatePredecessor(destination, split, [block], options.MergeIdenticalEdges);
        var tree = options.DominatorTree;
        var postTree = options.PostDominatorTree;
        if (tree is null && postTree is null && loops is null)
        { CriticalCount(metrics, "critical_edge.blocks_inserted"); return split; }
        if (tree is not null || postTree is not null)
        {
            List<LlvmCfgUpdate> updates =
            [new(LlvmCfgUpdateKind.Insert, block, split), new(LlvmCfgUpdateKind.Insert, split, destination)];
            if (!terminator.Successors.Contains(destination)) updates.Add(new(LlvmCfgUpdateKind.Delete, block, destination));
            tree?.ApplyUpdates(updates, metrics: metrics);
            postTree?.ApplyUpdates(updates, metrics: metrics);
        }
        if (loops?.GetLoopFor(block) is { } fromLoop)
        {
            AddSplitToLoop(loops, fromLoop, destination, split);
            if (!fromLoop.Contains(destination))
            {
                Debug.Assert(!fromLoop.Contains(split));
                if (options.PreserveLCSSA)
                    CreatePHIsForSplitLoopExit(Enumerable.Repeat(block, splitIdenticalEdges).ToArray(), split, destination, metrics);
                if (loopPredecessors.Count != 0)
                {
                    var exit = SplitBlockPredecessors(destination, loopPredecessors, "split", tree, loops,
                        options.MemoryUpdater, options.PreserveLCSSA, trace, metrics);
                    if (options.PreserveLCSSA) CreatePHIsForSplitLoopExit(loopPredecessors, exit, destination, metrics);
                    CriticalCount(metrics, "critical_edge.loop_exits_resplit");
                }
            }
        }
        CriticalCount(metrics, "critical_edge.blocks_inserted");
        return split;
    }

    private static void AddSplitToLoop(LlvmLoopInfo loops, LlvmLoop fromLoop, LlvmBasicBlock destination, LlvmBasicBlock split)
    {
        var destinationLoop = loops.GetLoopFor(destination);
        if (destinationLoop is null) return;
        if (ReferenceEquals(fromLoop, destinationLoop)) destinationLoop.AddBasicBlockToLoop(split, loops);
        else if (fromLoop.Contains(destinationLoop)) fromLoop.AddBasicBlockToLoop(split, loops);
        else if (destinationLoop.Contains(fromLoop)) destinationLoop.AddBasicBlockToLoop(split, loops);
        else
        {
            Debug.Assert(ReferenceEquals(destinationLoop.Header, destination));
            destinationLoop.ParentLoop?.AddBasicBlockToLoop(split, loops);
        }
    }

    public static void CreatePHIsForSplitLoopExit(IReadOnlyList<LlvmBasicBlock> predecessors,
        LlvmBasicBlock split, LlvmBasicBlock destination, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(ReferenceEquals(FirstNonPhiForCriticalEdge(split), split.GetTerminator()));
        foreach (var phi in CriticalPhis(destination))
        {
            int index = phi.GetBasicBlockIndex(split);
            Debug.Assert(index >= 0);
            var incoming = phi.GetIncomingValue(index);
            if (incoming is LlvmPhiInstruction local && ReferenceEquals(local.Parent, split)) continue;
            var joined = new LlvmPhiInstruction(phi.Type, predecessors.Count)
            { LogicalType = phi.LogicalType, Source = phi.Source };
            joined.SetName("split");
            split.InsertBefore(joined, split.GetTerminator());
            int logicalType = phi.GetOperandUse(index).LogicalType;
            foreach (var predecessor in predecessors)
            {
                joined.AddIncoming(incoming, predecessor);
                joined.GetOperandUse(joined.IncomingCount - 1).LogicalType = logicalType;
            }
            phi.SetIncomingValue(index, joined);
            CriticalCount(metrics, "critical_edge.lcssa_phis_created");
        }
    }

    public static int SplitAllCriticalEdges(LlvmFunction function, LlvmCriticalEdgeSplittingOptions? options = null,
        CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        int broken = 0;
        foreach (var block in function.BasicBlocks.ToArray())
        {
            var terminator = block.GetTerminator();
            if (terminator.SuccessorCount <= 1 || terminator.Opcode == LlvmOpcode.IndirectBr) continue;
            for (int index = 0; index < terminator.SuccessorCount; ++index)
                if (SplitCriticalEdge(terminator, index, options, trace: trace, metrics: metrics) is not null) ++broken;
        }
        return broken;
    }

    private static LlvmInstruction FirstNonPhiForCriticalEdge(LlvmBasicBlock block)
    {
        var instruction = block.FirstInstruction!;
        while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
        return instruction;
    }

    private static IEnumerable<LlvmPhiInstruction> CriticalPhis(LlvmBasicBlock block)
    {
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            yield return phi;
    }

    private static void CriticalCount(Dictionary<string, ulong>? metrics, string name)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}
