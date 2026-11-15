// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: AliasAnalysis.cpp getModRefInfo/getSyncEffects.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmAAResults
{
    internal LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction, LlvmMemoryLocation? optional, LlvmAAQueryInfo query)
    {
        Count("aa.modref.queries");
        if (optional is null && instruction is LlvmCallBase) return GetMemoryEffects(instruction).GetModRef();
        var location = optional ?? new LlvmMemoryLocation();
        switch (instruction)
        {
            case LlvmLoadInstruction load:
                if (location.Ptr is not null && Alias(LlvmMemoryLocation.Get(load, Layout), location, query, load) == LlvmAliasKind.NoAlias)
                    return StrongerThanMonotonic(load.Ordering) ? SyncEffects(location, query) : LlvmModRefInfo.NoModRef;
                return StrongerThanUnordered(load.Ordering) ? LlvmModRefInfo.ModRef : LlvmModRefInfo.Ref;
            case LlvmStoreInstruction store:
                if (location.Ptr is not null)
                {
                    if (Alias(LlvmMemoryLocation.Get(store, Layout), location, query, store) == LlvmAliasKind.NoAlias)
                        return StrongerThanMonotonic(store.Ordering) ? SyncEffects(location, query) : LlvmModRefInfo.NoModRef;
                    if (!GetModRefInfoMask(location, query).IsModSet()) return LlvmModRefInfo.NoModRef;
                }
                return StrongerThanUnordered(store.Ordering) ? LlvmModRefInfo.ModRef : LlvmModRefInfo.Mod;
            case LlvmCallBase call:
                var result = CallModRef(call, location, query);
                return result.IsNoModRef() ? result : result & GetModRefInfoMask(location, query);
        }
        if (instruction.Opcode == LlvmOpcode.Fence)
            return location.Ptr is null ? LlvmModRefInfo.ModRef : SyncEffects(location, query);
        if (instruction is LlvmHeapAllocInst && location.Ptr is not null)
        {
            // Allocation touches allocator state, not bytes of an accessible object.
            // The returned bytes are uninitialized, not an implicit store.
            Count("aa.heap.accessible_location_no_modref");
            return LlvmModRefInfo.NoModRef;
        }
        if (instruction.Opcode is LlvmOpcode.HeapAlloc or LlvmOpcode.FileIn or LlvmOpcode.FileOut)
        {
            // Locationless queries retain the actual Core effects contract.
            // These effects do not invent a specific accessible-memory location.
            return (instruction.MayReadFromMemory() ? LlvmModRefInfo.Ref : 0)
                | (instruction.MayWriteToMemory() ? LlvmModRefInfo.Mod : 0);
        }
        Debug.Assert(!instruction.MayReadFromMemory() && !instruction.MayWriteToMemory());
        return LlvmModRefInfo.NoModRef;
    }
    internal LlvmModRefInfo GetModRefInfo(LlvmInstruction instruction, LlvmCallBase call, LlvmAAQueryInfo query)
    {
        if (instruction is LlvmCallBase first) return CallsModRef(first, call, query);
        if (instruction.Opcode == LlvmOpcode.Fence) return LlvmModRefInfo.ModRef;
        if (instruction.Opcode is LlvmOpcode.HeapAlloc or LlvmOpcode.FileIn or LlvmOpcode.FileOut)
            return instruction.MayReadFromMemory() || instruction.MayWriteToMemory() ? LlvmModRefInfo.ModRef : LlvmModRefInfo.NoModRef;
        var location = LlvmMemoryLocation.Get(instruction, Layout);
        return GetModRefInfo(call, location, query).IsModOrRefSet() ? LlvmModRefInfo.ModRef : LlvmModRefInfo.NoModRef;
    }
    internal LlvmModRefInfo GetModRefInfo(LlvmInstruction first, LlvmInstruction second, LlvmAAQueryInfo query)
    {
        if ((!first.MayReadFromMemory() && !first.MayWriteToMemory())
            || (!second.MayReadFromMemory() && !second.MayWriteToMemory())) return LlvmModRefInfo.NoModRef;
        if ((first is LlvmHeapAllocInst && second is LlvmLoadInstruction or LlvmStoreInstruction)
            || (second is LlvmHeapAllocInst && first is LlvmLoadInstruction or LlvmStoreInstruction))
        { Count("aa.heap.ordinary_access_no_modref"); return LlvmModRefInfo.NoModRef; }
        if (second is LlvmCallBase call) return GetModRefInfo(first, call, query);
        var result = GetModRefInfo(first, LlvmMemoryLocation.GetOrNone(second, Layout), query);
        return result.IsModOrRefSet() ? LlvmModRefInfo.ModRef : LlvmModRefInfo.NoModRef;
    }
    private LlvmModRefInfo SyncEffects(LlvmMemoryLocation location, LlvmAAQueryInfo query)
    {
        if (location.Ptr is null) return LlvmModRefInfo.ModRef;
        var value = LlvmValueTracking.GetUnderlyingObject(location.Ptr, metrics: Metrics);
        var captured = query.Captures.GetCapturesBefore(value, null, true, true);
        if (captured.CapturesNothing()) return LlvmModRefInfo.NoModRef;
        var result = captured.CapturesReadProvenanceOnly() ? LlvmModRefInfo.Ref : LlvmModRefInfo.ModRef;
        return result & GetModRefInfoMask(location, query);
    }
    private static bool StrongerThanUnordered(LlvmAtomicOrdering ordering) => ordering is not (LlvmAtomicOrdering.NotAtomic or LlvmAtomicOrdering.Unordered);
    private static bool StrongerThanMonotonic(LlvmAtomicOrdering ordering) => ordering is not (LlvmAtomicOrdering.NotAtomic or LlvmAtomicOrdering.Unordered or LlvmAtomicOrdering.Monotonic);
    private LlvmModRefInfo CallModRef(LlvmCallBase call, LlvmMemoryLocation location, LlvmAAQueryInfo query)
    {
        var value = LlvmValueTracking.GetUnderlyingObject(location.Ptr!, metrics: Metrics);
        if (value is LlvmAllocaInstruction && call is LlvmCallInstruction { TailCallKind: LlvmTailCallKind.Tail or LlvmTailCallKind.MustTail }
            && !HasByValArgument(call))
        { Count("aa.call_location.tail_frame"); return LlvmModRefInfo.NoModRef; }
        var effects = GetMemoryEffects(call).WithoutLoc(LlvmIRMemLocation.InaccessibleMem);
        if (effects.DoesNotAccessMemory) return LlvmModRefInfo.NoModRef;
        var argument = effects.GetModRef(LlvmIRMemLocation.ArgMem);
        var other = effects.GetModRef(LlvmIRMemLocation.Other);
        var errno = effects.GetModRef(LlvmIRMemLocation.ErrnoMem);
        // Inline assembly is not represented; ordinary calls use the source
        // nonescaping-object rule, including its returns_twice exception.
        if (other.IsModOrRefSet() && value is not LlvmConstant && !ReferenceEquals(call, value)
            && (value is LlvmAllocaInstruction || !call.HasReturnsTwice))
        {
            var captured = query.Captures.GetCapturesBefore(value, call, false, false);
            if (captured.CapturesNothing()) other = LlvmModRefInfo.NoModRef;
            else if (captured.CapturesReadProvenanceOnly()) other = LlvmModRefInfo.Ref;
        }
        if ((argument | other) != other)
        {
            LlvmModRefInfo refined = 0;
            for (int index = 0; index < call.OperandCount - 1; ++index)
            {
                var operand = call.GetOperand(index)!;
                if (!operand.Type.IsPointerTy()) continue;
                var argLocation = index < call.ArgumentCount ? LlvmMemoryLocation.GetForArgument(call, index)
                    : LlvmMemoryLocation.GetBeforeOrAfter(operand);
                if (Alias(argLocation, location, query, call) != LlvmAliasKind.NoAlias)
                    refined |= argument & GetArgModRefInfo(call, index);
                Count("aa.call_location.pointer_arguments");
                if (refined == argument) break;
            }
            argument = refined;
        }
        var result = argument | other;
        if ((errno | result) != result && AliasErrno(location) != LlvmAliasKind.NoAlias) result |= errno;
        if (result == LlvmModRefInfo.ModRef &&
            call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.InvariantStart)
        { Count("aa.call_location.invariant_start_read"); return LlvmModRefInfo.Ref; }
        return result;
    }
    private LlvmAliasResult AliasErrno(LlvmMemoryLocation location)
    {
        var value = LlvmValueTracking.GetUnderlyingObject(location.Ptr!, metrics: Metrics);
        if (LlvmBasicAliasAnalysis.IsIdentifiedFunctionLocal(value)) return LlvmAliasResult.NoAlias;
        if (value is LlvmGlobalVariable { HasLocalLinkage: true }) return LlvmAliasResult.NoAlias;
        // No TargetLibraryInfo int-size/errno-function facts or no-builtins
        // function attribute are represented in the current IR query context.
        return LlvmAliasResult.MayAlias;
    }
    private static bool HasByValArgument(LlvmCallBase call)
    {
        for (int index = 0; index < call.ArgumentCount; ++index)
            if (call.IsByValArgument(index)) return true;
        return false;
    }
    private LlvmModRefInfo CallsModRef(LlvmCallBase first, LlvmCallBase second, LlvmAAQueryInfo query)
    {
        Count("aa.call_pair.queries");
        var firstEffects = GetMemoryEffects(first);
        var secondEffects = GetMemoryEffects(second);
        if (firstEffects.DoesNotAccessMemory || secondEffects.DoesNotAccessMemory)
        { Count("aa.call_pair.no_access"); return LlvmModRefInfo.NoModRef; }
        if (firstEffects.OnlyReadsMemory && secondEffects.OnlyReadsMemory)
        { Count("aa.call_pair.read_only"); return LlvmModRefInfo.NoModRef; }
        LlvmModRefInfo result = firstEffects.OnlyReadsMemory ? LlvmModRefInfo.Ref
            : firstEffects.OnlyWritesMemory ? LlvmModRefInfo.Mod : LlvmModRefInfo.ModRef;
        if (secondEffects.OnlyAccessesArgPointees)
        {
            Count("aa.call_pair.second_argmem");
            if (!secondEffects.DoesAccessArgPointees) return LlvmModRefInfo.NoModRef;
            LlvmModRefInfo refined = LlvmModRefInfo.NoModRef;
            for (int index = 0; index < second.ArgumentCount; ++index)
            {
                if (!second.GetArgument(index).Type.IsPointerTy()) continue;
                var location = LlvmMemoryLocation.GetForArgument(second, index);
                var effects = GetArgModRefInfo(second, index);
                var mask = effects.IsModSet() ? LlvmModRefInfo.ModRef
                    : effects.IsRefSet() ? LlvmModRefInfo.Mod : LlvmModRefInfo.NoModRef;
                mask &= GetModRefInfo(first, location, query);
                refined = (refined | mask) & result;
                Count("aa.call_pair.pointer_arguments");
                if (refined == result) break;
            }
            return refined;
        }
        if (firstEffects.OnlyAccessesArgPointees)
        {
            Count("aa.call_pair.first_argmem");
            if (!firstEffects.DoesAccessArgPointees) return LlvmModRefInfo.NoModRef;
            LlvmModRefInfo refined = LlvmModRefInfo.NoModRef;
            for (int index = 0; index < first.ArgumentCount; ++index)
            {
                if (!first.GetArgument(index).Type.IsPointerTy()) continue;
                var location = LlvmMemoryLocation.GetForArgument(first, index);
                var effects = GetArgModRefInfo(first, index);
                var other = GetModRefInfo(second, location, query);
                if (effects.IsModSet() && other.IsModOrRefSet() || effects.IsRefSet() && other.IsModSet())
                    refined = (refined | effects) & result;
                Count("aa.call_pair.pointer_arguments");
                if (refined == result) break;
            }
            return refined;
        }
        if (firstEffects.OnlyAccessesInaccessibleOrTargetMem && secondEffects.OnlyAccessesInaccessibleOrTargetMem)
        {
            Count("aa.call_pair.inaccessible_target_regions");
            return InaccessibleAndTargetModRef(firstEffects, secondEffects);
        }
        return result;
    }

    public LlvmModRefInfo GetArgModRefInfo(LlvmCallBase call, int argument) =>
        call.DoesNotAccessMemory(argument) ? LlvmModRefInfo.NoModRef
        : call.OnlyWritesMemory(argument) ? LlvmModRefInfo.Mod
        : call.OnlyReadsMemory(argument) ? LlvmModRefInfo.Ref : LlvmModRefInfo.ModRef;

    private static LlvmModRefInfo InaccessibleAndTargetModRef(LlvmMemoryEffects use, LlvmMemoryEffects definition)
    {
        LlvmModRefInfo result = LlvmModRefInfo.NoModRef;
        Add(LlvmIRMemLocation.InaccessibleMem);
        Add(LlvmIRMemLocation.TargetMem0);
        Add(LlvmIRMemLocation.TargetMem1);
        return result;

        void Add(LlvmIRMemLocation location)
        {
            var first = use.GetModRef(location);
            var second = definition.GetModRef(location);
            if (first.IsNoModRef() || second.IsNoModRef() || first == LlvmModRefInfo.Ref && second == first) return;
            result |= first;
        }
    }
}
