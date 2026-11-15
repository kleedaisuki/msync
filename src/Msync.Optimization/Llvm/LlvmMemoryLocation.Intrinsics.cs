// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/MemoryLocation.cpp getForSource/getForDest/getForArgument memory cases.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public readonly partial record struct LlvmMemoryLocation
{
    public static LlvmMemoryLocation GetForDest(LlvmMemIntrinsic memory) => MemoryArgument(memory, memory.RawDest);
    public static LlvmMemoryLocation GetForSource(LlvmMemIntrinsic memory)
    { Debug.Assert(memory.IsMemTransfer); return MemoryArgument(memory, memory.RawSource); }
    public static LlvmMemoryLocation GetForArgument(LlvmCallBase call, int argument,
        LlvmTargetLibraryInfo? library = null)
    {
        var pointer = call.GetArgument(argument);
        var descriptor = call.CalledFunction?.IntrinsicDescriptor;
        if (LlvmMemIntrinsic.TryGet(call, out var memory)) return MemoryArgument(memory, pointer);
        if (descriptor?.IsLifetimeStartOrEnd == true)
        {
            Debug.Assert(argument == 0);
            if (pointer is not LlvmAllocaInstruction alloca) return GetBeforeOrAfter(pointer);
            var size = LlvmValueTracking.GetAllocationSize(alloca, call.Parent!.Module!.DataLayout);
            return new(pointer, size is { } known ? LlvmLocationSize.Precise(known) : LlvmLocationSize.AfterPointer());
        }
        if (descriptor?.Kind == LlvmIntrinsicKind.InvariantStart)
        {
            Debug.Assert(argument == 1);
            return new(pointer, ((LlvmConstantInt)call.GetArgument(0)).Value.GetZExtValue());
        }
        if (descriptor?.Kind == LlvmIntrinsicKind.InvariantEnd)
        {
            if (argument == 0) return new(pointer, 0UL);
            Debug.Assert(argument == 2);
            return new(pointer, ((LlvmConstantInt)call.GetArgument(1)).Value.GetZExtValue());
        }
        // Native TLI is disableAllFunctions: no ordinary symbol gets a libc contract.
        return GetBeforeOrAfter(pointer);
    }
    public static LlvmMemoryLocation? GetForDest(LlvmCallBase call, LlvmTargetLibraryInfo? library = null)
    {
        // Source WriteME.onlyAccessesArgPointees ignores all read effects.
        if ((call.GetMemoryEffects().Raw & 0xaaaU & ~2U) != 0 || call.HasOperandBundles) return null;
        LlvmValue? used = null;
        int? usedIndex = null;
        for (int index = 0; index < call.ArgumentCount; ++index)
        {
            var argument = call.GetArgument(index);
            if (!argument.Type.IsPointerTy() || call.OnlyReadsMemory(index)) continue;
            if (used is null) { used = argument; usedIndex = index; continue; }
            usedIndex = null;
            if (!ReferenceEquals(used, argument)) return null;
        }
        if (used is null) return null;
        return usedIndex is { } single ? GetForArgument(call, single, library) : GetBeforeOrAfter(used);
    }
    private static LlvmMemoryLocation MemoryArgument(LlvmMemIntrinsic memory, LlvmValue pointer)
    {
        // This pin's getForArgument has no memset.inline case; preserve its unknown-size result.
        if (memory.Kind == LlvmIntrinsicKind.MemSetInline) return GetBeforeOrAfter(pointer);
        if (memory.Length is LlvmConstantInt length) return new(pointer, length.Value.GetZExtValue());
        return GetAfter(pointer);
    }
}
