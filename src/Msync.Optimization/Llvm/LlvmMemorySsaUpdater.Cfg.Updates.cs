// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp applyUpdates (mixed insert/delete snapshots) and
// the canonical CFGUpdate adapter of applyInsertUpdates.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    public void ApplyUpdates(IReadOnlyList<LlvmCfgUpdate> updates, LlvmDominatorTree tree, bool updateDtFirst = false)
        => RunUpdate("cfg-updates", null, (updates, tree, updateDtFirst),
            static (updater, state) => updater.ApplyUpdatesCore(state.updates, state.tree, state.updateDtFirst));

    private void ApplyUpdatesCore(IReadOnlyList<LlvmCfgUpdate> updates, LlvmDominatorTree tree, bool updateDtFirst)
    {
        var deletes = new List<LlvmCfgUpdate>();
        var reversedDeletes = new List<LlvmCfgUpdate>();
        var inserts = new List<LlvmCfgUpdate>();
        foreach (var update in updates)
        {
            if (update.Kind == LlvmCfgUpdateKind.Insert) inserts.Add(update);
            else
            {
                deletes.Add(update);
                reversedDeletes.Add(new(LlvmCfgUpdateKind.Insert, update.From, update.To));
            }
        }
        if (deletes.Count != 0)
        {
            if (inserts.Count != 0)
            {
                if (!updateDtFirst) tree.ApplyUpdates([], reversedDeletes, MemorySsa.Metrics);
                else tree.ApplyUpdates(updates, reversedDeletes, MemorySsa.Metrics);
                var graphDiff = new LlvmCfgDiff(reversedDeletes, metrics: MemorySsa.Metrics);
                ApplyInsertUpdates(inserts, tree, graphDiff);
                tree.ApplyUpdates(deletes, metrics: MemorySsa.Metrics);
            }
            else if (updateDtFirst) tree.ApplyUpdates(deletes, metrics: MemorySsa.Metrics);
        }
        else
        {
            if (updateDtFirst) tree.ApplyUpdates(updates, metrics: MemorySsa.Metrics);
            ApplyInsertUpdates(inserts, tree, new LlvmCfgDiff([], metrics: MemorySsa.Metrics));
        }
        foreach (var update in deletes) RemoveEdge(update.From, update.To);
        Count("cfg.insert_updates", (ulong)inserts.Count); Count("cfg.delete_updates", (ulong)deletes.Count);
    }

    public void ApplyInsertUpdates(IReadOnlyList<LlvmCfgUpdate> updates, LlvmDominatorTree tree) =>
        RunUpdate("cfg-inserts", null, (updates, tree), static (updater, state) =>
            updater.ApplyInsertUpdates(state.updates, state.tree, new LlvmCfgDiff([], metrics: updater.MemorySsa.Metrics)));

    private void ApplyInsertUpdates(IReadOnlyList<LlvmCfgUpdate> updates, LlvmDominatorTree tree, LlvmCfgDiff graphDiff) =>
        ApplyInsertUpdates(updates.Select(update => (update.From, update.To)).ToArray(), tree,
            block => graphDiff.GetChildren(block, inverseEdge: true), block => graphDiff.GetChildren(block));
}
