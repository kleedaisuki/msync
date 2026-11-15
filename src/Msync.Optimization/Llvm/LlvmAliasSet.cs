// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/AliasSetTracker.h/.cpp AliasSet.
using System.Collections;
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed class LlvmAliasSet : IEnumerable<LlvmMemoryLocation>
{
    internal LlvmAliasSet? Forward;
    internal LinkedListNode<LlvmAliasSet>? Node;
    internal List<LlvmMemoryLocation> Locations = [];
    internal List<LlvmInstruction> UnknownInstructions = [];
    internal uint References;
    internal bool AliasAny;
    internal bool MayAlias;
    internal LlvmModRefInfo Access;
    internal LlvmAliasSet() { }
    public bool IsRef => Access.IsRefSet();
    public bool IsMod => Access.IsModSet();
    public bool IsMustAlias => !MayAlias;
    public bool IsMayAlias => MayAlias;
    public bool IsForwardingAliasSet => Forward is not null;
    public int Count => Locations.Count;
    public IEnumerator<LlvmMemoryLocation> GetEnumerator() => Locations.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IReadOnlyList<LlvmValue> GetPointers()
    {
        var seen = new HashSet<LlvmValue>(ReferenceEqualityComparer.Instance);
        var pointers = new List<LlvmValue>();
        foreach (var location in Locations)
            if (seen.Add(location.Ptr!)) pointers.Add(location.Ptr!);
        return pointers;
    }
    internal void AddRef() => ++References;
    internal void DropRef(LlvmAliasSetTracker tracker)
    {
        Debug.Assert(References >= 1);
        if (--References == 0) tracker.Remove(this);
    }
    internal void MergeSetIn(LlvmAliasSet other, LlvmAliasSetTracker tracker)
    {
        Debug.Assert(other.Forward is null && Forward is null);
        Access |= other.Access;
        MayAlias |= other.MayAlias;
        if (!MayAlias && !Locations.Any(location => other.Locations.Any(otherLocation =>
            tracker.AliasAnalysis.Alias(location, otherLocation) == LlvmAliasKind.MustAlias))) MayAlias = true;
        if (Locations.Count == 0) (Locations, other.Locations) = (other.Locations, Locations);
        else { Locations.AddRange(other.Locations); other.Locations.Clear(); }
        bool hadUnknowns = other.UnknownInstructions.Count != 0;
        if (UnknownInstructions.Count == 0)
        {
            if (hadUnknowns)
            {
                (UnknownInstructions, other.UnknownInstructions) = (other.UnknownInstructions, UnknownInstructions);
                AddRef();
            }
        }
        else if (hadUnknowns) { UnknownInstructions.AddRange(other.UnknownInstructions); other.UnknownInstructions.Clear(); }
        other.Forward = this;
        AddRef();
        if (hadUnknowns) other.DropRef(tracker);
        tracker.CountWork("alias_set_tracker.sets.merged");
    }
    internal void AddMemoryLocation(LlvmAliasSetTracker tracker, LlvmMemoryLocation location, bool knownMustAlias)
    {
        if (IsMustAlias && !knownMustAlias && !Locations.Any(existing =>
            tracker.AliasAnalysis.Alias(location, existing) == LlvmAliasKind.MustAlias)) MayAlias = true;
        Locations.Add(location);
        ++tracker.TotalAliasSetSize;
        tracker.CountWork("alias_set_tracker.locations.added");
    }
    internal void AddUnknownInstruction(LlvmInstruction instruction)
    {
        if (UnknownInstructions.Count == 0) AddRef();
        UnknownInstructions.Add(instruction);
        // Guard/invariant.start intrinsic IDs are not represented by ordinary calls.
        if (!instruction.MayWriteToMemory()) { MayAlias = true; Access |= LlvmModRefInfo.Ref; return; }
        MayAlias = true;
        Access = LlvmModRefInfo.ModRef;
    }
    public LlvmAliasResult AliasesMemoryLocation(LlvmMemoryLocation location, LlvmBatchAAResults aa)
    {
        if (AliasAny) return LlvmAliasResult.MayAlias;
        foreach (var existing in Locations)
        {
            var result = aa.Alias(location, existing);
            if (result != LlvmAliasKind.NoAlias) return result;
        }
        foreach (var instruction in UnknownInstructions)
            if (aa.GetModRefInfo(instruction, location).IsModOrRefSet()) return LlvmAliasResult.MayAlias;
        return LlvmAliasResult.NoAlias;
    }
    public LlvmModRefInfo AliasesUnknownInst(LlvmInstruction instruction, LlvmBatchAAResults aa)
    {
        if (AliasAny) return LlvmModRefInfo.ModRef;
        if (!instruction.MayReadFromMemory() && !instruction.MayWriteToMemory()) return LlvmModRefInfo.NoModRef;
        foreach (var unknown in UnknownInstructions)
        {
            if (unknown is not LlvmCallBase first || instruction is not LlvmCallBase second
                || aa.GetModRefInfo(first, second).IsModOrRefSet() || aa.GetModRefInfo(second, first).IsModOrRefSet())
                return LlvmModRefInfo.ModRef;
        }
        LlvmModRefInfo result = 0;
        foreach (var location in Locations)
        {
            result |= aa.GetModRefInfo(instruction, location);
            if (result.IsModAndRefSet()) return result;
        }
        return result;
    }
}
