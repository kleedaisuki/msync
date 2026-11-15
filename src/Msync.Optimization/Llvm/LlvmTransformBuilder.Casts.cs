// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IRBuilder.h CreateBitOrPointerCast.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmValue CreateBitOrPointerCast(LlvmValue value, LlvmType destination, string name = "")
    {
        if (ReferenceEquals(value.Type, destination)) return value;
        if (value.Type.IsPtrOrPtrVectorTy() && destination.IsIntOrIntVectorTy())
            return CreatePtrToInt(value, destination, name);
        if (value.Type.IsIntOrIntVectorTy() && destination.IsPtrOrPtrVectorTy())
            return CreateIntToPtr(value, destination, name);
        return CreateBitCast(value, destination, name);
    }

    public LlvmValue CreateBitPreservingCastChain(LlvmDataLayout dataLayout, LlvmValue value, LlvmType destination)
    {
        LlvmType source = value.Type;
        if (ReferenceEquals(source, destination)) return value;
        Debug.Assert(!(source.IsIntegerTy() && destination.IsIntegerTy()));
        if ((source.IsIntOrIntVectorTy() || source.IsFPOrFPVectorTy()) && destination.IsPtrOrPtrVectorTy())
            return CreateIntToPtr(BitCastLike(value, PointerInteger(destination)), destination);
        if (source.IsPtrOrPtrVectorTy() && (destination.IsIntOrIntVectorTy() || destination.IsFPOrFPVectorTy()))
            return BitCastLike(CreatePtrToInt(value, PointerInteger(source)), destination);
        if (source.IsPtrOrPtrVectorTy() && destination.IsPtrOrPtrVectorTy() &&
            ((LlvmPointerType)source.GetScalarType()).AddressSpace != ((LlvmPointerType)destination.GetScalarType()).AddressSpace)
            return CreateIntToPtr(BitCastLike(CreatePtrToInt(value, PointerInteger(source)), PointerInteger(destination)), destination);
        return BitCastLike(value, destination);

        LlvmType PointerInteger(LlvmType pointer)
        {
            var integer = dataLayout.GetIntPtrType(pointer.Context, ((LlvmPointerType)pointer.GetScalarType()).AddressSpace);
            return pointer is LlvmVectorType vector ? LlvmVectorType.Get(integer, vector.ElementCount) : integer;
        }

        LlvmValue BitCastLike(LlvmValue operand, LlvmType type)
        {
            if (ReferenceEquals(operand.Type, type)) return operand;
            if (operand.Type is LlvmFixedVectorType && type is LlvmScalableVectorType scalable)
            {
                var shape = LlvmVectorType.GetWithSizeAndScalar(scalable, operand.Type)!;
                return CreateBitCast(CreateInsertVector(shape, LlvmPoisonValue.Get(shape), operand, 0UL), type);
            }
            if (operand.Type is LlvmScalableVectorType sourceVector && type is LlvmFixedVectorType)
            {
                var shape = LlvmVectorType.GetWithSizeAndScalar(sourceVector, type)!;
                return CreateExtractVector(type, CreateBitCast(operand, shape), 0UL);
            }
            return CreateBitCast(operand, type);
        }
    }
}
