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
                return FoldFloatingFormatCast(value, destination, metrics);
            case LlvmOpcode.FPToSI: case LlvmOpcode.FPToUI:
                if (value is not LlvmConstantFP fp || destination is not LlvmIntegerType integer) return null;
                LlvmFloatIntegerResult converted = fp.Value.ConvertToInteger(integer.BitWidth, opcode == LlvmOpcode.FPToSI);
                return converted.Status == LlvmFloatStatus.InvalidOp ? LlvmPoisonValue.Get(destination) :
                    LlvmConstantInt.Get(destination.Context, converted.Value);
            case LlvmOpcode.UIToFP: case LlvmOpcode.SIToFP:
                return FoldIntegerToFloatingFormat(value, destination, opcode == LlvmOpcode.SIToFP, metrics);
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
    private static LlvmConstant? FoldBitCast(LlvmConstant value, LlvmType destination) =>
        FoldScalarBitCast(value, destination);
}
