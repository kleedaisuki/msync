// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFold.cpp
// ConstantFoldCastInstruction FPTrunc/FPExt/SIToFP/UIToFP and scalar FoldBitCast.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    private static LlvmConstant? FoldFloatingFormatCast(LlvmConstant value, LlvmType destination, Dictionary<string, ulong>? metrics)
    {
        if (value is not LlvmConstantFP floating) return null;
        LlvmAPFloatConvertResult converted = floating.Value.Convert(destination.GetFltSemantics(), LlvmRoundingMode.NearestTiesToEven);
        Count(metrics, converted.LosesInfo ? "constant_fold.cast.fp_format_lossy" : "constant_fold.cast.fp_format_exact");
        return LlvmConstantFP.Get(destination, converted.Value);
    }

    private static LlvmConstant? FoldIntegerToFloatingFormat(LlvmConstant value, LlvmType destination,
        bool isSigned, Dictionary<string, ulong>? metrics)
    {
        if (value is not LlvmConstantInt integer) return null;
        LlvmAPFloatResult converted = LlvmAPFloat.FromAPInt(destination.GetFltSemantics(), integer.Value, isSigned, LlvmRoundingMode.NearestTiesToEven);
        Count(metrics, (converted.Status & LlvmFloatStatus.Inexact) == 0 ? "constant_fold.cast.int_to_fp_exact" : "constant_fold.cast.int_to_fp_inexact");
        return LlvmConstantFP.Get(destination, converted.Value);
    }

    private static LlvmConstant? FoldScalarBitCast(LlvmConstant value, LlvmType destination)
    {
        if (value.Type == destination) return value;
        if (IsAllOnes(value) && !destination.IsPointerTy()) return AllOnes(destination);
        if (value is LlvmConstantInt integer)
        {
            if (destination is LlvmByteType bytes && bytes.BitWidth == integer.BitWidth) return LlvmConstantByte.Get(destination.Context, integer.Value);
            if (destination.IsFloatingPointTy() && !destination.IsPPC_FP128Ty() && destination.GetPrimitiveSizeInBits().GetFixedValue() == integer.BitWidth)
                return LlvmConstantFP.Get(destination, new LlvmAPFloat(destination.GetFltSemantics(), integer.Value));
            return null;
        }
        if (value is LlvmConstantByte bytesValue)
        {
            if (destination is LlvmIntegerType integerType && integerType.BitWidth == bytesValue.Value.BitWidth) return LlvmConstantInt.Get(destination.Context, bytesValue.Value);
            if (destination.IsFloatingPointTy() && !destination.IsPPC_FP128Ty() && destination.GetPrimitiveSizeInBits().GetFixedValue() == bytesValue.Value.BitWidth)
                return LlvmConstantFP.Get(destination, new LlvmAPFloat(destination.GetFltSemantics(), bytesValue.Value));
            return null;
        }
        if (value is not LlvmConstantFP floating) return null;
        LlvmAPInt bits = floating.Value.BitcastToAPInt();
        if (destination.IsFloatingPointTy()) return LlvmConstantFP.Get(destination, new LlvmAPFloat(destination.GetFltSemantics(), bits));
        if (value.Type.IsPPC_FP128Ty()) return null;
        if (destination is LlvmIntegerType targetInteger && targetInteger.BitWidth == bits.BitWidth) return LlvmConstantInt.Get(destination.Context, bits);
        if (destination is LlvmByteType targetByte && targetByte.BitWidth == bits.BitWidth) return LlvmConstantByte.Get(destination.Context, bits);
        return null;
    }
}
