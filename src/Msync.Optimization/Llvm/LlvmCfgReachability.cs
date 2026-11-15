// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/CFG.h and lib/Analysis/CFG.cpp: isReachableImpl (single stop),
// isPotentiallyReachableFromMany and block/instruction isPotentiallyReachable.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static class LlvmCfgReachability
{
    private const uint DefaultMaxBBsToExplore = 32;

    /// <summary>Consumes the source worklist; returns true when the source exploration budget expires.</summary>
    public static bool IsPotentiallyReachableFromMany(List<LlvmBasicBlock> worklist, LlvmBasicBlock target,
        IReadOnlySet<LlvmBasicBlock>? exclusion = null, LlvmDominatorTree? tree = null,
        LlvmLoopInfo? loops = null, Dictionary<string, ulong>? metrics = null, LlvmCycleInfo? cycles = null)
    {
        Debug.Assert(worklist.Count != 0);
        Count(metrics, "cfg.reachability.queries");
        if (cycles is not null) loops = null;
        if (tree is not null && !tree.IsReachableFromEntry(target))
        { tree = null; Count(metrics, "cfg.reachability.dt.unreachable_target"); }
        if (exclusion is { Count: > 0 })
        {
            if (tree is not null) Count(metrics, "cfg.reachability.dt.exclusion_disabled");
            tree = null;
        }
        HashSet<LlvmLoop>? loopsWithHoles = null;
        if (loops is not null && exclusion is not null)
        {
            foreach (var block in exclusion)
            {
                Count(metrics, "cfg.reachability.exclusions.visited");
                if (loops.GetLoopFor(block)?.OutermostLoop is not { } loop) continue;
                (loopsWithHoles ??= new(ReferenceEqualityComparer.Instance)).Add(loop);
            }
        }
        HashSet<LlvmCycle>? cyclesWithHoles = null;
        if (cycles is not null && exclusion is not null)
        {
            foreach (var block in exclusion)
                if (cycles.GetTopLevelParentCycle(block) is { } cycle)
                    (cyclesWithHoles ??= new(ReferenceEqualityComparer.Instance)).Add(cycle);
        }
        LlvmCycle? stopCycle = cycles?.GetTopLevelParentCycle(target);
        LlvmLoop? stopLoop = loops?.GetLoopFor(target)?.OutermostLoop;
        uint limit = DefaultMaxBBsToExplore;
        HashSet<LlvmBasicBlock> visited = new(ReferenceEqualityComparer.Instance);
        do
        {
            var block = worklist[^1];
            worklist.RemoveAt(worklist.Count - 1);
            Count(metrics, "cfg.reachability.worklist.popped");
            if (!visited.Add(block)) { Count(metrics, "cfg.reachability.visited.duplicate"); continue; }
            if (ReferenceEquals(block, target)) { Count(metrics, "cfg.reachability.target.reached"); return true; }
            if (exclusion?.Contains(block) == true) { Count(metrics, "cfg.reachability.excluded"); continue; }
            if (tree is not null && tree.Dominates(block, target))
            { Count(metrics, "cfg.reachability.dt.shortcut"); return true; }
            LlvmLoop? outer = loops?.GetLoopFor(block)?.OutermostLoop;
            if (outer is not null)
            {
                if (loopsWithHoles?.Contains(outer) == true)
                { outer = null; Count(metrics, "cfg.reachability.loop.hole"); }
                else if (ReferenceEquals(outer, stopLoop))
                { Count(metrics, "cfg.reachability.loop.target_shortcut"); return true; }
            }
            LlvmCycle? outerCycle = cycles?.GetTopLevelParentCycle(block);
            if (cycles is not null)
            {
                if (outerCycle is not null)
                {
                    if (cyclesWithHoles?.Contains(outerCycle) == true)
                    { outerCycle = null; Count(metrics, "cfg.reachability.cycle.hole"); }
                    else if (ReferenceEquals(outerCycle, stopCycle))
                    { Count(metrics, "cfg.reachability.cycle.target_shortcut"); return true; }
                }
                else if (tree is not null && tree.IsReachableFromEntry(block) && tree.Dominates(target, block))
                { Count(metrics, "cfg.reachability.cycle.acyclic_backedge_pruned"); continue; }
            }
            if (--limit == 0) { Count(metrics, "cfg.reachability.limit_reached"); return true; }
            Count(metrics, "cfg.reachability.blocks.expanded");
            if (outer is not null) AppendLoopExits(outer, worklist, metrics);
            else if (outerCycle is not null)
            {
                int before = worklist.Count;
                outerCycle.GetExitBlocks(worklist);
                Count(metrics, "cfg.reachability.cycle.exit_walks");
                CountBy(metrics, "cfg.reachability.cycle.exit_edges", (ulong)(worklist.Count - before));
            }
            else
            {
                foreach (var successor in LlvmDominatorTree.Successors(block))
                { worklist.Add(successor); Count(metrics, "cfg.reachability.successor_edges"); }
            }
        } while (worklist.Count != 0);
        Count(metrics, "cfg.reachability.paths_exhausted");
        return false;
    }

    public static bool IsPotentiallyReachable(LlvmBasicBlock from, LlvmBasicBlock to,
        IReadOnlySet<LlvmBasicBlock>? exclusion = null, LlvmDominatorTree? tree = null,
        LlvmLoopInfo? loops = null, Dictionary<string, ulong>? metrics = null, LlvmCycleInfo? cycles = null)
    {
        Debug.Assert(ReferenceEquals(from.Parent, to.Parent), "Reachability analysis is function-local");
        if (tree is not null)
        {
            if (tree.IsReachableFromEntry(from) && !tree.IsReachableFromEntry(to))
            { Count(metrics, "cfg.reachability.dt.unreachable_destination"); return false; }
            if (exclusion is null || exclusion.Count == 0)
            {
                if (ReferenceEquals(from, from.Parent!.BasicBlocks[0]) && tree.IsReachableFromEntry(to))
                { Count(metrics, "cfg.reachability.dt.entry_source"); return true; }
                if (ReferenceEquals(to, to.Parent!.BasicBlocks[0]) && tree.IsReachableFromEntry(from))
                { Count(metrics, "cfg.reachability.dt.entry_destination"); return false; }
            }
        }
        return IsPotentiallyReachableFromMany([from], to, exclusion, tree, loops, metrics, cycles);
    }

    public static bool IsPotentiallyReachable(LlvmInstruction from, LlvmInstruction to,
        IReadOnlySet<LlvmBasicBlock>? exclusion = null, LlvmDominatorTree? tree = null,
        LlvmLoopInfo? loops = null, Dictionary<string, ulong>? metrics = null, LlvmCycleInfo? cycles = null)
    {
        Debug.Assert(ReferenceEquals(from.Parent!.Parent, to.Parent!.Parent), "Reachability analysis is function-local");
        if (!ReferenceEquals(from.Parent, to.Parent))
            return IsPotentiallyReachable(from.Parent!, to.Parent!, exclusion, tree, loops, metrics, cycles);
        var block = from.Parent!;
        if (ReferenceEquals(from, to) || from.ComesBefore(to))
        { Count(metrics, "cfg.reachability.instruction.forward"); return true; }
        if (exclusion is null || exclusion.Count == 0)
        {
            if (cycles is not null)
            {
                Count(metrics, "cfg.reachability.instruction.cycle_query");
                return cycles.GetCycle(block) is not null;
            }
            if (loops?.GetLoopFor(block) is not null)
            { Count(metrics, "cfg.reachability.instruction.loop"); return true; }
        }
        if (ReferenceEquals(block, block.Parent!.BasicBlocks[0]))
        { Count(metrics, "cfg.reachability.instruction.entry_backwards"); return false; }
        var worklist = LlvmDominatorTree.Successors(block).ToList();
        if (worklist.Count == 0) { Count(metrics, "cfg.reachability.instruction.no_successors"); return false; }
        return IsPotentiallyReachableFromMany(worklist, block, exclusion, tree, loops, metrics, cycles);
    }

    // GenericLoopInfoImpl.h getExitBlocks appends successor edges, including duplicates.
    private static void AppendLoopExits(LlvmLoop loop, List<LlvmBasicBlock> worklist, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "cfg.reachability.loop.exit_walks");
        foreach (var block in loop.Blocks)
        {
            foreach (var successor in LlvmDominatorTree.Successors(block))
            {
                Count(metrics, "cfg.reachability.loop.successor_edges");
                if (loop.Contains(successor)) continue;
                worklist.Add(successor);
                Count(metrics, "cfg.reachability.loop.exit_edges");
            }
        }
    }

    private static void CountBy(Dictionary<string, ulong>? metrics, string name, ulong count)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + count; }

    private static void Count(Dictionary<string, ulong>? metrics, string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
