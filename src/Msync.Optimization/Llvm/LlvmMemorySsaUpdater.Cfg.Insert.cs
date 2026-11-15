// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp applyInsertUpdates. CFG child delegates are the
// managed adapter of the source GraphDiff<BasicBlock *>::getChildren interface.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    private sealed class OrderedBlocks
    {
        internal readonly List<LlvmBasicBlock> Values = [];
        private readonly HashSet<LlvmBasicBlock> set = [];
        internal int Count => Values.Count;
        internal bool Contains(LlvmBasicBlock block) => set.Contains(block);
        internal void Add(LlvmBasicBlock block) { if (set.Add(block)) Values.Add(block); }
    }
    private sealed class PredInfo
    {
        internal readonly OrderedBlocks Added = new();
        internal readonly OrderedBlocks Previous = new();
    }

    private LlvmMemoryAccess LastDefForCfg(LlvmBasicBlock block, LlvmDominatorTree tree,
        Func<LlvmBasicBlock, IEnumerable<LlvmBasicBlock>> predecessors)
    {
        while (true)
        {
            if (MemorySsa.GetBlockDefs(block)?.Last is { } last) return last;
            using var iterator = predecessors(block).GetEnumerator();
            bool one = iterator.MoveNext();
            var predecessor = one ? iterator.Current : null;
            if (one && iterator.MoveNext()) one = false;
            var node = tree.GetNode(block);
            if (node is null) return MemorySsa.LiveOnEntryDef;
            if (one) { block = predecessor!; continue; }
            if (node.IDom?.Block is { } idom && !ReferenceEquals(idom, block)) { block = idom; continue; }
            return MemorySsa.LiveOnEntryDef;
        }
    }

    public void ApplyInsertUpdates(IReadOnlyList<(LlvmBasicBlock From, LlvmBasicBlock To)> updates, LlvmDominatorTree tree) =>
        ApplyInsertUpdates(updates, tree, LlvmDominatorTree.Predecessors, LlvmDominatorTree.Successors);

    private void ApplyInsertUpdates(IReadOnlyList<(LlvmBasicBlock From, LlvmBasicBlock To)> updates, LlvmDominatorTree tree,
        Func<LlvmBasicBlock, IEnumerable<LlvmBasicBlock>> predecessors,
        Func<LlvmBasicBlock, IEnumerable<LlvmBasicBlock>> successors)
    {
        var predMap = new Dictionary<LlvmBasicBlock, PredInfo>();
        foreach (var edge in updates)
        {
            if (!predMap.TryGetValue(edge.To, out var info)) predMap.Add(edge.To, info = new());
            info.Added.Add(edge.From);
        }
        var edgeCounts = new Dictionary<(LlvmBasicBlock From, LlvmBasicBlock To), int>();
        var newBlocks = new List<LlvmBasicBlock>();
        foreach (var (block, info) in predMap)
        {
            foreach (var predecessor in predecessors(block))
            {
                if (!info.Added.Contains(predecessor)) info.Previous.Add(predecessor);
                var edge = (predecessor, block); edgeCounts[edge] = edgeCounts.GetValueOrDefault(edge) + 1;
            }
            if (info.Previous.Count != 0) continue;
            Debug.Assert(Predecessors(block).Length == info.Added.Count && info.Added.Count == 1);
            newBlocks.Add(block);
        }
        foreach (var block in newBlocks) predMap.Remove(block);
        var blocksWithDefsToReplace = new List<LlvmBasicBlock>();
        var phis = new List<LlvmMemoryPhi>();
        foreach (var edge in updates)
            if (predMap.ContainsKey(edge.To) && MemorySsa.GetMemoryAccess(edge.To) is null)
            { phis.Add(MemorySsa.CreateMemoryPhi(edge.To)); Count("cfg.insert.phis"); }
        foreach (var (block, info) in predMap)
        {
            Debug.Assert(info.Previous.Count != 0);
            var lastAdded = new Dictionary<LlvmBasicBlock, LlvmMemoryAccess>();
            foreach (var predecessor in info.Added.Values) lastAdded.Add(predecessor, LastDefForCfg(predecessor, tree, predecessors));
            var phi = MemorySsa.GetMemoryAccess(block)!;
            if (phi.IncomingCount != 0)
            {
                foreach (var predecessor in info.Added.Values)
                    for (int edge = 0; edge < edgeCounts[(predecessor, block)]; ++edge) phi.AddIncoming(lastAdded[predecessor], predecessor);
            }
            else
            {
                var oldDefinition = LastDefForCfg(info.Previous.Values[0], tree, predecessors);
                bool insert = lastAdded.Values.Any(definition => !ReferenceEquals(definition, oldDefinition));
                if (!insert)
                { phi.ReplaceTrackedUsesWith(oldDefinition); RemoveMemoryAccess(phi); continue; }
                foreach (var predecessor in info.Added.Values)
                    for (int edge = 0; edge < edgeCounts[(predecessor, block)]; ++edge) phi.AddIncoming(lastAdded[predecessor], predecessor);
                foreach (var predecessor in info.Previous.Values)
                    for (int edge = 0; edge < edgeCounts[(predecessor, block)]; ++edge) phi.AddIncoming(oldDefinition, predecessor);
            }
            var previousIdom = info.Previous.Values[0];
            foreach (var predecessor in info.Previous.Values) previousIdom = tree.FindNearestCommonDominator(previousIdom, predecessor)!;
            var currentIdom = tree.GetNode(block)!.IDom!.Block!;
            Debug.Assert(tree.Dominates(currentIdom, previousIdom));
            for (var noLongerDominating = previousIdom; !ReferenceEquals(noLongerDominating, currentIdom);
                noLongerDominating = tree.GetNode(noLongerDominating)!.IDom!.Block!) blocksWithDefsToReplace.Add(noLongerDominating);
        }
        TryRemoveTrivialPhis(phis);
        var defining = phis.Select(WeakPhi).Where(phi => phi is not null).Select(phi => phi!.Block).ToHashSet();
        if (defining.Count != 0)
        {
            var idf = new LlvmForwardIdfCalculator(tree, MemorySsa.Metrics) { DefiningBlocks = defining, SuccessorBlocks = successors };
            var frontier = new List<LlvmBasicBlock>(); idf.Calculate(frontier);
            var phisToFill = new HashSet<LlvmMemoryPhi>();
            foreach (var block in frontier)
                if (MemorySsa.GetMemoryAccess(block) is null)
                {
                    var phi = MemorySsa.CreateMemoryPhi(block); phis.Add(phi); phisToFill.Add(phi);
                    Count("cfg.insert.idf_phis");
                }
            foreach (var block in frontier)
            {
                var phi = MemorySsa.GetMemoryAccess(block)!;
                if (phisToFill.Contains(phi))
                    foreach (var predecessor in predecessors(block)) phi.AddIncoming(LastDefForCfg(predecessor, tree, predecessors), predecessor);
                else
                    for (int index = 0; index < phi.IncomingCount; ++index)
                        phi.SetIncomingValue(index, LastDefForCfg(phi.GetIncomingBlock(index), tree, predecessors));
            }
        }
        foreach (var block in blocksWithDefsToReplace)
        {
            if (MemorySsa.GetBlockDefs(block) is not { } definitions) continue;
            foreach (var definition in definitions)
            {
                var resetOptimized = new List<LlvmMemoryUseOrDef>();
                foreach (var use in definition.Uses.ToArray())
                {
                    var user = (LlvmMemoryAccess)use.User;
                    if (user is LlvmMemoryPhi phi)
                    {
                        var incoming = phi.GetIncomingBlock(use);
                        if (!tree.Dominates(definition.Block, incoming)) use.Set(LastDefForCfg(incoming, tree, predecessors));
                    }
                    else if (!tree.Dominates(definition.Block, user.Block))
                    {
                        use.Set(MemorySsa.GetMemoryAccess(user.Block) ?? LastDefForCfg(tree.GetNode(user.Block)!.IDom!.Block!, tree, predecessors));
                        resetOptimized.Add((LlvmMemoryUseOrDef)user); Count("cfg.insert.relinked_uses");
                    }
                }
                foreach (var access in resetOptimized) access.ResetOptimized();
            }
        }
        TryRemoveTrivialPhis(phis);
    }

    public void UpdateExitBlocksForClonedLoop(IReadOnlyList<LlvmBasicBlock> exitBlocks,
        IReadOnlyDictionary<LlvmValue, LlvmValue> valueMap, LlvmDominatorTree tree) =>
        UpdateExitBlocksForClonedLoop(exitBlocks, [valueMap], tree);

    public void UpdateExitBlocksForClonedLoop(IReadOnlyList<LlvmBasicBlock> exitBlocks,
        IReadOnlyList<IReadOnlyDictionary<LlvmValue, LlvmValue>> valueMaps, LlvmDominatorTree tree)
    {
        var updates = new List<(LlvmBasicBlock From, LlvmBasicBlock To)>();
        foreach (var exit in exitBlocks)
            foreach (var map in valueMaps)
                if (map.GetValueOrDefault(exit) is LlvmBasicBlock clone) updates.Add((clone, clone.GetTerminator().GetSuccessor(0)));
        ApplyInsertUpdates(updates, tree);
    }
}
