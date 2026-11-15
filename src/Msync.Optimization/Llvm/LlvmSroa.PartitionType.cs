// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp tryCanonicalizeStructToVector and selectPartitionType.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private static LlvmFixedVectorType? TryCanonicalizeStructToVector(LlvmStructType structure,
        Partition partition, LlvmDataLayout layout)
    {
        Debug.Assert(structure.Elements.Count > 0);
        LlvmType element = structure.Elements[0];
        if (structure.Elements.Any(type => type != element)) return null;
        bool integralPointer = element.IsPointerTy() && !layout.IsNonIntegralPointerType(element);
        if (!element.IsIntegerTy() && !element.IsFloatingPointTy() && !integralPointer) return null;
        if (layout.GetTypeSizeInBits(element) != layout.GetTypeAllocSizeInBits(element)) return null;
        var vector = LlvmFixedVectorType.Get(element, (uint)structure.Elements.Count);
        if (layout.GetStructLayout(structure).SizeInBytes != layout.GetTypeStoreSize(vector)) return null;
        foreach (Slice slice in partition)
            if (!IsIgnorableOrMemIntrinsicSlice(slice)) return null;
        foreach (Slice slice in partition.SplitSliceTails)
            if (!IsIgnorableOrMemIntrinsicSlice(slice)) return null;
        return vector;
    }

    private static bool IsIgnorableOrMemIntrinsicSlice(Slice slice)
    {
        if (slice.IsDead || slice.Use is null) return true;
        return slice.Use.User is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor }
            && (descriptor.IsMemoryIntrinsic || descriptor.IsLifetimeIntrinsic);
    }

    internal sealed partial class State
    {
        private (LlvmType Type, bool IntegerWidening, LlvmVectorType? Vector) SelectPartitionType(
            Partition partition, LlvmAllocaInstruction allocation)
        {
            LlvmVectorType? vector = IsVectorPromotionViable(partition, DL, allocation.Parent!.Parent!.GetVScaleValue(), Metrics);
            if (vector is not null && vector.ElementType.IsFloatingPointTy() && vector.ElementCount.GetFixedValue() > 1)
                return SelectedPartition("direct_fp_vector", partition, vector, false, vector);
            var (common, largestInteger) = FindCommonType(partition, partition.EndOffset);
            if (common is not null)
            {
                LlvmTypeSize size = DL.GetTypeAllocSize(common);
                if (!size.IsScalable && size.GetFixedValue() >= partition.Size)
                {
                    if (vector is not null) return SelectedPartition("common_type_vector", partition, vector, false, vector);
                    return SelectedPartition("common_type", partition, common, IsIntegerWideningViable(partition, common, DL, Metrics), null);
                }
            }
            LlvmType? originalPartition = GetTypePartition(DL, allocation.AllocatedType, partition.BeginOffset, partition.Size, Metrics);
            if (originalPartition is not null)
            {
                if (originalPartition is LlvmArrayType { ElementType: LlvmIntegerType } && DL.IsLegalInteger(partition.Size * 8))
                    originalPartition = LlvmIntegerType.Get(C, unchecked((uint)(partition.Size * 8)));
                if (IsIntegerWideningViable(partition, originalPartition, DL, Metrics))
                    return SelectedPartition("type_partition_integer_widening", partition, originalPartition, true, null);
                if (vector is not null) return SelectedPartition("type_partition_vector", partition, vector, false, vector);
                if (largestInteger is not null && DL.GetTypeAllocSize(largestInteger).GetFixedValue() >= partition.Size
                    && IsIntegerWideningViable(partition, largestInteger, DL, Metrics))
                    return SelectedPartition("largest_integer_widening", partition, largestInteger, true, null);
                if (AggregateToVector && originalPartition is LlvmStructType structure
                    && TryCanonicalizeStructToVector(structure, partition, DL) is { } canonical)
                    return SelectedPartition("struct_fallback_vector", partition, canonical, false, null);
                return SelectedPartition("type_partition_fallback", partition, originalPartition, false, null);
            }
            if (largestInteger is not null && DL.GetTypeAllocSize(largestInteger).GetFixedValue() >= partition.Size)
                return SelectedPartition("largest_integer_fallback", partition, largestInteger, false, null);
            if (DL.IsLegalInteger(partition.Size * 8))
                return SelectedPartition("legal_integer_fallback", partition,
                    LlvmIntegerType.Get(C, unchecked((uint)(partition.Size * 8))), false, null);
            return SelectedPartition("byte_array_fallback", partition,
                LlvmArrayType.Get(LlvmType.GetInt8Ty(C), partition.Size), false, null);
        }

        private (LlvmType Type, bool IntegerWidening, LlvmVectorType? Vector) SelectedPartition(string path,
            Partition partition, LlvmType type, bool widening, LlvmVectorType? vector)
        {
            if (Metrics is not null)
            {
                Count("partition.type." + path);
                Count("partition.type.kind." + type.Id);
                Count("partition.type.bytes", partition.Size);
                if (type is LlvmIntegerType integer) Count("partition.type.integer_bits", integer.BitWidth);
                if (vector is not null) Count("partition.type.vector_elements", vector.ElementCount.KnownMinValue);
            }
            return (type, widening, vector);
        }
    }
}
