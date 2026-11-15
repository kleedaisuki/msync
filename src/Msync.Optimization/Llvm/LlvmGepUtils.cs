// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// llvm/lib/Analysis/Local.cpp emitGEPOffset.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static class LlvmGepUtils
{
    public static LlvmValue EmitGEPOffset(LlvmTransformBuilder builder, LlvmDataLayout layout,
        LlvmGepOperator gep, bool noAssumptions = false, Dictionary<string, ulong>? metrics = null)
    {
        var scalarIndex = layout.GetIndexType(gep.Context, ((LlvmPointerType)gep.Type.GetScalarType()).AddressSpace);
        LlvmType indexType = gep.Type is LlvmVectorType vector ? LlvmVectorType.Get(scalarIndex, vector.ElementCount) : scalarIndex;
        LlvmValue? result = null;
        bool nsw = gep.HasNoUnsignedSignedWrap && !noAssumptions;
        bool nuw = gep.HasNoUnsignedWrap && !noAssumptions;
        LlvmType current = gep.SourceElementType;
        for (int index = 0; index < gep.IndexCount; ++index)
        {
            Count(metrics, "indices");
            LlvmValue operand = gep.GetIndex(index);
            var structure = index == 0 ? null : current as LlvmStructType;
            bool vectorElement = index != 0 && current is LlvmVectorType;
            LlvmType element = index == 0 ? current : LlvmGetElementPtrInstruction.GetTypeAtIndex(current, operand)!;
            current = element;
            if (operand is LlvmConstant constant)
            {
                if (constant.IsNullValue) continue;
                if (structure is not null)
                {
                    ulong field = ((LlvmConstantInt)constant).GetZExtValue();
                    ulong size = layout.GetStructLayout(structure).GetElementOffset((uint)field).GetFixedValue();
                    if (size == 0) continue;
                    LlvmConstant offset = LlvmConstantInt.Get(scalarIndex, size);
                    if (indexType is LlvmVectorType vectorIndex) offset = LlvmConstantVector.GetSplat(vectorIndex.ElementCount, offset);
                    AddOffset(offset);
                    Count(metrics, "struct_offsets");
                    continue;
                }
            }
            if (indexType is LlvmVectorType indexVector && !operand.Type.IsVectorTy())
                operand = builder.CreateVectorSplat(indexVector.ElementCount, operand);
            if (!ReferenceEquals(operand.Type, indexType))
                operand = builder.CreateIntCast(operand, indexType, true, operand.Name + ".c");
            LlvmTypeSize stride = vectorElement ? layout.GetTypeStoreSize(element) : layout.GetTypeAllocSize(element);
            if (stride.IsScalable || stride.GetFixedValue() != 1)
            {
                LlvmValue scale = builder.CreateTypeSize(indexType.GetScalarType(), stride);
                if (indexType is LlvmVectorType scaleVector) scale = builder.CreateVectorSplat(scaleVector.ElementCount, scale);
                operand = builder.CreateMul(operand, scale, gep.Name + ".idx", nuw, nsw);
                Count(metrics, "scaled_offsets");
            }
            AddOffset(operand);
        }
        return result ?? LlvmConstant.GetNullValue(indexType);

        void AddOffset(LlvmValue offset)
        {
            result = result is null ? offset : builder.CreateAdd(result, offset, gep.Name + ".offs", nuw, nsw);
        }
    }

    private static void Count(Dictionary<string, ulong>? metrics, string key)
    {
        if (metrics is null) return;
        key = "gep_offset." + key;
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }
}
