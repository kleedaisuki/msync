// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ConstantFolding.cpp
// ConstantFoldCompareInstOperands symbolic pointer/constant-expression folds.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    private static LlvmConstant? FoldExpressionCompare(LlvmCmpPredicate predicate, LlvmConstant left,
        LlvmConstant right, LlvmDataLayout layout, LlvmFunction? function, Dictionary<string, ulong>? metrics)
    {
        if (left is not LlvmConstantExpr expression)
            return right is LlvmConstantExpr ? ConstantFoldCompareInstOperands(
                LlvmCmpInstruction.GetSwappedPredicate(predicate), right, left, layout, function, metrics) : null;
        Count(metrics, "constant_fold.compare.expression_queries");
        if (right.IsNullValue)
        {
            if (expression.Opcode == LlvmOpcode.IntToPtr)
            {
                var integer = ConstantFoldIntegerCast(expression.GetConstantOperand(0),
                    layout.GetIntPtrType(expression.Type), false, layout, metrics);
                if (integer is not null) return ConstantFoldCompareInstOperands(predicate, integer,
                    LlvmConstant.GetNullValue(integer.Type), layout, function, metrics);
            }
            if (expression.Opcode is LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr &&
                expression.Type == layout.GetAddressType(expression.GetConstantOperand(0).Type))
            {
                var pointer = expression.GetConstantOperand(0);
                return ConstantFoldCompareInstOperands(predicate, pointer, LlvmConstant.GetNullValue(pointer.Type), layout, function, metrics);
            }
        }
        if (right is LlvmConstantExpr other && expression.Opcode == other.Opcode)
        {
            if (expression.Opcode == LlvmOpcode.IntToPtr)
            {
                var integerType = layout.GetIntPtrType(expression.Type);
                var first = ConstantFoldIntegerCast(expression.GetConstantOperand(0), integerType, false, layout, metrics);
                var second = ConstantFoldIntegerCast(other.GetConstantOperand(0), integerType, false, layout, metrics);
                if (first is not null && second is not null)
                    return ConstantFoldCompareInstOperands(predicate, first, second, layout, function, metrics);
            }
            if (expression.Opcode is LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr &&
                expression.Type == layout.GetAddressType(expression.GetConstantOperand(0).Type) &&
                expression.GetConstantOperand(0).Type == other.GetConstantOperand(0).Type)
                return ConstantFoldCompareInstOperands(predicate, expression.GetConstantOperand(0), other.GetConstantOperand(0), layout, function, metrics);
        }
        bool signed = predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle;
        if (!left.Type.IsPointerTy() || signed) return null;
        bool equality = predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
        uint width = layout.GetIndexTypeSizeInBits(left.Type);
        LlvmAPInt firstOffset = new(width, 0), secondOffset = new(width, 0);
        var firstBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(left, layout, ref firstOffset,
            equality, metrics: metrics, lookThroughIntToPtr: equality);
        var secondBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(right, layout, ref secondOffset,
            equality, metrics: metrics, lookThroughIntToPtr: equality);
        if (!ReferenceEquals(firstBase, secondBase)) return null;
        LlvmCmpPredicate signedPredicate = predicate switch
        {
            LlvmCmpPredicate.ICmpUgt => LlvmCmpPredicate.ICmpSgt, LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpSge,
            LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpSlt, LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpSle,
            _ => predicate
        };
        Count(metrics, "constant_fold.compare.same_base_offsets");
        return LlvmConstantInt.GetBool(left.Context, CompareInteger(firstOffset, secondOffset, signedPredicate));
    }

}
