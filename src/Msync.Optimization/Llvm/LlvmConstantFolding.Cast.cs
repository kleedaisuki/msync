// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ConstantFolding.cpp
// ConstantFoldCastOperand; IR/ConstantFold.cpp ConstantFoldCastInstruction,
// FoldBitCast (represented scalar integer/binary64/pointer cases).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldCastOperand(LlvmOpcode opcode, LlvmConstant value, LlvmType destination,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.cast.queries");
        LlvmConstant? result = FoldSymbolicCast(opcode, value, destination, layout, metrics);
        result ??= FoldCast(opcode, value, destination, layout, metrics);
        if (result is null && LlvmConstantExpr.IsDesirableCastOp(opcode))
        { Count(metrics, "constant_fold.cast.expression_created"); result = LlvmConstantExpr.GetCast(opcode, value, destination); }
        RecordResult(metrics, "constant_fold.cast", result);
        return result;
    }
    private static LlvmConstant? FoldCast(LlvmOpcode opcode, LlvmConstant value, LlvmType destination,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        if (value is LlvmPoisonValue) return LlvmPoisonValue.Get(destination);
        if (value is LlvmUndefValue)
            return opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt or LlvmOpcode.UIToFP or LlvmOpcode.SIToFP ?
                Null(destination) : LlvmUndefValue.Get(destination);
        if (value.IsNullValue && opcode != LlvmOpcode.AddrSpaceCast) return Null(destination);
        if (value.Type.IsVectorTy() || destination.IsVectorTy())
        { Count(metrics, "constant_fold.unported.vector"); return null; }
        switch (opcode)
        {
            case LlvmOpcode.FPTrunc: case LlvmOpcode.FPExt:
                // General format conversion is not represented by ConstantFP;
                // binary64 to binary64 preserves the source convert identity.
                return value is LlvmConstantFP && destination.IsDoubleTy() ? value : null;
            case LlvmOpcode.FPToSI: case LlvmOpcode.FPToUI:
                if (value is not LlvmConstantFP fp || destination is not LlvmIntegerType integer) return null;
                LlvmFloatIntegerResult converted = fp.Value.ConvertToInteger(integer.BitWidth, opcode == LlvmOpcode.FPToSI);
                return converted.Status == LlvmFloatStatus.InvalidOp ? LlvmPoisonValue.Get(destination) :
                    LlvmConstantInt.Get(destination.Context, converted.Value);
            case LlvmOpcode.UIToFP: case LlvmOpcode.SIToFP:
                return value is LlvmConstantInt sourceInt && destination.IsDoubleTy() ?
                    LlvmConstantFP.Get(destination, LlvmAPFloat.FromAPInt(sourceInt.Value, opcode == LlvmOpcode.SIToFP).Value) : null;
            case LlvmOpcode.Trunc: case LlvmOpcode.ZExt: case LlvmOpcode.SExt:
                if (value is not LlvmConstantInt ci || destination is not LlvmIntegerType targetInt) return null;
                LlvmAPInt result = opcode switch
                {
                    LlvmOpcode.Trunc => ci.Value.Trunc(targetInt.BitWidth),
                    LlvmOpcode.ZExt => ci.Value.ZExt(targetInt.BitWidth),
                    _ => ci.Value.SExt(targetInt.BitWidth)
                };
                return LlvmConstantInt.Get(destination.Context, result);
            case LlvmOpcode.BitCast: return FoldBitCast(value, destination);
            case LlvmOpcode.AddrSpaceCast: case LlvmOpcode.IntToPtr: case LlvmOpcode.PtrToAddr: case LlvmOpcode.PtrToInt:
                return null;
            default: throw new UnreachableException("Failed to cast constant expression");
        }
    }
    private static LlvmConstant? FoldBitCast(LlvmConstant value, LlvmType destination)
    {
        if (ReferenceEquals(value.Type, destination)) return value;
        if (value is LlvmConstantInt { Value.IsAllOnes: true } ||
            value is LlvmConstantFP floatingOnes && floatingOnes.Value.BitcastToAPInt().IsAllOnes)
            return !destination.IsPointerTy() ? AllOnes(destination) : null;
        if (value is LlvmConstantInt integerBytes && destination is LlvmByteType byteTarget && integerBytes.BitWidth == byteTarget.BitWidth)
            return LlvmConstantByte.Get(destination.Context, integerBytes.Value);
        if (value is LlvmConstantByte bytes)
        {
            if (destination is LlvmIntegerType integerTarget && bytes.Value.BitWidth == integerTarget.BitWidth)
                return LlvmConstantInt.Get(destination.Context, bytes.Value);
            if (destination.IsDoubleTy() && bytes.Value.BitWidth == 64)
                return LlvmConstantFP.Get(destination, new LlvmAPFloat(LlvmAPFloatSemantics.IEEEdouble, bytes.Value));
        }
        if (value is LlvmConstantFP floatingBytes && destination is LlvmByteType { BitWidth: 64 })
            return LlvmConstantByte.Get(destination.Context, floatingBytes.Value.BitcastToAPInt());
        if (value is LlvmConstantInt integer && destination.IsDoubleTy() && integer.BitWidth == 64)
            return LlvmConstantFP.Get(destination, new LlvmAPFloat(LlvmAPFloatSemantics.IEEEdouble, integer.Value));
        if (value is LlvmConstantFP floating && destination is LlvmIntegerType { BitWidth: 64 })
            return LlvmConstantInt.Get(destination.Context, floating.Value.BitcastToAPInt());
        return null;
    }
}
