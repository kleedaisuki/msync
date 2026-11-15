// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MemorySSA.cpp
// ClobberWalkerBase, CachingWalker, SkipSelfWalker; AnalysisOptions.td.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsa
{
    private LlvmMemorySsaClobberWalker? clobberWalker;
    private LlvmMemorySsaWalker? cachingWalker;
    private LlvmMemorySsaWalker? skipSelfWalker;
    public uint MemorySsaCheckLimit { get; set; } = 100;
    public LlvmMemorySsaWalker GetWalker() => cachingWalker ??= new(this, GetClobberWalker(), false);
    public LlvmMemorySsaWalker GetSkipSelfWalker() => skipSelfWalker ??= new(this, GetClobberWalker(), true);
    private LlvmMemorySsaClobberWalker GetClobberWalker() => clobberWalker ??= new(this);
}

public sealed class LlvmMemorySsaWalker
{
    private readonly LlvmMemorySsa memorySsa;
    private readonly LlvmMemorySsaClobberWalker walker;
    private readonly bool skipSelf;
    internal LlvmMemorySsaWalker(LlvmMemorySsa memorySsa, LlvmMemorySsaClobberWalker walker, bool skipSelf)
        => (this.memorySsa, this.walker, this.skipSelf) = (memorySsa, walker, skipSelf);

    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmInstruction instruction, Dictionary<string, ulong>? metrics = null) =>
        GetClobberingMemoryAccess(memorySsa.GetMemoryAccess(instruction)!, metrics);
    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmMemoryAccess access, Dictionary<string, ulong>? metrics = null)
    {
        uint limit = memorySsa.MemorySsaCheckLimit;
        return GetClobberingMemoryAccess(access, new LlvmBatchAAResults(memorySsa.AA), ref limit, metrics);
    }
    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmMemoryAccess access, LlvmBatchAAResults aa,
        Dictionary<string, ulong>? metrics = null)
    {
        uint limit = memorySsa.MemorySsaCheckLimit;
        return GetClobberingMemoryAccess(access, aa, ref limit, metrics);
    }
    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmMemoryAccess access, LlvmMemoryLocation location,
        Dictionary<string, ulong>? metrics = null)
    {
        uint limit = memorySsa.MemorySsaCheckLimit;
        return GetClobberingMemoryAccess(access, location, new LlvmBatchAAResults(memorySsa.AA), ref limit, metrics);
    }
    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmMemoryAccess access, LlvmMemoryLocation location,
        LlvmBatchAAResults aa, Dictionary<string, ulong>? metrics = null)
    {
        uint limit = memorySsa.MemorySsaCheckLimit;
        return GetClobberingMemoryAccess(access, location, aa, ref limit, metrics);
    }
    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmMemoryAccess access, LlvmMemoryLocation location,
        LlvmBatchAAResults aa, ref uint upwardWalkLimit, Dictionary<string, ulong>? metrics = null)
    {
        if (memorySsa.Trace is not null)
            return TraceQuery(access, location, aa, ref upwardWalkLimit, metrics);
        return GetLocationClobberCore(access, location, aa, ref upwardWalkLimit, metrics ?? memorySsa.Metrics);
    }
    private LlvmMemoryAccess GetLocationClobberCore(LlvmMemoryAccess access, LlvmMemoryLocation location,
        LlvmBatchAAResults aa, ref uint upwardWalkLimit, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "memoryssa.walker.location_queries");
        if (location.Ptr is null) return access;
        if (access is LlvmMemoryUseOrDef starting)
        {
            if (memorySsa.IsLiveOnEntryDef(starting)) return starting;
            if (IsNonCallFenceLike(starting.MemoryInstruction!))
            { Count(metrics, "memoryssa.walker.locationless_barrier"); return starting; }
        }
        var query = new LlvmUpwardsMemoryQuery(null, access, location);
        return walker.FindClobber(aa, access, query, ref upwardWalkLimit, metrics);
    }
    public LlvmMemoryAccess GetClobberingMemoryAccess(LlvmMemoryAccess access, LlvmBatchAAResults aa,
        ref uint upwardWalkLimit, Dictionary<string, ulong>? metrics = null)
    {
        if (memorySsa.Trace is not null)
            return TraceQuery(access, null, aa, ref upwardWalkLimit, metrics);
        return GetClobberCore(access, aa, ref upwardWalkLimit, metrics ?? memorySsa.Metrics);
    }
    private LlvmMemoryAccess GetClobberCore(LlvmMemoryAccess access, LlvmBatchAAResults aa,
        ref uint upwardWalkLimit, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "memoryssa.walker.queries");
        if (access is not LlvmMemoryUseOrDef starting) return access;
        bool optimized = false;
        if (starting.IsOptimized)
        {
            Count(metrics, "memoryssa.walker.optimized_hit");
            if (!skipSelf || starting is not LlvmMemoryDef) return starting.GetOptimized()!;
            optimized = true;
        }
        LlvmInstruction instruction = starting.MemoryInstruction!;
        if (IsNonCallFenceLike(instruction))
        { Count(metrics, "memoryssa.walker.locationless_barrier"); return starting; }
        LlvmMemoryLocation location = instruction is LlvmCallBase ? new() : LlvmMemoryLocation.Get(instruction);
        var query = new LlvmUpwardsMemoryQuery(instruction, starting, location);
        if (instruction is LlvmLoadInstruction && (aa.GetModRefInfoMask(location) & LlvmModRefInfo.Mod) == 0)
        {
            Count(metrics, "memoryssa.walker.constant_memory");
            starting.SetOptimized(memorySsa.LiveOnEntryDef);
            return memorySsa.LiveOnEntryDef;
        }
        LlvmMemoryAccess optimizedAccess;
        if (!optimized)
        {
            LlvmMemoryAccess defining = starting.DefiningAccess!;
            if (memorySsa.IsLiveOnEntryDef(defining))
            {
                starting.SetOptimized(defining);
                Count(metrics, "memoryssa.walker.live_on_entry");
                return defining;
            }
            optimizedAccess = walker.FindClobber(aa, defining, query, ref upwardWalkLimit, metrics);
            starting.SetOptimized(optimizedAccess);
            Count(metrics, "memoryssa.walker.optimized_written");
        }
        else optimizedAccess = starting.GetOptimized()!;
        if (skipSelf && optimizedAccess is LlvmMemoryPhi && starting is LlvmMemoryDef && upwardWalkLimit != 0)
        {
            query.SkipSelfAccess = true;
            Count(metrics, "memoryssa.walker.skip_self_search");
            return walker.FindClobber(aa, optimizedAccess, query, ref upwardWalkLimit, metrics);
        }
        return optimizedAccess;
    }
    private LlvmMemoryAccess TraceQuery(LlvmMemoryAccess access, LlvmMemoryLocation? location,
        LlvmBatchAAResults aa, ref uint upwardWalkLimit, Dictionary<string, ulong>? callerMetrics)
    {
        Dictionary<string, ulong> work = [];
        Dictionary<string, string> attributes = new()
        {
            ["function.name"] = memorySsa.Function.Name,
            ["block.name"] = access.Block.Name,
            ["query.skip_self"] = skipSelf ? "true" : "false"
        };
        if (access is LlvmMemoryUseOrDef { MemoryInstruction: { } instruction })
        {
            attributes["instruction.opcode"] = instruction.Opcode.ToString();
            if (instruction.Source is { } source)
            {
                attributes["source.name"] = source.Source;
                attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        var state = (Walker: this, Access: access, Location: location, AA: aa, Limit: upwardWalkLimit, Work: work);
        var result = memorySsa.Trace!.MeasureDetailed("optimizer.memoryssa.clobber-walk", state, static query =>
        {
            uint limit = query.Limit;
            LlvmMemoryAccess found = query.Location is { } location
                ? query.Walker.GetLocationClobberCore(query.Access, location, query.AA, ref limit, query.Work)
                : query.Walker.GetClobberCore(query.Access, query.AA, ref limit, query.Work);
            query.Work["memoryssa.walker.remaining_budget"] = limit;
            Count(query.Work, found is LlvmMemoryPhi ? "memoryssa.walker.result_phi" : "memoryssa.walker.result_def");
            return (Access: found, Limit: limit, Work: query.Work);
        }, attributes: attributes, metrics: static result => result.Work);
        upwardWalkLimit = result.Limit;
        MergeMetrics(work, memorySsa.Metrics);
        if (!ReferenceEquals(callerMetrics, memorySsa.Metrics)) MergeMetrics(work, callerMetrics);
        return result.Access;
    }
    private static void MergeMetrics(Dictionary<string, ulong> source, Dictionary<string, ulong>? destination)
    {
        if (destination is null) return;
        foreach (var item in source) destination[item.Key] = destination.GetValueOrDefault(item.Key) + item.Value;
    }
    public void InvalidateInfo(LlvmMemoryAccess access)
    { if (access is LlvmMemoryUseOrDef useOrDef) useOrDef.ResetOptimized(); }
    // Locationless project instructions use barrier entry semantics, without
    // bypassing instruction ModRef when encountered as another query's candidate.
    private static bool IsNonCallFenceLike(LlvmInstruction instruction) => instruction is not LlvmCallBase &&
        (instruction.Opcode is LlvmOpcode.Fence or LlvmOpcode.CatchPad or LlvmOpcode.CatchRet ||
         instruction is LlvmHeapAllocInst or LlvmFileInInst or LlvmFileOutInst);
    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}

internal sealed class LlvmUpwardsMemoryQuery(LlvmInstruction? instruction, LlvmMemoryAccess original, LlvmMemoryLocation location)
{
    internal readonly LlvmInstruction? Instruction = instruction;
    internal readonly LlvmMemoryAccess OriginalAccess = original;
    internal readonly LlvmMemoryLocation StartingLocation = location;
    internal bool SkipSelfAccess;
}
