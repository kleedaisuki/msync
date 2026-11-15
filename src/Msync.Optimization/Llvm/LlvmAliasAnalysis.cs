// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: AliasAnalysis.cpp AAResults and BatchAAResults.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmAAResults
{
    internal readonly LlvmBasicAliasAnalysis Basic;
    internal readonly LlvmFunction Function;
    internal readonly LlvmDataLayout Layout;
    internal readonly Dictionary<string, ulong>? Metrics;
    public LlvmAAResults(LlvmFunction function, LlvmDataLayout? layout = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null)
    {
        Function = function;
        Layout = layout ?? function.Parent!.DataLayout;
        Metrics = metrics;
        Basic = new(function, Layout, tree, metrics);
    }
    internal void Count(string name)
    {
        if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1;
    }
    public LlvmAliasResult Alias(LlvmMemoryLocation a, LlvmMemoryLocation b) => Alias(a, b, new(this));
    public LlvmAliasResult Alias(LlvmValue a, LlvmLocationSize aSize, LlvmValue b, LlvmLocationSize bSize) =>
        Alias(new LlvmMemoryLocation(a, aSize), new LlvmMemoryLocation(b, bSize));
    public LlvmAliasResult Alias(LlvmValue a, LlvmValue b) => Alias(LlvmMemoryLocation.GetBeforeOrAfter(a), LlvmMemoryLocation.GetBeforeOrAfter(b));
    internal LlvmAliasResult Alias(LlvmMemoryLocation a, LlvmMemoryLocation b, LlvmAAQueryInfo query, LlvmInstruction? context = null)
    {
        Debug.Assert(a.Ptr!.Type.IsPointerTy() && b.Ptr!.Type.IsPointerTy());
        Count("aa.alias.queries");
        ++query.Depth;
        LlvmAliasResult result;
        try { result = Basic.Alias(a, b, query, context); }
        finally { --query.Depth; }
        if (query.Depth == 0 && Metrics is not null) Count("aa.alias.result." + result.Kind);
        return result;
    }
    public LlvmModRefInfo GetModRefInfoMask(LlvmMemoryLocation location, bool ignoreLocals = false) =>
        GetModRefInfoMask(location, new(this), ignoreLocals);
    internal LlvmModRefInfo GetModRefInfoMask(LlvmMemoryLocation location, LlvmAAQueryInfo query, bool ignoreLocals = false)
    {
        Count("aa.modref_mask.queries");
        var visited = new HashSet<LlvmValue>(ReferenceEqualityComparer.Instance);
        var work = new List<LlvmValue> { location.Ptr! };
        int remaining = 8;
        LlvmModRefInfo result = LlvmModRefInfo.NoModRef;
        do
        {
            var value = LlvmValueTracking.GetUnderlyingObject(work[^1], metrics: Metrics);
            work.RemoveAt(work.Count - 1);
            if (!visited.Add(value)) continue;
            Count("aa.modref_mask.objects");
            if (ignoreLocals && value is LlvmAllocaInstruction) continue;
            // No noalias/readonly argument attributes are represented.
            if (value is LlvmGlobalVariable global)
            {
                if (!global.IsConstant) return LlvmModRefInfo.ModRef;
                continue;
            }
            if (value is LlvmSelectInstruction select)
            {
                work.Add(select.TrueValue);
                work.Add(select.FalseValue);
                continue;
            }
            if (value is LlvmPhiInstruction phi)
            {
                if (phi.IncomingCount > remaining) return LlvmModRefInfo.ModRef;
                for (int index = 0; index < phi.IncomingCount; ++index) work.Add(phi.GetIncomingValue(index));
                continue;
            }
            return LlvmModRefInfo.ModRef;
        } while (work.Count != 0 && --remaining != 0);
        if (work.Count != 0) { Count("aa.modref_mask.lookup_limit"); return LlvmModRefInfo.ModRef; }
        return result;
    }
    public LlvmMemoryEffects GetMemoryEffects(LlvmInstruction instruction)
    {
        if (instruction is LlvmHeapAllocInst)
        { Count("aa.heap.allocator_state_effects"); return LlvmMemoryEffects.InaccessibleMemOnly(); }
        if (instruction is not LlvmCallBase call) return new(GetModRefInfo(instruction, (LlvmMemoryLocation?)null));
        return LlvmMemoryEffects.CreateFromIntValue(call.GetMemoryEffects().Raw);
    }
    public LlvmMemoryEffects GetMemoryEffects(LlvmFunction function) =>
        LlvmMemoryEffects.CreateFromIntValue(function.GetMemoryEffects().Raw);
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction) => GetModRefInfo(instruction, (LlvmMemoryLocation?)null);
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction, LlvmMemoryLocation? location) => GetModRefInfo(instruction, location, new(this));
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction first, LlvmInstruction second) => GetModRefInfo(first, second, new(this));
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction, LlvmCallBase call) => GetModRefInfo(instruction, call, new(this));
    public LlvmModRefInfo GetModRefInfo(LlvmCallBase first, LlvmCallBase second) => GetModRefInfo((LlvmInstruction)first, second, new(this));
}

public sealed class LlvmBatchAAResults
{
    private readonly LlvmAAResults results;
    private readonly LlvmAAQueryInfo query;
    internal Dictionary<string, ulong>? Metrics => results.Metrics;
    public LlvmBatchAAResults(LlvmAAResults results, LlvmCaptureAnalysis? captures = null)
        => (this.results, query) = (results, new(results, captures));
    public bool MayBeCrossIteration { get => query.MayBeCrossIteration; set => query.MayBeCrossIteration = value; }
    public bool UseDominatorTree { get => query.UseDominatorTree; set => query.UseDominatorTree = value; }
    public LlvmAliasResult Alias(LlvmMemoryLocation a, LlvmMemoryLocation b) => results.Alias(a, b, query);
    public LlvmModRefInfo GetModRefInfoMask(LlvmMemoryLocation location, bool ignoreLocals = false) => results.GetModRefInfoMask(location, query, ignoreLocals);
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction) => GetModRefInfo(instruction, (LlvmMemoryLocation?)null);
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction, LlvmMemoryLocation? location) => results.GetModRefInfo(instruction, location, query);
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction first, LlvmInstruction second) => results.GetModRefInfo(first, second, query);
    public LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction, LlvmCallBase call) => results.GetModRefInfo(instruction, call, query);
    public LlvmModRefInfo GetModRefInfo(LlvmCallBase first, LlvmCallBase second) => results.GetModRefInfo((LlvmInstruction)first, second, query);
    public LlvmMemoryEffects GetMemoryEffects(LlvmInstruction instruction) => results.GetMemoryEffects(instruction);
}
