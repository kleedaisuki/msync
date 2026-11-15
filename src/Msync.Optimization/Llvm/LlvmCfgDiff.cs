// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/CFGUpdate.h/CFGDiff.h.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public enum LlvmCfgUpdateKind : byte { Insert, Delete }
public readonly record struct LlvmCfgUpdate(LlvmCfgUpdateKind Kind, LlvmBasicBlock From, LlvmBasicBlock To);

public sealed class LlvmCfgDiff
{
    private sealed class Edges
    {
        internal readonly List<LlvmBasicBlock> Deleted = [], Inserted = [];
        internal List<LlvmBasicBlock> Get(bool insert) => insert ? Inserted : Deleted;
        internal bool IsEmpty => Deleted.Count == 0 && Inserted.Count == 0;
    }
    private readonly Dictionary<LlvmBasicBlock, Edges> successors = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LlvmBasicBlock, Edges> predecessors = new(ReferenceEqualityComparer.Instance);
    private readonly List<LlvmCfgUpdate> legalized;
    private readonly bool reverseApplied, inverseGraph;
    private readonly Dictionary<string, ulong>? metrics;
    public IReadOnlyList<LlvmCfgUpdate> LegalizedUpdates { get; }
    public int NumLegalizedUpdates => legalized.Count;

    public LlvmCfgDiff(IReadOnlyList<LlvmCfgUpdate> updates, bool reverseApplyUpdates = false,
        bool inverseGraph = false, Dictionary<string, ulong>? metrics = null)
    {
        this.inverseGraph = inverseGraph; reverseApplied = reverseApplyUpdates; this.metrics = metrics;
        legalized = LegalizeUpdates(updates, inverseGraph, metrics: metrics);
        LegalizedUpdates = legalized.AsReadOnly();
        foreach (var update in legalized)
        {
            bool insert = (update.Kind == LlvmCfgUpdateKind.Insert) != reverseApplyUpdates;
            GetEdges(successors, update.From).Get(insert).Add(update.To);
            GetEdges(predecessors, update.To).Get(insert).Add(update.From);
        }
    }

    public static List<LlvmCfgUpdate> LegalizeUpdates(IReadOnlyList<LlvmCfgUpdate> updates,
        bool inverseGraph = false, bool reverseResultOrder = false, Dictionary<string, ulong>? metrics = null)
    {
        Dictionary<(LlvmBasicBlock From, LlvmBasicBlock To), int> operations = [];
        foreach (var update in updates)
        {
            var key = inverseGraph ? (update.To, update.From) : (update.From, update.To);
            operations[key] = operations.GetValueOrDefault(key) + (update.Kind == LlvmCfgUpdateKind.Insert ? 1 : -1);
            Count(metrics, "cfg_diff.updates.input");
        }
        List<LlvmCfgUpdate> result = new(operations.Count);
        foreach (var (edge, count) in operations)
        {
            Debug.Assert(Math.Abs(count) <= 1, "Unbalanced CFG operations");
            if (count == 0) { Count(metrics, "cfg_diff.edges.cancelled"); continue; }
            result.Add(new(count > 0 ? LlvmCfgUpdateKind.Insert : LlvmCfgUpdateKind.Delete, edge.From, edge.To));
        }
        for (int index = 0; index < updates.Count; ++index)
        {
            var update = updates[index];
            operations[inverseGraph ? (update.To, update.From) : (update.From, update.To)] = index;
        }
        result.Sort((a, b) => reverseResultOrder
            ? operations[(a.From, a.To)].CompareTo(operations[(b.From, b.To)])
            : operations[(b.From, b.To)].CompareTo(operations[(a.From, a.To)]));
        Count(metrics, "cfg_diff.updates.legalized", (ulong)result.Count);
        return result;
    }

    public LlvmCfgUpdate PopUpdateForIncrementalUpdates()
    {
        Debug.Assert(legalized.Count != 0, "No CFG updates to apply");
        var update = legalized[^1]; legalized.RemoveAt(legalized.Count - 1);
        bool insert = (update.Kind == LlvmCfgUpdateKind.Insert) != reverseApplied;
        RemoveLast(successors, update.From, update.To, insert);
        RemoveLast(predecessors, update.To, update.From, insert);
        Count(metrics, "cfg_diff.updates.popped");
        return update;
    }

    public List<LlvmBasicBlock> GetChildren(LlvmBasicBlock block, bool inverseEdge = false)
    {
        List<LlvmBasicBlock> result = inverseEdge
            ? LlvmDominatorTree.Predecessors(block).ToList()
            : LlvmDominatorTree.Successors(block).Reverse().ToList();
        var map = inverseEdge != inverseGraph ? predecessors : successors;
        if (!map.TryGetValue(block, out var changes)) return result;
        foreach (var child in changes.Deleted)
        {
            for (int index = result.Count; --index >= 0;)
                if (ReferenceEquals(result[index], child)) result.RemoveAt(index);
            Count(metrics, "cfg_diff.children.deleted_edges");
        }
        result.AddRange(changes.Inserted);
        Count(metrics, "cfg_diff.children.inserted_edges", (ulong)changes.Inserted.Count);
        return result;
    }

    private static Edges GetEdges(Dictionary<LlvmBasicBlock, Edges> map, LlvmBasicBlock block)
    {
        if (!map.TryGetValue(block, out var edges)) map.Add(block, edges = new());
        return edges;
    }
    private static void RemoveLast(Dictionary<LlvmBasicBlock, Edges> map, LlvmBasicBlock block, LlvmBasicBlock child, bool insert)
    {
        var edges = map[block]; var list = edges.Get(insert);
        Debug.Assert(ReferenceEquals(list[^1], child));
        list.RemoveAt(list.Count - 1);
        if (edges.IsEmpty) map.Remove(block);
    }
    private static void Count(Dictionary<string, ulong>? metrics, string name, ulong value = 1)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + value; }
}
