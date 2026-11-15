// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/GenericDomTreeUpdater.h,
// GenericDomTreeUpdaterImpl.h and DomTreeUpdater.h/.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public enum LlvmDomTreeUpdateStrategy : byte { Eager, Lazy }

public sealed partial class LlvmDomTreeUpdater : IDisposable
{
    private readonly LlvmDominatorTree? tree, postTree;
    private readonly CompilationTrace? trace;
    private readonly Dictionary<string, ulong>? metrics;
    private readonly List<PendingUpdate> pending = [];
    private readonly HashSet<LlvmBasicBlock> deletedBlocks = new(ReferenceEqualityComparer.Instance);
    private readonly List<BlockDeletionCallback> callbacks = [];
    private int treeIndex, postTreeIndex;
    private bool recalculatingTree, recalculatingPostTree;

    private readonly record struct CriticalEdge(LlvmBasicBlock From, LlvmBasicBlock To, LlvmBasicBlock NewBlock);
    private readonly record struct PendingUpdate(LlvmCfgUpdate Update, CriticalEdge? Split = null);

    public LlvmDomTreeUpdateStrategy Strategy { get; }
    public bool IsLazy => Strategy == LlvmDomTreeUpdateStrategy.Lazy;
    public bool IsEager => Strategy == LlvmDomTreeUpdateStrategy.Eager;
    public bool HasTree => tree is not null;
    public bool HasDomTree => HasTree;
    public bool HasPostTree => postTree is not null;
    public bool HasPostDomTree => HasPostTree;
    public bool HasPendingDeletedBlocks => deletedBlocks.Count != 0;
    public bool HasPendingTreeUpdates => tree is not null && pending.Count != treeIndex;
    public bool HasPendingPostTreeUpdates => postTree is not null && pending.Count != postTreeIndex;
    public bool HasPendingUpdates => HasPendingTreeUpdates || HasPendingPostTreeUpdates;

    public LlvmDomTreeUpdater(LlvmDominatorTree? tree, LlvmDominatorTree? postTree,
        LlvmDomTreeUpdateStrategy strategy, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(tree is null || !tree.IsPostDominator);
        Debug.Assert(postTree is null || postTree.IsPostDominator);
        Debug.Assert(tree is null || postTree is null || ReferenceEquals(tree.Function, postTree.Function));
        this.tree = tree;
        this.postTree = postTree;
        Strategy = strategy;
        this.trace = trace?.Detail;
        this.metrics = metrics;
    }

