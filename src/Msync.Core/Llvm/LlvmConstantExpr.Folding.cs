// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantFold.cpp
// scalar ConstantFoldBinaryInstruction / ConstantFoldCastInstruction.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantExpr
{
    private static LlvmConstant? FoldBinary(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right)
    {
        if (opcode is LlvmOpcode.Add or LlvmOpcode.Xor)
        {
            if (left.IsNullValue) return right;
            if (right.IsNullValue) return left;
        }
        else if (right.IsNullValue) return left;
        if (left is LlvmPoisonValue || right is LlvmPoisonValue) return LlvmPoisonValue.Get(left.Type);
        if (left.Type is not LlvmFixedVectorType && (left is LlvmUndefValue || right is LlvmUndefValue))
            return opcode == LlvmOpcode.Xor && left is LlvmUndefValue && right is LlvmUndefValue
                ? GetNullValue(left.Type) : LlvmUndefValue.Get(left.Type);
        if (left.Type is LlvmVectorType vector)
        {
            if (left.GetSplatValue() is { } aSplat && right.GetSplatValue() is { } bSplat)
                return LlvmConstantVector.GetSplat(vector.ElementCount, Get(opcode, aSplat, bSplat));
            if (vector is not LlvmFixedVectorType fixedVector) return null;
            var elements = new LlvmConstant[fixedVector.NumElements];
            for (uint lane = 0; lane < fixedVector.NumElements; ++lane)
            {
                var index = LlvmConstantInt.Get(LlvmType.GetInt32Ty(left.Context), lane);
                elements[lane] = Get(opcode, GetExtractElement(left, index), GetExtractElement(right, index));
            }
            return LlvmConstantVector.Get(elements);
        }
        if (left is LlvmConstantInt a && right is LlvmConstantInt b)
            return LlvmConstantInt.Get(left.Context, opcode switch
            { LlvmOpcode.Add => a.Value + b.Value, LlvmOpcode.Sub => a.Value - b.Value, _ => a.Value ^ b.Value });
        if (left is LlvmConstantExpr expression && (opcode is LlvmOpcode.Add or LlvmOpcode.Xor) && expression.Opcode == opcode)
        {
            var folded = Get(opcode, expression.GetConstantOperand(1), right);
            if (folded is not LlvmConstantExpr same || same.Opcode != opcode)
                return Get(opcode, expression.GetConstantOperand(0), folded);
        }
        else if (left is not LlvmConstantExpr && right is LlvmConstantExpr && (opcode is LlvmOpcode.Add or LlvmOpcode.Xor))
            return FoldBinary(opcode, right, left);
        if (left.Type.IsIntegerTy(1) && (opcode is LlvmOpcode.Add or LlvmOpcode.Sub)) return GetXor(left, right);
        return null;
    }

    public static LlvmConstant GetCast(LlvmOpcode opcode, LlvmConstant value, LlvmType destination) =>
        GetCast(opcode, value, destination, false)!;
    public static LlvmConstant? GetCast(LlvmOpcode opcode, LlvmConstant value, LlvmType destination, bool onlyIfReduced)
    {
        Debug.Assert(IsSupportedCastOp(opcode));
        if (opcode == LlvmOpcode.BitCast && value.Type == destination) return value;
        if (FoldCast(opcode, value, destination) is { } folded)
        { Count(value.Context, "constant_expr.cast.folded"); return folded; }
        return onlyIfReduced ? null : Intern(destination, opcode, [value]);
    }
    public static LlvmConstant GetTrunc(LlvmConstant value, LlvmType destination) => GetCast(LlvmOpcode.Trunc, value, destination);
    public static LlvmConstant GetPtrToAddr(LlvmConstant value, LlvmType destination) => GetCast(LlvmOpcode.PtrToAddr, value, destination);
    public static LlvmConstant GetPtrToInt(LlvmConstant value, LlvmType destination) => GetCast(LlvmOpcode.PtrToInt, value, destination);
    public static LlvmConstant GetIntToPtr(LlvmConstant value, LlvmType destination) => GetCast(LlvmOpcode.IntToPtr, value, destination);
    public static LlvmConstant GetBitCast(LlvmConstant value, LlvmType destination) => GetCast(LlvmOpcode.BitCast, value, destination);
    public static LlvmConstant GetAddrSpaceCast(LlvmConstant value, LlvmType destination) => GetCast(LlvmOpcode.AddrSpaceCast, value, destination);
    public static LlvmConstant GetTruncOrBitCast(LlvmConstant value, LlvmType destination) =>
        GetCast(value.Type.GetScalarSizeInBits() == destination.GetScalarSizeInBits() ? LlvmOpcode.BitCast : LlvmOpcode.Trunc, value, destination);
    public static LlvmConstant GetPointerCast(LlvmConstant value, LlvmType destination) =>
        GetCast(destination.IsIntOrIntVectorTy() ? LlvmOpcode.PtrToInt :
            ((LlvmPointerType)value.Type.GetScalarType()).AddressSpace != ((LlvmPointerType)destination.GetScalarType()).AddressSpace
                ? LlvmOpcode.AddrSpaceCast : LlvmOpcode.BitCast, value, destination);
    public static LlvmConstant GetPointerBitCastOrAddrSpaceCast(LlvmConstant value, LlvmType destination) =>
        GetCast(((LlvmPointerType)value.Type.GetScalarType()).AddressSpace != ((LlvmPointerType)destination.GetScalarType()).AddressSpace
            ? LlvmOpcode.AddrSpaceCast : LlvmOpcode.BitCast, value, destination);

    private static LlvmConstant? FoldCast(LlvmOpcode opcode, LlvmConstant value, LlvmType destination)
    {
        if (value is LlvmPoisonValue) return LlvmPoisonValue.Get(destination);
        if (value is LlvmUndefValue) return LlvmUndefValue.Get(destination);
        if (value.IsNullValue && opcode != LlvmOpcode.AddrSpaceCast) return GetNullValue(destination);
        if (value is LlvmConstantExpr { IsCast: true } expression &&
            LlvmCastInstruction.IsEliminableCastPair(expression.Opcode, opcode, expression.GetConstantOperand(0).Type,
                value.Type, destination) is { } combined)
            return FoldMaybeUndesirableCast(combined, expression.GetConstantOperand(0), destination);
        if (value.Type is LlvmVectorType sourceVector && destination is LlvmVectorType targetVector &&
            sourceVector.ElementCount == targetVector.ElementCount)
        {
            if (value.GetSplatValue() is { } splat)
            {
                var folded = FoldMaybeUndesirableCast(opcode, splat, targetVector.ElementType);
                return folded is null ? null : LlvmConstantVector.GetSplat(targetVector.ElementCount, folded);
            }
            if (targetVector is not LlvmFixedVectorType fixedVector) return null;
            var elements = new LlvmConstant[fixedVector.NumElements];
            for (uint lane = 0; lane < fixedVector.NumElements; ++lane)
            {
                var element = GetExtractElement(value, LlvmConstantInt.Get(LlvmType.GetInt32Ty(value.Context), lane));
                if (FoldMaybeUndesirableCast(opcode, element, targetVector.ElementType) is not { } folded) return null;
                elements[lane] = folded;
            }
            return LlvmConstantVector.Get(elements);
        }
        if (opcode == LlvmOpcode.Trunc && value is LlvmConstantInt integer)
            return LlvmConstantInt.Get(value.Context, integer.Value.Trunc(destination.GetIntegerBitWidth()));
        if (opcode != LlvmOpcode.BitCast) return null;
        if (value.Type == destination) return value;
        if (value is LlvmConstantInt bits && destination is LlvmByteType bytes && bits.BitWidth == bytes.BitWidth)
            return LlvmConstantByte.Get(value.Context, bits.Value);
        if (value is LlvmConstantByte data)
        {
            if (destination is LlvmIntegerType integerType && data.Value.BitWidth == integerType.BitWidth)
                return LlvmConstantInt.Get(value.Context, data.Value);
            if (destination.IsFloatingPointTy() && data.Value.BitWidth == destination.GetScalarSizeInBits())
                return LlvmConstantFP.Get(destination, new LlvmAPFloat(destination.GetFltSemantics(), data.Value));
        }
        if (value is LlvmConstantInt fpBits && destination.IsFloatingPointTy() && fpBits.BitWidth == destination.GetScalarSizeInBits())
            return LlvmConstantFP.Get(destination, new LlvmAPFloat(destination.GetFltSemantics(), fpBits.Value));
        if (value is LlvmConstantFP floating && destination is LlvmIntegerType integerTarget &&
            floating.Value.BitcastToAPInt().BitWidth == integerTarget.BitWidth)
            return LlvmConstantInt.Get(value.Context, floating.Value.BitcastToAPInt());
        if (value is LlvmConstantFP byteFloating && destination is LlvmByteType byteTarget &&
            byteFloating.Value.BitcastToAPInt().BitWidth == byteTarget.BitWidth)
            return LlvmConstantByte.Get(value.Context, byteFloating.Value.BitcastToAPInt());
        return null;
    }

    private static LlvmConstant? FoldMaybeUndesirableCast(LlvmOpcode opcode, LlvmConstant value, LlvmType type)
    {
        if (IsSupportedCastOp(opcode)) return GetCast(opcode, value, type);
        if (opcode is LlvmOpcode.SExt or LlvmOpcode.ZExt)
        {
            if (value is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
            if (value is LlvmUndefValue) return GetNullValue(type);
            if (value is LlvmConstantInt integer) return LlvmConstantInt.Get(type.Context,
                opcode == LlvmOpcode.SExt ? integer.Value.SExt(type.GetIntegerBitWidth()) : integer.Value.ZExt(type.GetIntegerBitWidth()));
        }
        return null;
    }

    private static LlvmConstant GetAllOnes(LlvmType type) => type switch
    {
        LlvmVectorType vector => LlvmConstantVector.GetSplat(vector.ElementCount, GetAllOnes(vector.ElementType)),
        LlvmIntegerType integer => LlvmConstantInt.Get(type.Context, LlvmAPInt.GetAllOnes(integer.BitWidth)),
        LlvmByteType bytes => LlvmConstantByte.Get(type.Context, LlvmAPInt.GetAllOnes(bytes.BitWidth)),
        _ when type.IsFloatingPointTy() => LlvmConstantFP.Get(type,
            new LlvmAPFloat(type.GetFltSemantics(), LlvmAPInt.GetAllOnes(type.GetScalarSizeInBits()))),
        _ => throw new NotSupportedException("All-ones constant for the represented scalar type is unavailable.")
    };
}
