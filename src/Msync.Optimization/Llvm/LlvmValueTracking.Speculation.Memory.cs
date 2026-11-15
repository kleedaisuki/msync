// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/Loads.cpp
// isDereferenceableAndAlignedPointer; IR/Value.cpp getPointerDereferenceableBytes,
// getPointerAlignment; IR/Instructions.cpp AllocaInst::getAllocationSize.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsDereferenceableAndAlignedPointer(LlvmValue value, LlvmType type, LlvmAlign alignment,
        LlvmDataLayout layout, LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        bool ignoreFree = false, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        if (!type.IsSized() || type.IsScalableTy())
        { Count(metrics, "value_tracking.deref.unsized_type"); return false; }
        var size = new LlvmAPInt(layout.GetPointerTypeSizeInBits(value.Type), layout.GetTypeStoreSize(type).GetFixedValue());
        return IsDereferenceableAndAlignedPointer(value, alignment, size, layout, context, tree, ignoreFree, metrics, assumptions, allowEphemerals, conditions);
    }

    public static bool IsDereferenceableAndAlignedPointer(LlvmValue value, LlvmAlign alignment, LlvmAPInt size,
        LlvmDataLayout layout, LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        bool ignoreFree = false, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        Count(metrics, "value_tracking.deref.queries");
        var visited = new HashSet<LlvmValue>(ReferenceEqualityComparer.Instance);
        bool result = IsDereferenceableAndAlignedPointer(value, alignment, size, layout, context, tree, ignoreFree, visited, 16, metrics, assumptions, allowEphemerals, conditions);
        Count(metrics, result ? "value_tracking.deref.known" : "value_tracking.deref.no_fact");
        return result;
    }

    private static bool IsDereferenceableAndAlignedPointer(LlvmValue value, LlvmAlign alignment, LlvmAPInt size,
        LlvmDataLayout layout, LlvmInstruction? context, LlvmDominatorTree? tree, bool ignoreFree,
        HashSet<LlvmValue> visited, uint depth, Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null)
    {
        if (depth-- == 0) { Count(metrics, "value_tracking.deref.depth_limit"); return false; }
        if (!visited.Add(value)) { Count(metrics, "value_tracking.deref.visited"); return false; }
        Count(metrics, "value_tracking.deref.steps");
        bool Recurse(LlvmValue operand, LlvmAPInt access) => IsDereferenceableAndAlignedPointer(operand, alignment,
            access, layout, context, tree, ignoreFree, visited, depth, metrics, assumptions, allowEphemerals, conditions);
        if (value is LlvmGetElementPtrInstruction gep)
        {
            Count(metrics, "value_tracking.deref.gep");
            var offset = LlvmAPInt.GetZero(layout.GetIndexTypeSizeInBits(gep.Type));
            if (!AccumulateConstantOffset(gep, layout, ref offset, metrics)) return false;
            if (offset.IsNegative) { Count(metrics, "value_tracking.deref.negative_offset"); return false; }
            if (!offset.URem(new LlvmAPInt(offset.BitWidth, alignment.Value)).IsZero)
            { Count(metrics, "value_tracking.deref.offset_alignment"); return false; }
            return Recurse(gep.PointerOperand, offset + size.SExtOrTrunc(offset.BitWidth));
        }
        if (value is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } bitcast && bitcast.Operand.Type.IsPointerTy())
            return Recurse(bitcast.Operand, size);
        if (value is LlvmSelectInstruction select)
            return Recurse(select.TrueValue, size) && Recurse(select.FalseValue, size);
        ulong bytes = GetPointerDereferenceableBytes(value, layout, out bool canBeNull, out _, metrics);
        bool known = size.TryZExtValue() is { } amount && amount <= bytes;
        if (known && canBeNull) known = LlvmKnownBitsAnalysis.IsKnownNonZero(value, context: context, tree: tree, metrics: metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions);
        // HeapAlloc is non-speculatable, just like alloca, and live results cannot
        // be freed by represented IR operations. Its byte facts need no temporal scan.
        if (known && value is LlvmInstruction instruction && instruction is not (LlvmAllocaInstruction or LlvmHeapAllocInst))
            known = context is not null && IsValidAssumeForContext(instruction, context, tree, false, metrics);
        if (known)
        {
            bool aligned = GetPointerAlignment(value, layout, metrics).Value >= alignment.Value;
            Count(metrics, aligned ? "value_tracking.deref.base_aligned" : "value_tracking.deref.base_alignment");
            return aligned;
        }
        if (value is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } addressCast)
            return Recurse(addressCast.Operand, size);
        return IsDereferenceableAndAlignedViaAssumption(value, alignment, size, layout, context, tree, ignoreFree, metrics, assumptions, allowEphemerals, conditions);
    }

    public static ulong GetPointerDereferenceableBytes(LlvmValue value, LlvmDataLayout layout,
        out bool canBeNull, out bool canBeFreed, Dictionary<string, ulong>? metrics = null)
    {
        canBeNull = false; canBeFreed = false;
        if (value is LlvmAllocaInstruction alloca)
            return GetAllocationSize(alloca, layout)?.GetKnownMinValue() ?? 0;
        if (value is LlvmHeapAllocInst heap)
        {
            if (heap.ByteSize is LlvmConstantInt constant && constant.Value.TryZExtValue() is { } bytes)
            { Count(metrics, "value_tracking.deref.heap_bytes"); return bytes; }
            Count(metrics, "value_tracking.deref.heap_dynamic_size");
            return 0;
        }
        if (value is LlvmGlobalVariable global && global.ValueType.IsSized() && global.Linkage != LlvmLinkage.ExternalWeakLinkage)
            return layout.GetTypeStoreSize(global.ValueType).GetFixedValue();
        // Zero bytes, not guessed object sizes: no attrs/metadata exist on arguments, calls or loads.
        if (value is LlvmArgument or LlvmCallBase or LlvmLoadInstruction or LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr }) canBeNull = true;
        return 0;
    }

    public static LlvmTypeSize? GetAllocationSize(LlvmAllocaInstruction alloca, LlvmDataLayout layout)
    {
        var size = layout.GetTypeAllocSize(alloca.AllocatedType);
        if (size.IsZero || alloca.ArraySize is LlvmConstantInt { IsOne: true }) return size;
        if (alloca.ArraySize is not LlvmConstantInt count || count.Value.TryZExtValue() is not { } elements) return null;
        ulong bytes = size.GetKnownMinValue();
        if (elements != 0 && bytes > ulong.MaxValue / elements) return null;
        return new LlvmTypeSize(bytes * elements, size.IsScalable);
    }

    public static LlvmAlign GetPointerAlignment(LlvmValue value, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.alignment.queries");
        switch (value)
        {
            case LlvmFunction function:
                var pointerAlignment = layout.GetFunctionPtrAlign() ?? new(1);
                return layout.GetFunctionPtrAlignType() == LlvmFunctionPointerAlignType.Independent
                    ? pointerAlignment : LlvmAlign.Max(pointerAlignment, function.Alignment ?? new(1));
            case LlvmGlobalVariable global:
                if (global.Alignment is { } explicitAlignment) return explicitAlignment;
                if (!global.ValueType.IsSized()) return new(1);
                return global.IsStrongDefinitionForLinker ? layout.GetPreferredAlign(global) : layout.GetABITypeAlign(global.ValueType);
            case LlvmArgument argument:
                var attributes = argument.Parent?.GetParamAttributes((int)argument.ArgNo) ?? default;
                if (attributes.Alignment is { } alignment) return alignment;
                return attributes.StructRetType is { } structure && structure.IsSized()
                    ? layout.GetABITypeAlign(structure) : new(1);
            case LlvmAllocaInstruction alloca:
                return alloca.Alignment;
            case LlvmCallBase call:
                return call.GetRetAlign() ?? new(1);
            case LlvmHeapAllocInst heap:
                Count(metrics, "value_tracking.alignment.heap");
                return heap.Alignment;
            case LlvmConstantExpr { Opcode: LlvmOpcode.IntToPtr } expression
                when expression.GetOperand(0) is LlvmConstantInt integer:
                uint zeros = Math.Min(integer.Value.CountTrailingZeros(), LlvmValue.MaxAlignmentExponent);
                return new(1UL << (int)zeros);
            default:
                return new(1);
        }
    }
}
