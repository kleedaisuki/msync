// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2747–2844.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmBinaryOperator SubMakeNeg(LlvmValue value) => new(LlvmOpcode.Sub, LlvmConstant.GetNullValue(value.Type), value);
        private LlvmInstruction? SubBitwiseIdentities(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (SubCommuted(right, LlvmOpcode.Add, left, out var other)) return SubRewrite(SubMakeNeg(other), "value_minus_sum");
            if (SubBinary(left, LlvmOpcode.Sub, out var minuend, out var subtrahend) && minuend == right)
                return SubRewrite(SubMakeNeg(subtrahend), "subtract_minuend");
            if (SubBinary(right, LlvmOpcode.And, out var a, out var b) && SubPair(left, LlvmOpcode.Or, a, b))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Xor, a, b), "or_minus_and");
            if (SubBinary(left, LlvmOpcode.Add, out a, out b) && SubPair(right, LlvmOpcode.Or, a, b))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, a, b), "add_minus_or");
            if (SubBinary(left, LlvmOpcode.Add, out a, out b) && SubPair(right, LlvmOpcode.And, a, b))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Or, a, b), "add_minus_and");
            if (SubBinary(left, LlvmOpcode.And, out a, out b) && SubPair(right, LlvmOpcode.Or, a, b) && (left.HasOneUse || right.HasOneUse))
                return SubRewrite(SubMakeNeg(Builder.CreateXor(a, b)), "and_minus_or");
            if (SubBinary(right, LlvmOpcode.Xor, out a, out b) && SubPair(left, LlvmOpcode.Or, a, b))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, a, b), "or_minus_xor");
            if (SubBinary(left, LlvmOpcode.Xor, out a, out b) && SubPair(right, LlvmOpcode.Or, a, b) && (left.HasOneUse || right.HasOneUse))
                return SubRewrite(SubMakeNeg(Builder.CreateAnd(a, b)), "xor_minus_or");
            if (left.HasOneUse && SubCommuted(left, LlvmOpcode.Or, right, out other))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, other, Builder.CreateNot(right, right.Name + ".not")), "or_minus_operand");
            if (left.HasOneUse && SubCommuted(left, LlvmOpcode.And, right, out other) && other.HasOneUse && SubNeg(other, out var negated))
                return SubRewrite(SubMakeNeg(Builder.CreateAnd(right, Builder.CreateAdd(negated, LlvmConstant.GetAllOnesValue(instruction.Type)))), "masked_negation_minus_value");
            if (left.HasOneUse && SubBinary(left, LlvmOpcode.And, out var masked, out var mask) && masked == right && mask is LlvmConstant)
                return SubRewrite(SubMakeNeg(Builder.CreateAnd(right, Builder.CreateNot(mask))), "masked_value_minus_value");
            if (SubXorSignMask(left, right, out var condition, out var value))
                return SubRewrite(CreateSelectInstWithUnknownProfile(condition, Builder.CreateNeg(value), value), "xor_sext_bool_minus_sext");
            if (SubXorSignMask(right, left, out condition, out value))
                return SubRewrite(CreateSelectInstWithUnknownProfile(condition, value, Builder.CreateNeg(value)), "sext_bool_minus_xor");
            return null;
        }
        private static bool SubXorSignMask(LlvmValue left, LlvmValue right, out LlvmValue condition, out LlvmValue value)
        {
            condition = value = null!;
            return left.HasOneUse && SubCommuted(left, LlvmOpcode.Xor, right, out value) &&
                SubCast(right, LlvmOpcode.SExt, out condition) && condition.Type.GetScalarSizeInBits() == 1;
        }
    }
}
