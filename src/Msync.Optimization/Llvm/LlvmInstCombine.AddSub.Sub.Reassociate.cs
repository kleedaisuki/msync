// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2559–2628.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? SubFoldMaskedSubset(LlvmBinaryOperator instruction)
        {
            if (!SubBinary(instruction.Left, LlvmOpcode.And, out var first, out var firstMask) ||
                !SubBinary(instruction.Right, LlvmOpcode.And, out var second, out var secondMask) || first != second ||
                firstMask is not LlvmConstant a || secondMask is not LlvmConstant b || !IsAddSubImmediateConstant(a) || !IsAddSubImmediateConstant(b)) return null;
            var intersection = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.And, a, b, DL, Metrics);
            if (!SubElementWiseEqual(b, intersection)) return null;
            return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, first, LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Xor, a, b, DL, Metrics)!), "masked_subset");
        }
        private LlvmInstruction? SubReassociate(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (left.HasOneUse && SubBinary(left, LlvmOpcode.Add, out var first, out var second))
            {
                if (first.HasOneUse && SubBinary(first, LlvmOpcode.Sub, out var x, out var y))
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateAdd(x, second), Builder.CreateAdd(y, right)), "reassociate_add_sub");
                if (second.HasOneUse && SubBinary(second, LlvmOpcode.Sub, out x, out y))
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateAdd(x, first), Builder.CreateAdd(y, right)), "reassociate_add_sub");
            }
            if (left.HasOneUse && SubBinary(left, LlvmOpcode.Sub, out var minuend, out var subtrahend))
            {
                bool unsigned = instruction.HasNoUnsignedWrap && SubNUW(left);
                bool signed = unsigned && instruction.HasNoSignedWrap && SubNSW(left);
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, minuend, Builder.CreateAdd(subtrahend, right, nuw: unsigned, nsw: signed))
                    { HasNoUnsignedWrap = unsigned, HasNoSignedWrap = signed }, "reassociate_sub_sub");
            }
            if (left.HasOneUse && right.HasOneUse && SubBinary(left, LlvmOpcode.Add, out var lhs, out var lhsConstant) &&
                SubBinary(right, LlvmOpcode.Add, out var rhs, out var rhsConstant) && lhsConstant is LlvmConstant a && rhsConstant is LlvmConstant b &&
                IsAddSubImmediateConstant(a) && IsAddSubImmediateConstant(b))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateSub(lhs, rhs), LlvmConstantExpr.GetSub(a, b)), "difference_constant_sums");
            if (!SubAddLike(left, out var w, out var x0) || !SubAddLike(right, out var y0, out var z)) return null;
            LlvmValue resultLeft, resultRight;
            if (w == y0) { resultLeft = x0; resultRight = z; }
            else if (w == z) { resultLeft = x0; resultRight = y0; }
            else if (x0 == y0) { resultLeft = w; resultRight = z; }
            else if (x0 == z) { resultLeft = w; resultRight = y0; }
            else return null;
            return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, resultLeft, resultRight)
            { HasNoSignedWrap = instruction.HasNoSignedWrap && SubNSW(left) && SubNSW(right),
                HasNoUnsignedWrap = instruction.HasNoUnsignedWrap && SubNUW(right) }, "cancel_common_addend");
        }
        private static bool SubElementWiseEqual(LlvmConstant value, LlvmValue? other)
        {
            if (value == other) return true;
            if (other is not LlvmConstant constant || value.Type != constant.Type || value.Type is not LlvmVectorType vector ||
                !vector.ElementType.IsIntegerTy() && !vector.ElementType.IsFloatingPointTy()) return false;
            var integerType = LlvmVectorType.GetInteger(vector);
            var first = LlvmConstantExpr.GetBitCast(value, integerType);
            var second = LlvmConstantExpr.GetBitCast(constant, integerType);
            var equal = LlvmConstantFolding.ConstantFoldCompareInstruction(LlvmCmpPredicate.ICmpEq, first, second);
            return equal is LlvmPoisonValue || equal is not null && SubOne(equal);
        }
    }
}
