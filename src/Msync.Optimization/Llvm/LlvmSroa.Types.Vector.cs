// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SROA.cpp vector-promotion candidate collection, ordering and per-slice checks.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private static bool IsVectorPromotionViableForSlice(Partition partition, Slice slice, LlvmVectorType type,
        ulong elementSize, LlvmDataLayout layout, uint vscale, Dictionary<string, ulong>? metrics)
    {
        ulong begin = Math.Max(slice.BeginOffset, partition.BeginOffset) - partition.BeginOffset;
        ulong beginIndex = begin / elementSize;
        var vector = (LlvmFixedVectorType)type;
        if (beginIndex * elementSize != begin || beginIndex >= vector.NumElements) return RejectType(metrics, "vector.reject.begin_offset");
        ulong end = Math.Min(slice.EndOffset, partition.EndOffset) - partition.BeginOffset;
        ulong endIndex = end / elementSize;
        if (endIndex * elementSize != end || endIndex > vector.NumElements) return RejectType(metrics, "vector.reject.end_offset");
        Debug.Assert(endIndex > beginIndex);
        ulong elements = endIndex - beginIndex;
        LlvmType sliceType = elements == 1 ? vector.ElementType : LlvmFixedVectorType.Get(vector.ElementType, (uint)elements);
        var splitInteger = LlvmIntegerType.Get(type.Context, (uint)(elements * elementSize * 8));
        var instruction = (LlvmInstruction)slice.Use!.User;
        if (LlvmMemIntrinsic.TryGet(instruction, out var memory))
        {
            if (memory.IsVolatile) return RejectType(metrics, "vector.reject.volatile_memory");
            if (!slice.IsSplittable) return RejectType(metrics, "vector.reject.unsplittable_memory");
            if (memory.IsMemSet)
            {
                var splat = LlvmIntegerType.Get(type.Context, (uint)(elementSize * 8));
                if (!CanConvertValue(layout, splat, vector.ElementType, vscale)) return RejectType(metrics, "vector.reject.memset_splat_conversion");
            }
            return true;
        }
        if (instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor })
            return descriptor.IsLifetimeStartOrEnd || instruction.IsDroppable() || RejectType(metrics, "vector.reject.intrinsic_kind");
        if (instruction is LlvmLoadInstruction load)
        {
            if (load.IsVolatile) return RejectType(metrics, "vector.reject.volatile_load");
            if (load.Type.IsStructTy()) return RejectType(metrics, "vector.reject.aggregate_load");
            LlvmType loaded = load.Type;
            if (partition.BeginOffset > slice.BeginOffset || partition.EndOffset < slice.EndOffset)
            { Debug.Assert(loaded.IsIntegerTy()); loaded = splitInteger; }
            return CanConvertValue(layout, sliceType, loaded, vscale) || RejectType(metrics, "vector.reject.load_conversion");
        }
        if (instruction is LlvmStoreInstruction store)
        {
            LlvmType stored = store.ValueOperand.Type;
            if (store.IsVolatile) return RejectType(metrics, "vector.reject.volatile_store");
            if (stored.IsStructTy()) return RejectType(metrics, "vector.reject.aggregate_store");
            if (partition.BeginOffset > slice.BeginOffset || partition.EndOffset < slice.EndOffset)
            { Debug.Assert(stored.IsIntegerTy()); stored = splitInteger; }
            return CanConvertValue(layout, stored, sliceType, vscale) || RejectType(metrics, "vector.reject.store_conversion");
        }
        return RejectType(metrics, "vector.reject.use_kind");
    }

    private static LlvmVectorType? CheckVectorTypesForPromotion(Partition partition, LlvmDataLayout layout,
        List<LlvmVectorType> candidates, bool haveCommonElement, LlvmType? commonElement,
        bool havePointer, bool haveCommonPointer, LlvmVectorType? commonPointer, uint vscale,
        Dictionary<string, ulong>? metrics)
    {
        if (candidates.Count == 0) { CountType(metrics, "vector.reject.no_candidates"); return null; }
        if (havePointer && !haveCommonPointer) { CountType(metrics, "vector.reject.pointer_element_conflict"); return null; }
        if (!haveCommonElement && havePointer)
        { candidates.Clear(); candidates.Add(commonPointer!); }
        else if (!haveCommonElement)
        {
            for (int index = 0; index < candidates.Count; ++index)
            {
                var vector = candidates[index];
                if (!vector.ElementType.IsIntegerTy())
                    candidates[index] = LlvmVectorType.Get(LlvmIntegerType.Get(vector.Context, vector.GetScalarSizeInBits()), vector.ElementCount);
            }
            candidates.Sort(static (left, right) => ((LlvmFixedVectorType)left).NumElements.CompareTo(((LlvmFixedVectorType)right).NumElements));
            for (int index = candidates.Count - 1; index > 0; --index)
                if (((LlvmFixedVectorType)candidates[index]).NumElements == ((LlvmFixedVectorType)candidates[index - 1]).NumElements)
                    candidates.RemoveAt(index);
        }
        else
        {
            Debug.Assert(candidates.All(vector => ReferenceEquals(vector.ElementType, commonElement) && ReferenceEquals(vector, candidates[0])));
            if (candidates.Count > 1) candidates.RemoveRange(1, candidates.Count - 1);
        }
        int oversized = candidates.RemoveAll(vector => ((LlvmFixedVectorType)vector).NumElements > ushort.MaxValue);
        if (oversized != 0) CountType(metrics, "vector.reject.maximum_lane_count", (ulong)oversized);
        foreach (var type in candidates)
        {
            CountType(metrics, "vector.candidates");
            ulong size = layout.GetTypeSizeInBits(type.ElementType).GetFixedValue();
            if (size % 8 != 0) { CountType(metrics, "vector.reject.element_bit_padding"); continue; }
            Debug.Assert(layout.GetTypeSizeInBits(type).GetFixedValue() % 8 == 0);
            bool viable = true;
            foreach (var slice in partition)
            {
                CountType(metrics, "vector.slices");
                if (IsVectorPromotionViableForSlice(partition, slice, type, size / 8, layout, vscale, metrics)) continue;
                viable = false; break;
            }
            if (!viable) continue;
            foreach (var slice in partition.SplitSliceTails)
            {
                CountType(metrics, "vector.split_tails");
                if (IsVectorPromotionViableForSlice(partition, slice, type, size / 8, layout, vscale, metrics)) continue;
                viable = false; break;
            }
            if (!viable) continue;
            CountType(metrics, "vector.viable");
            return type;
        }
        return null;
    }

    internal static LlvmVectorType? IsVectorPromotionViable(Partition partition, LlvmDataLayout layout,
        uint vscale, Dictionary<string, ulong>? metrics = null)
    {
        CountType(metrics, "vector.queries");
        List<LlvmVectorType> candidates = [];
        OrderedSet<LlvmType> memoryTypes = new(), deferredTypes = new();
        LlvmType? commonElement = null;
        LlvmVectorType? commonPointer = null;
        bool havePointer = false, haveCommonElement = true, haveCommonPointer = true;
        foreach (var slice in partition)
        {
            LlvmType? type = slice.Use!.User switch
            {
                LlvmLoadInstruction load => load.Type,
                LlvmStoreInstruction store => store.ValueOperand.Type,
                _ => null
            };
            if (type is null) continue;
            if (type.GetScalarType().IsPointerTy() &&
                (slice.BeginOffset != partition.BeginOffset || slice.EndOffset != partition.EndOffset))
            { deferredTypes.Add(type); CountType(metrics, "vector.pointer_types_deferred"); continue; }
            memoryTypes.Add(type);
            if (slice.BeginOffset == partition.BeginOffset && slice.EndOffset == partition.EndOffset) CheckCandidate(type);
        }
        LlvmVectorType[] initialCandidates = candidates.ToArray();
        if (CreateAndCheck(memoryTypes) is { } result) return result;
        candidates.Clear();
        return CreateAndCheck(deferredTypes);

        void CheckCandidate(LlvmType type)
        {
            if (type is not LlvmFixedVectorType vector) return;
            if (candidates.Count != 0 && layout.GetTypeSizeInBits(vector).GetFixedValue() != layout.GetTypeSizeInBits(candidates[0]).GetFixedValue())
            { candidates.Clear(); CountType(metrics, "vector.reject.candidate_size_conflict"); return; }
            candidates.Add(vector);
            if (commonElement is null) commonElement = vector.ElementType;
            else if (!ReferenceEquals(commonElement, vector.ElementType)) haveCommonElement = false;
            if (!vector.ElementType.IsPointerTy()) return;
            havePointer = true;
            if (commonPointer is null) commonPointer = vector;
            else if (!ReferenceEquals(commonPointer, vector)) haveCommonPointer = false;
        }
        LlvmVectorType? CreateAndCheck(OrderedSet<LlvmType> otherTypes)
        {
            foreach (var type in otherTypes)
            {
                if (!LlvmVectorType.IsValidElementType(type)) continue;
                uint bits = (uint)layout.GetTypeSizeInBits(type).GetFixedValue();
                foreach (var vector in initialCandidates)
                {
                    uint vectorBits = (uint)layout.GetTypeSizeInBits(vector).GetFixedValue();
                    uint elementBits = (uint)layout.GetTypeSizeInBits(vector.ElementType).GetFixedValue();
                    if (bits != vectorBits && bits != elementBits && vectorBits % bits == 0)
                        CheckCandidate(LlvmFixedVectorType.Get(type, vectorBits / bits));
                }
            }
            return CheckVectorTypesForPromotion(partition, layout, candidates, haveCommonElement, commonElement,
                havePointer, haveCommonPointer, commonPointer, vscale, metrics);
        }
    }
}
