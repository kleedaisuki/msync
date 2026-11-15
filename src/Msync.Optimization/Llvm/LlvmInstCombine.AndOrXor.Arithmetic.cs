// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldXorToXor (4822–4875), canonicalizeAbs (5088–5121),
// canonicalizeConditionalNegationViaMathToSelect (1677–1691), foldMaskedAddXorPattern (5455–5475).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldXorToXor(LlvmBinaryOperator instruction)
        {
            Count("logic.arithmetic.xor_to_xor.queries");
            var left = instruction.Left; var right = instruction.Right;
            if (SubBinary(left, LlvmOpcode.And, out var a, out var b) && SubPair(right, LlvmOpcode.Or, a, b) ||
                SubBinary(right, LlvmOpcode.And, out a, out b) && SubPair(left, LlvmOpcode.Or, a, b))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, a, b), "arithmetic_and_xor_or");
            if (MatchArithmeticComplementedPair(left, right, LlvmOpcode.Or, out a, out b))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, a, b), "arithmetic_complemented_or_xor");
            if (MatchArithmeticComplementedPair(left, right, LlvmOpcode.And, out a, out b))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, a, b), "arithmetic_complemented_and_xor");
            if (!left.HasOneUse && !right.HasOneUse) return null;
            if (SubBinary(left, LlvmOpcode.Or, out a, out b) && SubNot(right, out var inverted) && SubPair(inverted, LlvmOpcode.And, a, b) ||
                SubBinary(left, LlvmOpcode.And, out a, out b) && SubNot(right, out inverted) && SubPair(inverted, LlvmOpcode.Or, a, b))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateXor(a, b), LlvmConstant.GetAllOnesValue(instruction.Type)), "arithmetic_xor_complemented_dual");
            return null;
        }
        private static bool MatchArithmeticComplementedPair(LlvmValue first, LlvmValue second, LlvmOpcode opcode, out LlvmValue a, out LlvmValue b)
        {
            a = b = null!;
            if (!SubBinary(first, opcode, out var left, out var right) || !SubBinary(second, opcode, out var secondLeft, out var secondRight)) return false;
            if (SubNot(right, out var candidate) &&
                (SubNot(secondLeft, out var original) && original == left && secondRight == candidate ||
                 SubNot(secondRight, out original) && original == left && secondLeft == candidate))
            { a = left; b = candidate; return true; }
            if (SubNot(left, out candidate) &&
                (SubNot(secondLeft, out var other) && other == right && secondRight == candidate ||
                 SubNot(secondRight, out other) && other == right && secondLeft == candidate))
            { a = right; b = candidate; return true; }
            return false;
        }
        private LlvmInstruction? CanonicalizeAbs(LlvmBinaryOperator instruction)
        {
            Count("logic.arithmetic.abs.queries");
            var sum = instruction.Left; var sign = instruction.Right;
            if (sum.HasNUses(2)) (sum, sign) = (sign, sum);
            if (!SubBinary(sign, LlvmOpcode.AShr, out var value, out var amount) || !sign.HasNUses(2) ||
                SubAPInt(amount) is not { } shift || shift.Value != new LlvmAPInt(instruction.Type.GetScalarSizeInBits(), instruction.Type.GetScalarSizeInBits() - 1) ||
                !sum.HasOneUse || !SubPair(sum, LlvmOpcode.Add, value, sign)) return null;
            var comparison = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, value, LlvmConstant.GetNullValue(value.Type));
            var negative = SubNUW(sum) ? LlvmConstant.GetNullValue(value.Type) : Builder.CreateNeg(value, nsw: SubNSW(sum));
            return LogicResult(new LlvmSelectInstruction(comparison, negative, value), "arithmetic_canonicalize_abs");
        }
        private LlvmInstruction? CanonicalizeConditionalNegationViaMathToSelect(LlvmBinaryOperator instruction)
        {
            Count("logic.arithmetic.conditional_negation.queries");
            if (!instruction.Left.HasOneUse && !instruction.Right.HasOneUse ||
                !SubCast(instruction.Right, LlvmOpcode.SExt, out var condition) || !condition.Type.GetScalarType().IsIntegerTy(1) ||
                !SubBinary(instruction.Left, LlvmOpcode.Add, out var left, out var right)) return null;
            LlvmValue value;
            if (SubCast(left, LlvmOpcode.SExt, out var first) && first == condition) value = right;
            else if (SubCast(right, LlvmOpcode.SExt, out var second) && second == condition) value = left;
            else return null;
            return LogicResult(CreateSelectInstWithUnknownProfile(condition, Builder.CreateNeg(value, value.Name + ".neg"), value), "arithmetic_conditional_negation");
        }
        private LlvmInstruction? FoldMaskedAddXorPattern(LlvmBinaryOperator instruction)
        {
            Count("logic.arithmetic.masked_add_xor.queries");
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } mask || mask.Right != instruction.Right ||
                mask.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstant constant } add || !IsAddSubImmediateConstant(constant)) return null;
            var inverted = Builder.CreateNot(constant);
            var difference = Builder.CreateSub(inverted, add.Left, nuw: add.HasNoUnsignedWrap, nsw: add.HasNoSignedWrap);
            return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, difference, mask.Right), "arithmetic_masked_add_xor");
        }
    }
}
