// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantFold.cpp
// ConstantFoldBinaryInstruction vector slice, shared scalar operand folders.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    private static LlvmConstant? FoldVectorBinary(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        var type = (LlvmVectorType)left.Type;
        var identity = BinOpIdentity(opcode, type, false);
        if (identity is not null)
        {
            if (ReferenceEquals(left, identity)) return right;
            if (ReferenceEquals(right, identity)) return left;
        }
        else if (ReferenceEquals(right, BinOpIdentity(opcode, type, true))) return left;
        if (left is LlvmPoisonValue || right is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
        if (type is LlvmScalableVectorType && (left is LlvmUndefValue || right is LlvmUndefValue))
            return FoldUndefBinary(opcode, left, right);
        if (right.GetSplatValue() is { } rightSplat)
        {
            if (rightSplat.IsNullValue && opcode is LlvmOpcode.SDiv or LlvmOpcode.UDiv or LlvmOpcode.SRem or LlvmOpcode.URem)
                return LlvmPoisonValue.Get(type);
            if (left.GetSplatValue() is { } leftSplat)
            {
                var result = ConstantFoldBinaryOpOperands(opcode, leftSplat, rightSplat, layout, metrics);
                return result is null ? null : LlvmConstantVector.GetSplat(type.ElementCount, result);
            }
        }
        if (type is not LlvmFixedVectorType fixedType) return null;
        var elements = new LlvmConstant[fixedType.NumElements];
        for (uint lane = 0; lane < fixedType.NumElements; ++lane)
        {
            Count(metrics, "constant_fold.binary.vector_lanes");
            var index = LlvmConstantInt.Get(LlvmType.GetInt32Ty(left.Context), lane);
            var first = LlvmConstantExpr.GetExtractElement(left, index);
            var second = LlvmConstantExpr.GetExtractElement(right, index);
            if (ConstantFoldBinaryOpOperands(opcode, first, second, layout, metrics) is not { } result) return null;
            elements[lane] = result;
        }
        return LlvmConstantVector.Get(elements);
    }
    private static LlvmConstant? FoldVectorCompare(LlvmCmpPredicate predicate, LlvmConstant left, LlvmConstant right)
    {
        var type = (LlvmVectorType)left.Type;
        var resultType = LlvmVectorType.Get(LlvmType.GetInt1Ty(left.Context), type.ElementCount);
        LlvmConstant Boolean(bool value) => LlvmConstantVector.GetSplat(type.ElementCount, LlvmConstantInt.GetBool(left.Context, value));
        if (predicate == LlvmCmpPredicate.FCmpFalse) return Boolean(false);
        if (predicate == LlvmCmpPredicate.FCmpTrue) return Boolean(true);
        if (left is LlvmPoisonValue || right is LlvmPoisonValue) return LlvmPoisonValue.Get(resultType);
        if (left is LlvmUndefValue || right is LlvmUndefValue)
        {
            bool integer = (byte)predicate >= 32;
            if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe || integer && ReferenceEquals(left, right))
                return LlvmUndefValue.Get(resultType);
            return Boolean(integer ? predicate is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUle or
                LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle : (byte)predicate >= 8);
        }
        if (right.IsNullValue && predicate == LlvmCmpPredicate.ICmpUge) return Boolean(true);
        if (right.IsNullValue && predicate == LlvmCmpPredicate.ICmpUlt) return Boolean(false);
        if (left.GetSplatValue() is { } firstSplat && right.GetSplatValue() is { } secondSplat)
        {
            var result = ConstantFoldCompareInstruction(predicate, firstSplat, secondSplat);
            return result is null ? null : LlvmConstantVector.GetSplat(type.ElementCount, result);
        }
        if (type is not LlvmFixedVectorType fixedType) return null;
        var elements = new LlvmConstant[fixedType.NumElements];
        for (uint lane = 0; lane < fixedType.NumElements; ++lane)
        {
            var index = LlvmConstantInt.Get(LlvmType.GetInt32Ty(left.Context), lane);
            var first = LlvmConstantExpr.GetExtractElement(left, index);
            var second = LlvmConstantExpr.GetExtractElement(right, index);
            if (ConstantFoldCompareInstruction(predicate, first, second) is not { } result) return null;
            elements[lane] = result;
        }
        return LlvmConstantVector.Get(elements);
    }
}
