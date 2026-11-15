// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/AssumptionCache.{h,cpp}.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed class LlvmAssumptionCache : IDisposable
{
    public const uint ExprResultIdx = uint.MaxValue;
    public sealed class ResultElem(LlvmValue? assume, uint index) : IDisposable
    {
        public LlvmWeakValueHandle Assume { get; } = new(assume);
        public uint Index { get; } = index;
        public void Dispose() => Assume.Dispose();
    }
    private sealed class AffectedHandle(LlvmValue value, LlvmAssumptionCache cache) : LlvmCallbackValueHandle(value)
    {
        protected override void Deleted() { if (Value is { } old) cache.RemoveEntry(old); }
        protected override void AllUsesReplacedWith(LlvmValue replacement)
        {
            if (replacement is LlvmInstruction or LlvmArgument && Value is { } old)
                cache.Transfer(old, replacement);
        }
    }
    private sealed class Entry(LlvmValue value, LlvmAssumptionCache cache) : IDisposable
    {
        internal readonly AffectedHandle Handle = new(value, cache);
        internal readonly List<ResultElem> Results = [];
        public void Dispose() { Handle.Dispose(); foreach (var item in Results) item.Dispose(); }
    }
    private readonly LlvmFunction function;
    private readonly CompilationTrace? trace;
    private readonly uint maxAssumesPerValue;
    private readonly List<LlvmWeakValueHandle> handles = [];
    private readonly Dictionary<LlvmValue, Entry> affected = [];
    private bool scanned;
    public LlvmAssumptionCache(LlvmFunction function, CompilationTrace? trace = null, uint maxAssumesPerValue = 1024)
        => (this.function, this.trace, this.maxAssumesPerValue) = (function, trace?.Detail, maxAssumesPerValue);
    public IReadOnlyList<LlvmWeakValueHandle> Assumptions() { Scan(); return handles; }
    public IReadOnlyList<ResultElem> AssumptionsFor(LlvmValue value)
    {
        Scan();
        if (affected.TryGetValue(value, out var entry)) { Count("affected_hit"); return entry.Results; }
        Count("affected_miss"); return Array.Empty<ResultElem>();
    }
    private void Count(string metric) => trace?.Metric("assumption_cache." + metric, 1);
    private Entry GetOrInsert(LlvmValue value)
    {
        if (affected.TryGetValue(value, out var entry)) return entry;
        entry = new(value, this); affected.Add(value, entry); Count("affected_entry"); return entry;
    }
    private void RemoveEntry(LlvmValue value)
    {
        if (!affected.Remove(value, out var entry)) return;
        entry.Dispose(); Count("affected_removed");
    }
    private void Scan()
    {
        if (scanned) return;
        Debug.Assert(handles.Count == 0);
        foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
            {
                Count("scan_instruction");
                if (instruction is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true } call)
                    handles.Add(new(call));
            }
        scanned = true; Count("scan");
        foreach (var handle in handles) UpdateAffectedValues((LlvmCallInstruction)handle.Value!);
    }
    public void RegisterAssumption(LlvmCallInstruction call)
    {
        if (!scanned) { Count("register_before_scan"); return; }
        Debug.Assert(call.CalledFunction?.IntrinsicDescriptor?.IsAssume == true);
        Debug.Assert(call.Parent?.Parent == function);
        handles.Add(new(call)); UpdateAffectedValues(call); Count("registered");
    }
    public void UnregisterAssumption(LlvmCallInstruction call)
    {
        RemoveAffectedValues(call);
        for (int i = handles.Count - 1; i >= 0; --i)
            if (ReferenceEquals(handles[i].Value, call)) { handles[i].Dispose(); handles.RemoveAt(i); }
        Count("unregistered");
    }
    public void ReplaceAssumption(LlvmWeakValueHandle handle, LlvmCallInstruction replacement)
    {
        RemoveAffectedValues((LlvmCallInstruction)handle.Value!);
        handle.Value = replacement; UpdateAffectedValues(replacement); Count("replaced");
    }
    public void UpdateAffectedValues(LlvmCallInstruction call)
    {
        foreach (var (value, index) in FindAffected(call))
        {
            var list = GetOrInsert(value).Results;
            if (list.Count >= maxAssumesPerValue) { Count("cap_reached"); continue; }
            if (list.Any(item => ReferenceEquals(item.Assume.Value, call) && item.Index == index))
            { Count("duplicate"); continue; }
            list.Add(new(call, index)); Count("affected_inserted");
        }
    }
    private void RemoveAffectedValues(LlvmCallInstruction call)
    {
        foreach (var (value, _) in FindAffected(call))
        {
            if (!affected.TryGetValue(value, out var entry)) continue;
            bool found = false, hasNonnull = false;
            foreach (var item in entry.Results)
            {
                if (ReferenceEquals(item.Assume.Value, call)) { found = true; item.Assume.Value = null; }
                hasNonnull |= item.Assume.Value is not null;
                if (found && hasNonnull) break;
            }
            if (found && !hasNonnull) RemoveEntry(value);
        }
    }
    private void Transfer(LlvmValue old, LlvmValue replacement)
    {
        var target = GetOrInsert(replacement).Results;
        if (!affected.TryGetValue(old, out var source)) return;
        foreach (var item in source.Results)
        {
            if (target.Count >= maxAssumesPerValue) break;
            if (!target.Any(other => ReferenceEquals(other.Assume.Value, item.Assume.Value)))
                target.Add(new(item.Assume.Value, item.Index));
        }
        RemoveEntry(old); Count("rauw_transferred");
    }
    private static List<(LlvmValue Value, uint Index)> FindAffected(LlvmCallInstruction call)
    {
        List<(LlvmValue, uint)> result = [];
        for (int i = 0; i < call.OperandBundleCount; ++i)
        {
            uint index = (uint)i;
            FindValuesAffectedByOperandBundle(call.GetOperandBundleAt(i), value => result.Add((value, index)));
        }
        LlvmValueTracking.FindValuesAffectedByCondition(call.GetArgument(0), true, value => result.Add((value, ExprResultIdx)));
        return result;
    }
    public static void FindValuesAffectedByOperandBundle(LlvmOperandBundleUse bundle, Action<LlvmValue> insertAffected)
    {
        void Add(LlvmValue value) { if (value is LlvmArgument or LlvmGlobalValue or LlvmInstruction) insertAffected(value); }
        if (bundle.Tag == "separate_storage")
        {
            Debug.Assert(bundle.InputCount == 2);
            Add(LlvmValueTracking.GetUnderlyingObject(bundle.GetInput(0)));
            Add(LlvmValueTracking.GetUnderlyingObject(bundle.GetInput(1)));
        }
        else if (bundle.InputCount != 0 && bundle.Tag != "ignore") Add(bundle.GetInput(0));
    }
    public void Clear()
    {
        foreach (var handle in handles) handle.Dispose();
        handles.Clear();
        foreach (var entry in affected.Values) entry.Dispose();
        affected.Clear(); scanned = false; Count("cleared");
    }
    public void Dispose() => Clear();
}
