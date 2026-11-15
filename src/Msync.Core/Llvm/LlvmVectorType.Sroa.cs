// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/DerivedTypes.h getWithSizeAndScalar.
namespace Msync.Core.Llvm;

public abstract partial class LlvmVectorType
{
    public static LlvmVectorType? GetWithSizeAndScalar(LlvmVectorType sizeType, LlvmType element)
    {
        if (ReferenceEquals(sizeType.GetScalarType(), element.GetScalarType())) return sizeType;
        uint bits = element.GetScalarSizeInBits();
        if (!sizeType.GetPrimitiveSizeInBits().IsKnownMultipleOf(bits)) return null;
        var count = (sizeType.ElementCount * sizeType.GetScalarSizeInBits()).DivideCoefficientBy(bits);
        return Get(element.GetScalarType(), count);
    }
}
