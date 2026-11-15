// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SROA.cpp isIntegerWideningViable[ForSlice], extractInteger and insertInteger.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private static bool IsIntegerWideningViableForSlice(Slice slice, ulong allocationBegin, LlvmType allocationType,
        LlvmDataLayout layout, ref bool wholeAllocaOperation, Dictionary<string, ulong>? metrics)
    {
        ulong size = layout.GetTypeStoreSize(allocationType).GetFixedValue();
        ulong relativeBegin = unchecked(slice.BeginOffset - allocationBegin);
        ulong relativeEnd = unchecked(slice.EndOffset - allocationBegin);
        var instruction = (LlvmInstruction)slice.Use!.User;
        if (instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } intrinsic } &&
            (intrinsic.IsLifetimeStartOrEnd || instruction.IsDroppable())) return true;
        if (relativeEnd > size) return RejectType(metrics, "integer.reject.slice_past_allocation");
        switch (instruction)
        {
            case LlvmLoadInstruction load:
                if (load.IsVolatile) return RejectType(metrics, "integer.reject.volatile_load");
                var loadSize = layout.GetTypeStoreSize(load.Type);
                if (!loadSize.IsFixed || loadSize.GetFixedValue() > size) return RejectType(metrics, "integer.reject.load_size");
                if (slice.BeginOffset < allocationBegin) return RejectType(metrics, "integer.reject.load_split_tail");
                if (load.Type is not LlvmVectorType && relativeBegin == 0 && relativeEnd == size) wholeAllocaOperation = true;
                if (load.Type is LlvmIntegerType loadInteger)
                {
                    if (loadInteger.BitWidth < layout.GetTypeStoreSizeInBits(loadInteger).GetFixedValue()) return RejectType(metrics, "integer.reject.load_bit_padding");
                }
                else if (relativeBegin != 0 || relativeEnd != size || !CanConvertValue(layout, allocationType, load.Type))
                    return RejectType(metrics, "integer.reject.load_conversion");
                return true;
            case LlvmStoreInstruction store:
                var valueType = store.ValueOperand.Type;
                if (store.IsVolatile) return RejectType(metrics, "integer.reject.volatile_store");
                var storeSize = layout.GetTypeStoreSize(valueType);
                if (!storeSize.IsFixed || storeSize.GetFixedValue() > size) return RejectType(metrics, "integer.reject.store_size");
                if (slice.BeginOffset < allocationBegin) return RejectType(metrics, "integer.reject.store_split_tail");
                if (valueType is not LlvmVectorType && relativeBegin == 0 && relativeEnd == size) wholeAllocaOperation = true;
                if (valueType is LlvmIntegerType storeInteger)
                {
                    if (storeInteger.BitWidth < layout.GetTypeStoreSizeInBits(storeInteger).GetFixedValue()) return RejectType(metrics, "integer.reject.store_bit_padding");
                }
                else if (relativeBegin != 0 || relativeEnd != size || !CanConvertValue(layout, valueType, allocationType))
                    return RejectType(metrics, "integer.reject.store_conversion");
                return true;
            default:
                if (!LlvmMemIntrinsic.TryGet(instruction, out var memory)) return RejectType(metrics, "integer.reject.use_kind");
                if (memory.IsVolatile) return RejectType(metrics, "integer.reject.volatile_memory");
                if (memory.Length is not LlvmConstant) return RejectType(metrics, "integer.reject.dynamic_memory_length");
                return slice.IsSplittable || RejectType(metrics, "integer.reject.unsplittable_memory");
        }
    }

    internal static bool IsIntegerWideningViable(Partition partition, LlvmType allocationType, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics = null)
    {
        CountType(metrics, "integer.queries");
        ulong size = layout.GetTypeSizeInBits(allocationType).GetFixedValue();
        if (size > LlvmIntegerType.MaxIntBits) return RejectType(metrics, "integer.reject.maximum_bit_width");
        if (size != layout.GetTypeStoreSizeInBits(allocationType).GetFixedValue()) return RejectType(metrics, "integer.reject.allocation_bit_padding");
        var integer = LlvmIntegerType.Get(allocationType.Context, (uint)size);
        if (!CanConvertValue(layout, allocationType, integer) || !CanConvertValue(layout, integer, allocationType))
            return RejectType(metrics, "integer.reject.allocation_conversion");
        bool whole = partition.IsEmpty && layout.IsLegalInteger(size);
        foreach (var slice in partition)
        {
            CountType(metrics, "integer.slices");
            if (!IsIntegerWideningViableForSlice(slice, partition.BeginOffset, allocationType, layout, ref whole, metrics)) return false;
        }
        foreach (var slice in partition.SplitSliceTails)
        {
            CountType(metrics, "integer.split_tails");
            if (!IsIntegerWideningViableForSlice(slice, partition.BeginOffset, allocationType, layout, ref whole, metrics)) return false;
        }
        if (whole) CountType(metrics, "integer.viable");
        else CountType(metrics, "integer.reject.no_covering_operation");
        return whole;
    }

    internal static LlvmValue ExtractInteger(LlvmDataLayout layout, LlvmTransformBuilder builder, LlvmValue value,
        LlvmIntegerType type, ulong offset, string name)
    {
        var integer = (LlvmIntegerType)value.Type;
        Debug.Assert(layout.GetTypeStoreSize(type).GetFixedValue() + offset <= layout.GetTypeStoreSize(integer).GetFixedValue());
        ulong shift = 8 * offset;
        if (layout.IsBigEndian)
            shift = 8 * (layout.GetTypeStoreSize(integer).GetFixedValue() - layout.GetTypeStoreSize(type).GetFixedValue() - offset);
        if (shift != 0) value = builder.CreateBinary(LlvmOpcode.LShr, value, LlvmConstantInt.Get(integer, shift), name + ".shift");
        Debug.Assert(type.BitWidth <= integer.BitWidth);
        if (!ReferenceEquals(type, integer)) value = builder.CreateTrunc(value, type, name + ".trunc");
        return value;
    }

    internal static LlvmValue InsertInteger(LlvmDataLayout layout, LlvmTransformBuilder builder, LlvmValue oldValue,
        LlvmValue value, ulong offset, string name)
    {
        var integer = (LlvmIntegerType)oldValue.Type;
        var type = (LlvmIntegerType)value.Type;
        Debug.Assert(type.BitWidth <= integer.BitWidth);
        if (!ReferenceEquals(type, integer)) value = builder.CreateZExt(value, integer, name + ".ext");
        Debug.Assert(layout.GetTypeStoreSize(type).GetFixedValue() + offset <= layout.GetTypeStoreSize(integer).GetFixedValue());
        ulong shift = 8 * offset;
        if (layout.IsBigEndian)
            shift = 8 * (layout.GetTypeStoreSize(integer).GetFixedValue() - layout.GetTypeStoreSize(type).GetFixedValue() - offset);
        if (shift != 0) value = builder.CreateBinary(LlvmOpcode.Shl, value, LlvmConstantInt.Get(integer, shift), name + ".shift");
        if (shift != 0 || type.BitWidth < integer.BitWidth)
        {
            var mask = ~LlvmAPInt.GetLowBitsSet(integer.BitWidth, type.BitWidth).Shl((uint)shift);
            oldValue = builder.CreateAnd(oldValue, LlvmConstantInt.Get(integer.Context, mask), name + ".mask");
            value = builder.CreateOr(oldValue, value, name + ".insert");
        }
        return value;
    }
}