    public LlvmDomTreeUpdater(LlvmDominatorTree? tree, LlvmDomTreeUpdateStrategy strategy = LlvmDomTreeUpdateStrategy.Eager,
        CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
        : this(tree?.IsPostDominator == true ? null : tree,
            tree?.IsPostDominator == true ? tree : null, strategy, trace, metrics) { }

    public LlvmDomTreeUpdater(LlvmDomTreeUpdateStrategy strategy, CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null) : this(null, null, strategy, trace, metrics) { }

    public bool IsBlockPendingDeletion(LlvmBasicBlock block) => IsLazy && deletedBlocks.Contains(block);

    public void ApplyUpdates(IReadOnlyList<LlvmCfgUpdate> updates) => Observe("apply-updates", updates,
        static (updater, submitted) => updater.ApplyUpdatesCore(submitted));

    private void ApplyUpdatesCore(IReadOnlyList<LlvmCfgUpdate> updates)
    {
        if (!HasTree && !HasPostTree) return;
        Count("updates.submitted", (ulong)updates.Count);
        if (IsLazy)
        {
            foreach (var update in updates)
            {
                if (ReferenceEquals(update.From, update.To)) { Count("updates.self_edges_skipped"); continue; }
                pending.Add(new(update));
                Count("updates.queued");
            }
            return;
        }
        tree?.ApplyUpdates(updates, metrics: metrics);
        postTree?.ApplyUpdates(updates, metrics: metrics);
        Count("updates.eager_applied", (ulong)updates.Count);
    }

    public void ApplyUpdatesPermissive(IReadOnlyList<LlvmCfgUpdate> updates) => Observe("apply-permissive", updates,
        static (updater, submitted) => updater.ApplyUpdatesPermissiveCore(submitted));

    private void ApplyUpdatesPermissiveCore(IReadOnlyList<LlvmCfgUpdate> updates)
    {
        if (!HasTree && !HasPostTree) return;
        Count("permissive.submitted", (ulong)updates.Count);
        HashSet<(LlvmBasicBlock, LlvmBasicBlock)> seen = [];
        List<LlvmCfgUpdate> deduplicated = [];
        foreach (var update in updates)
        {
            if (ReferenceEquals(update.From, update.To)) { Count("permissive.self_edges_skipped"); continue; }
            if (!seen.Add((update.From, update.To))) { Count("permissive.duplicates_skipped"); continue; }
            bool hasEdge = LlvmDominatorTree.Successors(update.From).Contains(update.To);
            if ((update.Kind == LlvmCfgUpdateKind.Insert) != hasEdge)
            { Count("permissive.cfg_noops_skipped"); continue; }
            if (IsLazy) pending.Add(new(update));
            else deduplicated.Add(update);
            Count("permissive.accepted");
        }
        if (IsLazy) return;
        tree?.ApplyUpdates(deduplicated, metrics: metrics);
        postTree?.ApplyUpdates(deduplicated, metrics: metrics);
    }

    public void SplitCriticalEdge(LlvmBasicBlock from, LlvmBasicBlock to, LlvmBasicBlock newBlock) =>
        Observe("split-critical-edge", new CriticalEdge(from, to, newBlock),
            static (updater, edge) => updater.SplitCriticalEdgeCore(edge.From, edge.To, edge.NewBlock), from);

    private void SplitCriticalEdgeCore(LlvmBasicBlock from, LlvmBasicBlock to, LlvmBasicBlock newBlock)
    {
        if (!HasTree && !HasPostTree) return;
        var edge = new CriticalEdge(from, to, newBlock);
        Count("critical_splits.submitted");
        if (IsLazy) { pending.Add(new(default, edge)); return; }
        SplitTreeCriticalEdges([edge]);
        SplitPostTreeCriticalEdges([edge]);
    }

    public LlvmDominatorTree GetTree()
    {
        Debug.Assert(tree is not null);
        ApplyPendingUpdates(forward: true);
        DropOutOfDateUpdates();
        return tree;
    }

    public LlvmDominatorTree GetPostTree()
    {
        Debug.Assert(postTree is not null);
        ApplyPendingUpdates(forward: false);
        DropOutOfDateUpdates();
        return postTree;
    }

    public void Flush()
    {
        if (trace is null) { FlushCore(); return; }
        trace.Run("optimizer.domtree-updater.flush", this, static updater => updater.FlushCore(), Attributes());
    }

    private void FlushCore()
    {
        Count("flushes");
        ApplyPendingUpdates(forward: true);
        ApplyPendingUpdates(forward: false);
        DropOutOfDateUpdates();
    }

    public void Dispose() => Flush();

    private void Count(string name, ulong amount = 1)
    {
        if (metrics is null && trace is null) return;
        name = "domtree.updater." + name;
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + amount;
        trace?.Metric(name, (long)amount);
    }

    private void Observe<T>(string phase, T state, Action<LlvmDomTreeUpdater, T> action, LlvmBasicBlock? block = null)
    {
        if (trace is null) { action(this, state); return; }
        trace.Run("optimizer.domtree-updater." + phase, (Updater: this, State: state, Action: action),
            static work => work.Action(work.Updater, work.State), Attributes(block));
    }

    private Dictionary<string, string> Attributes(LlvmBasicBlock? block = null)
    {
        var attributes = new Dictionary<string, string>
        {
            ["function.name"] = (tree ?? postTree)?.Function.Name ?? block?.Parent?.Name ?? "",
            ["update.strategy"] = Strategy.ToString(),
            ["trees.forward"] = HasTree.ToString(),
            ["trees.post"] = HasPostTree.ToString()
        };
        if (block is null) return attributes;
        attributes["block.name"] = block.Name;
        if (block.FirstInstruction?.Source is not { } source) return attributes;
        attributes["source.file"] = source.Source;
        attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return attributes;
    }
}
