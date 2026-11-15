// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/DataLayout.h typeSizeEqualsStoreSize, IR/DataLayout.cpp getIndexedOffsetInType.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmDataLayout
{
    public bool TypeSizeEqualsStoreSize(LlvmType type) => GetTypeSizeInBits(type) == GetTypeStoreSizeInBits(type);

    public long GetIndexedOffsetInType(LlvmType element, IReadOnlyList<LlvmValue> indices)
    {
        long result = 0;
        for (int index = 0; index < indices.Count; ++index)
        {
            var operand = (LlvmConstantInt)indices[index];
            if (index != 0 && element is LlvmStructType structure)
            {
                Debug.Assert(operand.Type.IsIntegerTy(32), "Illegal struct index");
                uint field = (uint)operand.GetZExtValue();
                result = unchecked(result + (long)GetStructLayout(structure).GetElementOffset(field).GetFixedValue());
                element = structure.Elements[(int)field];
                continue;
            }
            bool vectorElement = index != 0 && element is LlvmVectorType;
            if (index != 0)
                element = element switch
                {
                    LlvmArrayType array => array.ElementType,
                    LlvmVectorType vector => vector.ElementType,
                    _ => throw new InvalidOperationException("Sequential GEP index requires an array or vector type.")
                };
            long offset = operand.Value.GetSExtValue();
            if (offset == 0) continue;
            if (vectorElement) Debug.Assert(TypeSizeEqualsStoreSize(element), "Not byte-addressable");
            ulong stride = (vectorElement ? GetTypeStoreSize(element) : GetTypeAllocSize(element)).GetFixedValue();
            result = unchecked(result + offset * (long)stride);
        }
        return result;
    }

    public LlvmType GetIntPtrType(LlvmType type)
    {
        Debug.Assert(type.IsPtrOrPtrVectorTy());
        LlvmIntegerType integer = LlvmIntegerType.Get(type.Context, GetPointerTypeSizeInBits(type));
        return type is LlvmVectorType vector ? LlvmVectorType.Get(integer, vector) : integer;
    }
}
