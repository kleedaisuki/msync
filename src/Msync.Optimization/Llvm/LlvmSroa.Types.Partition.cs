// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SROA.cpp stripAggregateTypeWrapping / getTypePartition.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal static LlvmType StripAggregateTypeWrapping(LlvmDataLayout layout, LlvmType type)
    {
        if (type.IsSingleValueType()) return type;
        ulong allocated = layout.GetTypeAllocSize(type).GetFixedValue();
        ulong size = layout.GetTypeSizeInBits(type).GetFixedValue();
        LlvmType inner;
        switch (type)
        {
            case LlvmArrayType array: inner = array.ElementType; break;
            case LlvmStructType structure:
                uint field = layout.GetStructLayout(structure).GetElementContainingOffset(0);
                inner = structure.Elements[(int)field];
                break;
            default: return type;
        }
        if (allocated > layout.GetTypeAllocSize(inner).GetFixedValue() || size > layout.GetTypeSizeInBits(inner).GetFixedValue()) return type;
        return StripAggregateTypeWrapping(layout, inner);
    }

    internal static LlvmType? GetTypePartition(LlvmDataLayout layout, LlvmType type, ulong offset, ulong size,
        Dictionary<string, ulong>? metrics = null)
    {
        CountType(metrics, "partition.queries");
        ulong allocated = layout.GetTypeAllocSize(type).GetFixedValue();
        if (offset == 0 && allocated == size) return StripAggregateTypeWrapping(layout, type);
        if (offset > allocated || allocated - offset < size) return null;
        if (type is LlvmArrayType or LlvmVectorType)
        {
            LlvmType element;
            ulong count;
            if (type is LlvmArrayType array) { element = array.ElementType; count = array.NumElements; }
            else { var vector = (LlvmFixedVectorType)type; element = vector.ElementType; count = vector.NumElements; }
            ulong elementSize = layout.GetTypeAllocSize(element).GetFixedValue();
            ulong skipped = offset / elementSize;
            if (skipped >= count) return null;
            offset -= skipped * elementSize;
            if (offset > 0 || size < elementSize)
                return offset + size > elementSize ? null : GetTypePartition(layout, element, offset, size, metrics);
            Debug.Assert(offset == 0);
            if (size == elementSize) return StripAggregateTypeWrapping(layout, element);
            Debug.Assert(size > elementSize);
            ulong elements = size / elementSize;
            if (elements * elementSize != size) return null;
            return LlvmArrayType.Get(element, elements);
        }
        if (type is not LlvmStructType structure) return null;
        var physical = layout.GetStructLayout(structure);
        if (physical.SizeInBits.IsScalable || offset >= physical.SizeInBytes.GetFixedValue()) return null;
        ulong end = offset + size;
        if (end > physical.SizeInBytes.GetFixedValue()) return null;
        uint index = physical.GetElementContainingOffset(offset);
        offset -= physical.GetElementOffset(index).GetFixedValue();
        var fieldType = structure.Elements[(int)index];
        ulong fieldSize = layout.GetTypeAllocSize(fieldType).GetFixedValue();
        if (offset >= fieldSize) { CountType(metrics, "partition.alignment_padding"); return null; }
        if (offset > 0 || size < fieldSize)
            return offset + size > fieldSize ? null : GetTypePartition(layout, fieldType, offset, size, metrics);
        Debug.Assert(offset == 0);
        if (size == fieldSize) return StripAggregateTypeWrapping(layout, fieldType);
        uint endIndex = (uint)structure.NumElements;
        if (end < physical.SizeInBytes.GetFixedValue())
        {
            endIndex = physical.GetElementContainingOffset(end);
            if (index == endIndex || physical.GetElementOffset(endIndex).GetFixedValue() != end) return null;
            Debug.Assert(index < endIndex);
        }
        var fields = structure.Elements.Skip((int)index).Take((int)(endIndex - index)).ToArray();
        var subtype = LlvmStructType.Get(type.Context, fields, structure.IsPacked);
        if (size != layout.GetStructLayout(subtype).SizeInBytes.GetFixedValue()) return null;
        CountType(metrics, "partition.substructure");
        return subtype;
    }
}
