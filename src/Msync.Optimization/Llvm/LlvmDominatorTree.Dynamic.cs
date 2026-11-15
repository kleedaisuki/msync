// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// GenericDomTreeConstruction.h: batch views, ApplyUpdates and source rebuild selection.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private sealed class BatchUpdateInfo(LlvmCfgDiff preView, LlvmCfgDiff? postView = null)
    {
        internal LlvmCfgDiff PreView = preView;
        internal readonly LlvmCfgDiff? PostView = postView;
        internal readonly int NumLegalized = preView.NumLegalizedUpdates;
        internal bool IsRecalculated;
    }

    public void ApplyUpdates(IReadOnlyList<LlvmCfgUpdate> updates,
        IReadOnlyList<LlvmCfgUpdate>? postViewUpdates = null, Dictionary<string, ulong>? metrics = null)
    {
        if (postViewUpdates is null)
        {
            ApplyViewUpdates(new(updates, reverseApplyUpdates: true, inverseGraph: IsPostDominator, metrics: metrics), null, metrics);
            return;
        }
        var postView = new LlvmCfgDiff(postViewUpdates, inverseGraph: IsPostDominator, metrics: metrics);
        if (updates.Count == 0) { ApplyViewUpdates(postView, postView, metrics); return; }
        List<LlvmCfgUpdate> allUpdates = new(updates.Count + postViewUpdates.Count);
        allUpdates.AddRange(updates); allUpdates.AddRange(postViewUpdates);
        ApplyViewUpdates(new(allUpdates, reverseApplyUpdates: true, inverseGraph: IsPostDominator, metrics: metrics), postView, metrics);
    }

    public void InsertEdge(LlvmBasicBlock from, LlvmBasicBlock to, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(ReferenceEquals(from.Parent, Function) && ReferenceEquals(to.Parent, Function));
        if (IsPostDominator) (from, to) = (to, from);
        DynamicInsertEdge(null, from, to, metrics);
    }

    public void DeleteEdge(LlvmBasicBlock from, LlvmBasicBlock to, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(ReferenceEquals(from.Parent, Function) && ReferenceEquals(to.Parent, Function));
        if (IsPostDominator) (from, to) = (to, from);
        DynamicDeleteEdge(null, from, to, metrics);
    }

    private void ApplyViewUpdates(LlvmCfgDiff preView, LlvmCfgDiff? postView, Dictionary<string, ulong>? metrics)
    {
        int count = preView.NumLegalizedUpdates;
        if (count == 0) return;
        if (count == 1)
        {
            var update = preView.PopUpdateForIncrementalUpdates();
            var singleBatch = postView is null ? null : new BatchUpdateInfo(postView, postView);
            ApplyOne(update, singleBatch, metrics);
            return;
        }
        var batch = new BatchUpdateInfo(preView, postView);
        if (nodeCapacity <= 100 ? count > nodeCapacity : count > nodeCapacity / 40)
        {
            CountMutation(metrics, "domtree.dynamic.rebuild.batch_threshold");
            CalculateFromScratch(batch, metrics);
        }
        for (int index = 0; index < batch.NumLegalized && !batch.IsRecalculated; ++index)
            ApplyOne(batch.PreView.PopUpdateForIncrementalUpdates(), batch, metrics);
    }

    private void ApplyOne(LlvmCfgUpdate update, BatchUpdateInfo? batch, Dictionary<string, ulong>? metrics)
    {
        CountMutation(metrics, "domtree.dynamic.updates.applied");
        if (update.Kind == LlvmCfgUpdateKind.Insert) DynamicInsertEdge(batch, update.From, update.To, metrics);
        else DynamicDeleteEdge(batch, update.From, update.To, metrics);
    }

    private IEnumerable<LlvmBasicBlock> DynamicChildren(LlvmBasicBlock block, BatchUpdateInfo? batch, bool inverse)
    {
        if (batch is not null)
        {
            foreach (var child in batch.PreView.GetChildren(block, inverse)) yield return child;
            yield break;
        }
        if (inverse)
        {
            foreach (var predecessor in Predecessors(block)) yield return predecessor;
            yield break;
        }
        var terminator = block.GetTerminator();
        for (int index = terminator.SuccessorCount; index-- > 0;) yield return terminator.GetSuccessor(index);
    }

    private void CalculateFromScratch(BatchUpdateInfo? batch, Dictionary<string, ulong>? metrics)
    {
        blockNumberEpoch = Function.GetBlockNumberEpoch();
        nodes.Clear(); Roots = []; RootNode = null; dfsInfoValid = false; slowQueries = 0; nodeCapacity = 0;
        BatchUpdateInfo? postBatch = null;
        if (batch?.PostView is { } postView) { batch.PreView = postView; postBatch = batch; }
        new Construction(this, trace, metrics, postBatch).Calculate();
        nodeCapacity = Function.GetMaxBlockNumber() + (IsPostDominator ? 1U : 0U);
        if (batch is not null) batch.IsRecalculated = true;
        CountMutation(metrics, "domtree.dynamic.rebuild.completed");
    }

    private void UpdateRootsAfterUpdate(BatchUpdateInfo? batch, Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(IsPostDominator);
        bool hasNonTrivial = false;
        foreach (var root in Roots)
        {
            using var children = DynamicChildren(root, batch, inverse: false).GetEnumerator();
            if (children.MoveNext()) { hasNonTrivial = true; break; }
        }
        if (!hasNonTrivial) return;
        var previousRoots = Roots;
        new Construction(this, trace, metrics, batch).FindTreeRoots();
        var newRoots = Roots; Roots = previousRoots;
        bool equal = previousRoots.Count == newRoots.Count;
        if (equal)
        {
            HashSet<LlvmBasicBlock> old = new(previousRoots, ReferenceEqualityComparer.Instance);
            foreach (var root in newRoots) if (!old.Contains(root)) { equal = false; break; }
        }
        if (equal) return;
        CountMutation(metrics, "domtree.dynamic.rebuild.post_roots_changed");
        CalculateFromScratch(batch, metrics);
    }
}
