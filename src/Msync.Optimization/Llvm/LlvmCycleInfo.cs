// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADT/GenericCycleInfo.h,
// ADT/GenericCycleImpl.h static discovery and queries; IR/CycleInfo.h adapter.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmCycle
{
    internal readonly LlvmCycleInfo Owner;
    internal readonly int Index;
    internal LlvmCycle(LlvmCycleInfo owner, int index) => (Owner, Index) = (owner, index);
    public LlvmBasicBlock Header => Entries[0];
    public LlvmCycle? ParentCycle => Owner.GetParentCycle(this);
    public uint Depth => Owner.GetDepth(this);
    public bool IsReducible => Entries.Count == 1;
    public IReadOnlyList<LlvmBasicBlock> Blocks { get; internal set; } = Array.Empty<LlvmBasicBlock>();
    public IReadOnlyList<LlvmBasicBlock> Entries { get; internal set; } = Array.Empty<LlvmBasicBlock>();
    public IEnumerable<LlvmCycle> Children => Owner.GetChildren(this);
    public bool Contains(LlvmCycle cycle) => Owner.Contains(this, cycle);
    public bool Contains(LlvmBasicBlock block) => Owner.Contains(this, block);
    public bool IsEntry(LlvmBasicBlock block) => Entries.Contains(block);
    public uint GetDepth() => Depth;
    public void GetExitBlocks(List<LlvmBasicBlock> result) => Owner.GetExitBlocks(this, result);
    public void GetExitingBlocks(List<LlvmBasicBlock> result) => Owner.GetExitingBlocks(this, result);
}

public sealed partial class LlvmCycleInfo
{
    private struct CycleData
    {
        internal int Parent, BlockBegin, BlockEnd, EntryBegin, EntrySize, Descendants;
        internal uint Depth;
    }
    private int[] blockMap = [];
    private CycleData[] data = [];
    private LlvmCycle[] cycles = [];
    private List<LlvmBasicBlock>?[]? exitCaches;
    private readonly CompilationTrace? trace;
    private readonly Dictionary<string, ulong>? queryMetrics;
    public LlvmFunction Function { get; }
    public IReadOnlyList<LlvmCycle> Cycles { get; private set; } = Array.Empty<LlvmCycle>();
    public LlvmCycleInfo(LlvmFunction function, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        Function = function; this.trace = trace?.Detail; queryMetrics = metrics;
        Debug.Assert(!function.IsDeclaration);
        var work = new Discovery(this, trace?.Detail, metrics);
        if (trace?.Detail is not { } detail) work.Run();
        else detail.MeasureDetailed("optimizer.analysis.llvm-cycle-info", work, static state => { state.Run(); return state; },
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name }, metrics: static state => state.Metrics!);
    }
    public LlvmCycle? GetCycle(LlvmBasicBlock block)
    {
        Debug.Assert(ReferenceEquals(block.Parent, Function));
        if (block.Number >= blockMap.Length) return null;
        int index = blockMap[(int)block.Number];
        return index < 0 ? null : cycles[index];
    }
    public LlvmCycle? GetParentCycle(LlvmCycle cycle)
    { int parent = data[cycle.Index].Parent; return parent < 0 ? null : cycles[parent]; }
    public uint GetDepth(LlvmCycle cycle) => data[cycle.Index].Depth;
    public uint GetCycleDepth(LlvmBasicBlock block) => GetCycle(block)?.Depth ?? 0;
    public bool Contains(LlvmCycle outer, LlvmCycle inner)
    {
        Debug.Assert(ReferenceEquals(outer.Owner, this) && ReferenceEquals(inner.Owner, this));
        return data[outer.Index].BlockBegin <= data[inner.Index].BlockBegin && data[inner.Index].BlockEnd <= data[outer.Index].BlockEnd;
    }
    public bool Contains(LlvmCycle cycle, LlvmBasicBlock block) => GetCycle(block) is { } inner && Contains(cycle, inner);
    public IEnumerable<LlvmCycle> GetChildren(LlvmCycle cycle)
    {
        int end = cycle.Index + 1 + data[cycle.Index].Descendants;
        for (int index = cycle.Index + 1; index < end; index += 1 + data[index].Descendants) yield return cycles[index];
    }
    public IEnumerable<LlvmCycle> TopLevelCycles
    {
        get { for (int index = 0; index < cycles.Length; index += 1 + data[index].Descendants) yield return cycles[index]; }
    }
    public LlvmCycle? GetSmallestCommonCycle(LlvmCycle? first, LlvmCycle? second)
    {
        if (first is null || second is null) return null;
        while (first!.Depth > second!.Depth) first = first.ParentCycle;
        while (second!.Depth > first!.Depth) second = second.ParentCycle;
        while (!ReferenceEquals(first, second)) { first = first!.ParentCycle; second = second!.ParentCycle; }
        return first;
    }
    public LlvmCycle? GetSmallestCommonCycle(LlvmBasicBlock first, LlvmBasicBlock second) => GetSmallestCommonCycle(GetCycle(first), GetCycle(second));
    public LlvmCycle? GetTopLevelParentCycle(LlvmBasicBlock block)
    {
        LlvmCycle? cycle = GetCycle(block);
        while (cycle?.ParentCycle is { } parent) cycle = parent;
        return cycle;
    }
    public void GetExitBlocks(LlvmCycle cycle, List<LlvmBasicBlock> result)
    {
        if (trace is null) { GetExitBlocksCore(cycle, result); return; }
        trace.Run("optimizer.cycle-info.exit-blocks", (Info: this, Cycle: cycle, Output: result),
            static state => state.Info.GetExitBlocksCore(state.Cycle, state.Output),
            new Dictionary<string, string> { ["function.name"] = Function.Name, ["cycle.header"] = cycle.Header.Name });
    }
    private void GetExitBlocksCore(LlvmCycle cycle, List<LlvmBasicBlock> result)
    {
        exitCaches ??= new List<LlvmBasicBlock>?[cycles.Length];
        List<LlvmBasicBlock> cache = exitCaches[cycle.Index] ??= [];
        ulong blocks = 0, edges = 0;
        if (cache.Count == 0)
        {
            var seen = new HashSet<LlvmBasicBlock>();
            foreach (LlvmBasicBlock block in cycle.Blocks)
            {
                ++blocks;
                foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
                {
                    ++edges;
                    if (!cycle.Contains(successor) && seen.Add(successor)) cache.Add(successor);
                }
            }
        }
        else RecordQuery("cycle_info.exit_query.cache_hit", 1);
        result.AddRange(cache);
        RecordQuery("cycle_info.exit_query.blocks", blocks);
        RecordQuery("cycle_info.exit_query.edges", edges);
        RecordQuery("cycle_info.exit_query.exits", (ulong)cache.Count);
    }
    private void RecordQuery(string name, ulong value)
    {
        if (queryMetrics is not null) queryMetrics[name] = queryMetrics.GetValueOrDefault(name) + value;
        trace?.Metric(name, (long)value);
    }
    public void GetExitingBlocks(LlvmCycle cycle, List<LlvmBasicBlock> result)
    {
        foreach (LlvmBasicBlock block in cycle.Blocks)
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
                if (!cycle.Contains(successor)) { result.Add(block); break; }
    }
}
