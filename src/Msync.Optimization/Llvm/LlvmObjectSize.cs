// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MemoryBuiltins.cpp
// getBaseObjectSize; IR/Instructions.cpp AllocaInst::getAllocationSize;
// IR/Globals.cpp GlobalVariable::getGlobalSize.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static class LlvmObjectSize
{
    public static LlvmTypeSize? GetBaseObjectSize(LlvmValue value, LlvmDataLayout layout,
        bool nullIsUnknownSize = false, bool roundToAlign = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "object_size.base.queries");
        if (value is LlvmUndefValue)
        { Count(metrics, "object_size.base.undef"); return LlvmTypeSize.GetZero(); }
        if (value is LlvmConstantPointerNull)
        {
            if (nullIsUnknownSize || ((LlvmPointerType)value.Type).AddressSpace != 0)
            { Count(metrics, "object_size.base.null_unknown"); return null; }
            Count(metrics, "object_size.base.null_zero");
            return LlvmTypeSize.GetZero();
        }
        if (value is LlvmGlobalVariable global)
        {
            if (!global.ValueType.IsSized() || global.Linkage == LlvmLinkage.ExternalWeakLinkage ||
                !global.HasInitializer || global.IsInterposable)
            { Count(metrics, "object_size.base.global_no_size"); return null; }
            Count(metrics, "object_size.base.global");
            // Explicit GlobalObject alignment is not represented, so getAlign()
            // follows the source's absent-MaybeAlign path.
            return LlvmTypeSize.GetFixed(layout.GetTypeAllocSize(global.ValueType).GetFixedValue());
        }
        if (value is LlvmAllocaInstruction alloca)
        {
            var size = GetAllocationSize(alloca, layout, metrics);
            if (size is null) return null;
            Count(metrics, "object_size.base.alloca");
            return roundToAlign && !size.Value.IsScalable
                ? LlvmTypeSize.GetFixed(alloca.Alignment.AlignTo(size.Value.GetFixedValue())) : size;
        }
        if (value is LlvmHeapAllocInst heap)
        {
            if (heap.ByteSize is not LlvmConstantInt constant || constant.Value.TryZExtValue() is not { } bytes)
            { Count(metrics, "object_size.base.heap_dynamic_size"); return null; }
            Count(metrics, "object_size.base.heap");
            return LlvmTypeSize.GetFixed(roundToAlign ? heap.Alignment.AlignTo(bytes) : bytes);
        }
        // Arguments have no byval/inalloca/preallocated memory-type attributes;
        // calls have no allocsize/allockind attributes or TLI. These source
        // queries therefore have no allocation-size fact. HeapAlloc's explicit
        // operand above supplies its own extension fact, not call attributes.
        Count(metrics, "object_size.base.no_fact");
        return null;
    }

    private static LlvmTypeSize? GetAllocationSize(LlvmAllocaInstruction alloca, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics)
    {
        var size = layout.GetTypeAllocSize(alloca.AllocatedType);
        if (size.IsZero) return size;
        if (alloca.ArraySize is LlvmConstantInt { IsOne: true }) return size;
        if (alloca.ArraySize is not LlvmConstantInt constant || constant.Value.TryZExtValue() is not { } count)
        { Count(metrics, "object_size.alloca.dynamic_count"); return null; }
        Debug.Assert(!size.IsScalable);
        ulong element = size.GetKnownMinValue();
        if (count != 0 && element > ulong.MaxValue / count)
        { Count(metrics, "object_size.alloca.product_overflow"); return null; }
        Count(metrics, "object_size.alloca.array");
        return LlvmTypeSize.GetFixed(element * count);
    }

    private static void Count(Dictionary<string, ulong>? metrics, string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
