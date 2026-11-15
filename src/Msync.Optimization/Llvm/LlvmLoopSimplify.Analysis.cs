// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// BasicBlockUtils.cpp UpdateAnalysisInformation, DT/DTU paths with optional MSSAU.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    private static bool UpdateAnalysisInformation(LlvmBasicBlock original, LlvmBasicBlock split,
        IReadOnlyList<LlvmBasicBlock> predecessors, LlvmDominatorTree? tree, LlvmLoopInfo? loops,
        bool preserveLcssa, CompilationTrace? trace, LlvmMemorySsaUpdater? updater = null,
        LlvmDomTreeUpdater? dominatorUpdater = null)
    {
        if (dominatorUpdater is not null)
        {
            if (ReferenceEquals(split.Parent!.BasicBlocks.First(), split) && dominatorUpdater.HasTree)
                dominatorUpdater.Recalculate(split.Parent);
            else
            {
                List<LlvmCfgUpdate> updates = [new(LlvmCfgUpdateKind.Insert, split, original)];
                var unique = new HashSet<LlvmBasicBlock>();
                foreach (var predecessor in predecessors)
                {
                    if (!unique.Add(predecessor)) continue;
                    updates.Add(new(LlvmCfgUpdateKind.Insert, predecessor, split));
                    updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, original));
                }
                dominatorUpdater.ApplyUpdates(updates);
            }
            trace?.Metric("analysis.dominator_updater_splits", 1);
        }
        else if (tree is not null)
        {
            if (ReferenceEquals(original, tree.RootNode!.Block)) tree.SetNewRoot(split);
            else tree.SplitBlock(split);
            trace?.Metric("analysis.dominator_splits", 1);
        }
        if (updater is not null)
        {
            updater.WireOldPredecessorsToNewImmediatePredecessor(original, split, predecessors);
            trace?.Metric("analysis.memory_ssa_predecessors_wired", 1);
        }
        if (loops is null) return false;
        if (dominatorUpdater?.HasTree == true) tree = dominatorUpdater.GetTree();
        Debug.Assert(tree is not null, "DT should be available to update LoopInfo!");
        var loop = loops.GetLoopFor(original);
        bool loopEntry = loop is not null, makesNewHeader = false, hasLoopExit = false;
        foreach (var predecessor in predecessors)
        {
            if (!tree!.IsReachableFromEntry(predecessor)) continue;
            if (preserveLcssa && loops.GetLoopFor(predecessor) is { } predecessorLoop && !predecessorLoop.Contains(original))
                hasLoopExit = true;
            if (loop is null) continue;
            if (loop.Contains(predecessor)) loopEntry = false;
            else makesNewHeader = true;
        }
        if (loop is null) return hasLoopExit;
        if (loopEntry)
        {
            LlvmLoop? innermost = null;
            foreach (var predecessor in predecessors)
            {
                var parent = loops.GetLoopFor(predecessor);
                while (parent is not null && !parent.Contains(original)) parent = parent.ParentLoop;
                if (parent is not null && parent.Contains(original) && (innermost is null || innermost.LoopDepth < parent.LoopDepth))
                    innermost = parent;
            }
            innermost?.AddBasicBlockToLoop(split, loops);
        }
        else
        {
            loop.AddBasicBlockToLoop(split, loops);
            if (makesNewHeader)
            {
                if (HasReachableLoopEntryToHeader(loop, tree!)) loops.Erase(loop);
                else loop.MoveToHeader(split);
            }
        }
        trace?.Metric("analysis.loop_info_splits", 1);
        return hasLoopExit;
    }
    private static bool HasReachableLoopEntryToHeader(LlvmLoop loop, LlvmDominatorTree tree)
    {
        foreach (var predecessor in LlvmDominatorTree.Predecessors(loop.Header))
            if (!loop.Contains(predecessor) && tree.IsReachableFromEntry(predecessor)) return true;
        return false;
    }
}
