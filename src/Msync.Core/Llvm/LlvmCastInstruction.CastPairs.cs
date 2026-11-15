// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Instructions.cpp, CastInst::isEliminableCastPair.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed partial class LlvmCastInstruction
{
    private static readonly byte[,] CastResults = {
        {1,0,0,99,99,0,0,99,99,99,99,0,3,0},
        {8,1,9,99,99,2,17,99,99,99,99,2,3,0},
        {8,0,1,99,99,0,2,99,99,99,99,0,3,0},
        {0,0,0,99,99,0,0,99,99,99,99,0,3,0},
        {0,0,0,99,99,0,0,99,99,99,99,0,3,0},
        {99,99,99,0,0,99,99,0,0,99,99,99,4,0},
        {99,99,99,0,0,99,99,0,0,99,99,99,4,0},
        {99,99,99,0,0,99,99,0,0,99,99,99,4,0},
        {99,99,99,2,2,99,99,8,2,99,99,99,4,0},
        {1,0,0,99,99,0,0,99,99,99,99,7,3,0},
        {0,0,0,99,99,0,0,99,99,99,99,0,3,0},
        {99,99,99,99,99,99,99,99,99,11,11,99,15,0},
        {5,5,5,0,0,5,5,0,0,16,16,5,18,14},
        {0,0,0,0,0,0,0,0,0,0,0,0,13,12}
    };

    public static LlvmOpcode? IsEliminableCastPair(LlvmOpcode first, LlvmOpcode second,
        LlvmType source, LlvmType middle, LlvmType destination, LlvmDataLayout? layout = null)
    {
        bool bothBitcasts = first == LlvmOpcode.BitCast && second == LlvmOpcode.BitCast;
        if (((first == LlvmOpcode.BitCast && source.IsVectorTy() != middle.IsVectorTy()) ||
             (second == LlvmOpcode.BitCast && middle.IsVectorTy() != destination.IsVectorTy())) && !bothBitcasts)
            return null;
        int action = CastResults[(int)first - (int)LlvmOpcode.Trunc, (int)second - (int)LlvmOpcode.Trunc];
        uint sourceSize, destinationSize;
        switch (action)
        {
            case 0: return null;
            case 1: return first;
            case 2: return second;
            case 3: return !source.IsVectorTy() && destination.IsIntegerTy() ? first : null;
            case 4: return destination == middle ? first : null;
            case 5: return source.IsIntegerTy() ? second : null;
            case 7:
                return source.Context.IntToPtrPtrToIntOptimization && source == destination && layout is not null &&
                    middle.GetScalarSizeInBits() >= layout.GetPointerTypeSizeInBits(source) ? LlvmOpcode.BitCast : null;
            case 8:
                sourceSize = source.GetScalarSizeInBits();
                destinationSize = destination.GetScalarSizeInBits();
                return source == destination ? LlvmOpcode.BitCast : sourceSize < destinationSize ? first :
                    sourceSize > destinationSize ? second : null;
            case 9: return LlvmOpcode.ZExt;
            case 11:
                if (layout is null) return null;
                uint middleSize = second == LlvmOpcode.PtrToAddr
                    ? layout.GetAddressSizeInBits(middle.GetScalarType().GetPointerAddressSpace())
                    : layout.GetPointerTypeSizeInBits(middle);
                sourceSize = source.GetScalarSizeInBits();
                destinationSize = destination.GetScalarSizeInBits();
                if (middleSize < sourceSize && middleSize < destinationSize) return null;
                return destinationSize < sourceSize ? LlvmOpcode.Trunc : destinationSize > sourceSize
                    ? LlvmOpcode.ZExt : LlvmOpcode.BitCast;
            case 12:
                return source.GetScalarType().GetPointerAddressSpace() != destination.GetScalarType().GetPointerAddressSpace()
                    ? LlvmOpcode.AddrSpaceCast : LlvmOpcode.BitCast;
            case 13: return destination.IsPtrOrPtrVectorTy() ? first : null;
            case 14: return source.IsPtrOrPtrVectorTy() ? LlvmOpcode.AddrSpaceCast : null;
            case 15: return destination.IsPtrOrPtrVectorTy() ? first : null;
            case 16:
                if (source.IsPtrOrPtrVectorTy()) return second;
                return second == LlvmOpcode.PtrToAddr && source.GetPrimitiveSizeInBits() == destination.GetPrimitiveSizeInBits()
                    ? LlvmOpcode.BitCast : null;
            case 17: return LlvmOpcode.UIToFP;
            case 18:
                if (!source.IsPtrOrPtrVectorTy() && !destination.IsPtrOrPtrVectorTy()) return LlvmOpcode.BitCast;
                if (source.IsPtrOrPtrVectorTy() && destination.IsPtrOrPtrVectorTy() &&
                    source.GetScalarType().GetPointerAddressSpace() == destination.GetScalarType().GetPointerAddressSpace())
                    return LlvmOpcode.BitCast;
                if (source.IsByteOrByteVectorTy() || destination.IsByteOrByteVectorTy()) return LlvmOpcode.BitCast;
                if (source.IsVectorTy() != destination.IsVectorTy() || layout is null) return null;
                if (source.IsPtrOrPtrVectorTy() && destination.IsIntOrIntVectorTy() &&
                    layout.GetPointerTypeSizeInBits(source) == destination.GetScalarSizeInBits()) return LlvmOpcode.PtrToInt;
                if (source.IsIntOrIntVectorTy() && destination.IsPtrOrPtrVectorTy() &&
                    source.GetScalarSizeInBits() == layout.GetPointerTypeSizeInBits(destination)) return LlvmOpcode.IntToPtr;
                return null;
            default:
                throw new UnreachableException("Invalid cast combination.");
        }
    }
}
