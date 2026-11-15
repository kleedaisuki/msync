// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: GenericDomTreeUpdaterImpl.h.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDomTreeUpdater
{
    public void Recalculate(LlvmFunction function)
    {
        if (trace is null) { RecalculateCore(function); return; }
        trace.Run("optimizer.domtree-updater.recalculate", (Updater: this, Function: function),
            static state => state.Updater.RecalculateCore(state.Function), Attributes());
    }

    private void RecalculateCore(LlvmFunction function)
    {
        Count("recalculations");
        if (IsEager)
        {
            tree?.Recalculate(function, metrics);
            postTree?.Recalculate(function, metrics);
            return;
        }
        recalculatingTree = recalculatingPostTree = true;
        ForceFlushDeletedBlocks();
        tree?.Recalculate(function, metrics);
        postTree?.Recalculate(function, metrics);
        recalculatingTree = recalculatingPostTree = false;
        treeIndex = postTreeIndex = pending.Count;
        DropOutOfDateUpdates();
    }

    private void ApplyPendingUpdates(bool forward)
    {
        var target = forward ? tree : postTree;
        if (!IsLazy || target is null) return;
        int index = forward ? treeIndex : postTreeIndex;
        while (index != pending.Count)
        {
            if (pending[index].Split is null)
            {
                List<LlvmCfgUpdate> updates = [];
                do { updates.Add(pending[index++].Update); }
                while (index != pending.Count && pending[index].Split is null);
                target.ApplyUpdates(updates, metrics: metrics);
                Count(forward ? "forward.normal_batches" : "post.normal_batches");
                Count(forward ? "forward.updates_applied" : "post.updates_applied", (ulong)updates.Count);
            }
            else
            {
                List<CriticalEdge> edges = [];
                do { edges.Add(pending[index++].Split!.Value); }
                while (index != pending.Count && pending[index].Split is not null);
                if (forward) SplitTreeCriticalEdges(edges);
                else SplitPostTreeCriticalEdges(edges);
                Count(forward ? "forward.critical_batches" : "post.critical_batches");
            }
        }
        if (forward) treeIndex = index;
        else postTreeIndex = index;
    }

    private void DropOutOfDateUpdates()
    {
        if (IsEager) return;
        if (!HasPendingUpdates) ForceFlushDeletedBlocks();
        if (tree is null) treeIndex = pending.Count;
        if (postTree is null) postTreeIndex = pending.Count;
        int dropIndex = Math.Min(treeIndex, postTreeIndex);
        Debug.Assert(dropIndex <= pending.Count);
        pending.RemoveRange(0, dropIndex);
        treeIndex -= dropIndex;
        postTreeIndex -= dropIndex;
        Count("updates.retired", (ulong)dropIndex);
    }

    private void SplitTreeCriticalEdges(IReadOnlyList<CriticalEdge> edges)
    {
        if (tree is null || edges.Count == 0) return;
        HashSet<LlvmBasicBlock> newBlocks = new(ReferenceEqualityComparer.Instance);
        foreach (var edge in edges) newBlocks.Add(edge.NewBlock);
        bool[] newIDom = new bool[edges.Count];
        Array.Fill(newIDom, true);
        for (int index = 0; index < edges.Count; ++index)
        {
            var edge = edges[index];
            var successorNode = tree.GetNode(edge.To);
            foreach (var predecessor in LlvmDominatorTree.Predecessors(edge.To))
            {
                if (ReferenceEquals(predecessor, edge.NewBlock)) continue;
                var previous = predecessor;
                if (newBlocks.Contains(previous))
                {
                    Debug.Assert(LlvmDominatorTree.Predecessors(previous).Count() == 1);
                    previous = LlvmDominatorTree.Predecessors(previous).First();
                }
                Count("critical_splits.dominance_queries");
                if (tree.Dominates(successorNode, tree.GetNode(previous))) continue;
                newIDom[index] = false;
                break;
            }
        }
        for (int index = 0; index < edges.Count; ++index)
        {
            var edge = edges[index];
            var newNode = tree.AddNewBlock(edge.NewBlock, edge.From, metrics);
            if (newIDom[index]) tree.ChangeImmediateDominator(tree.GetNode(edge.To)!, newNode, metrics);
            Count("critical_splits.forward_applied");
        }
    }

    private void SplitPostTreeCriticalEdges(IReadOnlyList<CriticalEdge> edges)
    {
        if (postTree is null || edges.Count == 0) return;
        List<LlvmCfgUpdate> updates = new(edges.Count * 3);
        foreach (var edge in edges)
        {
            updates.Add(new(LlvmCfgUpdateKind.Insert, edge.From, edge.NewBlock));
            updates.Add(new(LlvmCfgUpdateKind.Insert, edge.NewBlock, edge.To));
            if (!LlvmDominatorTree.Successors(edge.From).Contains(edge.To))
                updates.Add(new(LlvmCfgUpdateKind.Delete, edge.From, edge.To));
            Count("critical_splits.post_applied");
        }
        postTree.ApplyUpdates(updates, metrics: metrics);
    }
}
