// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/AliasSetTracker.h/.cpp.
using System.Collections;
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed class LlvmAliasSetTracker : IEnumerable<LlvmAliasSet>
{
    private const uint SaturationThreshold = 250;
    private readonly LinkedList<LlvmAliasSet> sets = new();
    private readonly Dictionary<LlvmValue, LlvmAliasSet> pointers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ulong>? metrics;
    private LlvmAliasSet? aliasAny;
    internal uint TotalAliasSetSize;
    public LlvmBatchAAResults AliasAnalysis { get; }
    public LlvmAliasSetTracker(LlvmBatchAAResults analysis, Dictionary<string, ulong>? metrics = null)
        => (AliasAnalysis, this.metrics) = (analysis, metrics ?? analysis.Metrics);
    public IEnumerator<LlvmAliasSet> GetEnumerator() => sets.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal void CountWork(string name)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
    private LlvmAliasSet CreateSet()
    {
        var set = new LlvmAliasSet();
        set.Node = sets.AddLast(set);
        CountWork("alias_set_tracker.sets.created");
        return set;
    }
    internal void Remove(LlvmAliasSet set)
    {
        if (set.Forward is { } forward) { forward.DropRef(this); set.Forward = null; }
        else TotalAliasSetSize -= (uint)set.Count;
        sets.Remove(set.Node!);
        if (ReferenceEquals(set, aliasAny)) { aliasAny = null; Debug.Assert(sets.Count == 0); }
        CountWork("alias_set_tracker.sets.removed");
    }
    private LlvmAliasSet CollapseForwarding(LlvmAliasSet set)
    {
        if (set.Forward is null) return set;
        set.Forward = CollapseForwarding(set.Forward);
        var replacement = set.Forward;
        replacement.AddRef();
        set.DropRef(this);
        CountWork("alias_set_tracker.forwarding.collapsed");
        return replacement;
    }
    private LlvmAliasSet? MergeForLocation(LlvmMemoryLocation location, LlvmAliasSet? existing, out bool mustAliasAll)
    {
        LlvmAliasSet? found = null;
        mustAliasAll = true;
        for (var node = sets.First; node is not null;)
        {
            var set = node.Value;
            node = node.Next;
            if (set.Forward is not null) continue;
            if (!ReferenceEquals(set, existing))
            {
                var result = set.AliasesMemoryLocation(location, AliasAnalysis);
                if (result == LlvmAliasKind.NoAlias) continue;
                if (result != LlvmAliasKind.MustAlias) mustAliasAll = false;
            }
            if (found is null) found = set;
            else found.MergeSetIn(set, this);
        }
        return found;
    }
    private LlvmAliasSet? FindForUnknown(LlvmInstruction instruction)
    {
        LlvmAliasSet? found = null;
        for (var node = sets.First; node is not null;)
        {
            var set = node.Value;
            node = node.Next;
            if (set.Forward is not null || !set.AliasesUnknownInst(instruction, AliasAnalysis).IsModOrRefSet()) continue;
            if (found is null) found = set;
            else found.MergeSetIn(set, this);
        }
        return found;
    }
    public LlvmAliasSet GetAliasSetFor(LlvmMemoryLocation location)
    {
        CountWork("alias_set_tracker.location.queries");
        pointers.TryGetValue(location.Ptr!, out var existing);
        if (existing is not null)
        {
            pointers[location.Ptr!] = existing = CollapseForwarding(existing);
            if (existing.Locations.Contains(location)) return existing;
        }
        bool mustAliasAll = false;
        LlvmAliasSet selected;
        if (aliasAny is not null) selected = aliasAny;
        else if (MergeForLocation(location, existing, out mustAliasAll) is { } merged) selected = merged;
        else { selected = CreateSet(); mustAliasAll = true; }
        selected.AddMemoryLocation(this, location, mustAliasAll);
        if (existing is not null)
        {
            pointers[location.Ptr!] = existing = CollapseForwarding(existing);
            Debug.Assert(ReferenceEquals(existing, selected));
        }
        else { selected.AddRef(); pointers.Add(location.Ptr!, selected); }
        return selected;
    }
    private LlvmAliasSet AddMemoryLocation(LlvmMemoryLocation location, LlvmModRefInfo access)
    {
        var set = GetAliasSetFor(location);
        set.Access |= access;
        return aliasAny is null && TotalAliasSetSize > SaturationThreshold ? MergeAll() : set;
    }
    public void Add(LlvmMemoryLocation location) => AddMemoryLocation(location, LlvmModRefInfo.NoModRef);
    public void Add(LlvmInstruction instruction)
    {
        if (instruction is LlvmLoadInstruction load)
        {
            if (StrongerThanMonotonic(load.Ordering)) { AddUnknown(instruction); return; }
            AddMemoryLocation(LlvmMemoryLocation.Get(load), LlvmModRefInfo.Ref);
            return;
        }
        if (instruction is LlvmStoreInstruction store)
        {
            if (StrongerThanMonotonic(store.Ordering)) { AddUnknown(instruction); return; }
            AddMemoryLocation(LlvmMemoryLocation.Get(store), LlvmModRefInfo.Mod);
            return;
        }
        if (instruction is LlvmCallBase call)
        {
            var effects = AliasAnalysis.GetMemoryEffects(call);
            if (effects.OnlyAccessesArgPointees)
            {
                var mask = effects.GetModRef();
                foreach (var argument in call.Arguments)
                    if (argument.Type.IsPointerTy() && mask.IsModOrRefSet())
                        AddMemoryLocation(LlvmMemoryLocation.GetBeforeOrAfter(argument), mask);
                return;
            }
        }
        // Per-argument attributes default to ModRef; intrinsic-ID memory
        // transfers, invariant markers and VAArg have no concrete carrier.
        AddUnknown(instruction);
    }
    public void AddWithoutAATags(LlvmStoreInstruction store)
    {
        Debug.Assert(!StrongerThanMonotonic(store.Ordering));
        AddMemoryLocation(LlvmMemoryLocation.Get(store), LlvmModRefInfo.Mod);
    }
    public void Add(LlvmBasicBlock block)
    {
        foreach (var instruction in block.Instructions) Add(instruction);
    }
    private void AddUnknown(LlvmInstruction instruction)
    {
        if (!instruction.MayReadFromMemory() && !instruction.MayWriteToMemory()) return;
        var set = FindForUnknown(instruction) ?? CreateSet();
        set.AddUnknownInstruction(instruction);
        CountWork("alias_set_tracker.unknown_instructions.added");
    }
    private LlvmAliasSet MergeAll()
    {
        Debug.Assert(aliasAny is null && TotalAliasSetSize > SaturationThreshold);
        var original = sets.ToArray();
        aliasAny = CreateSet();
        aliasAny.MayAlias = true;
        aliasAny.Access = LlvmModRefInfo.ModRef;
        aliasAny.AliasAny = true;
        foreach (var set in original)
        {
            if (set.Forward is { } forward)
            {
                set.Forward = aliasAny;
                aliasAny.AddRef();
                forward.DropRef(this);
                continue;
            }
            aliasAny.MergeSetIn(set, this);
        }
        CountWork("alias_set_tracker.saturation");
        return aliasAny;
    }
    private static bool StrongerThanMonotonic(LlvmAtomicOrdering ordering) => ordering is not
        (LlvmAtomicOrdering.NotAtomic or LlvmAtomicOrdering.Unordered or LlvmAtomicOrdering.Monotonic);
}
