// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2980–3075.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? SubAbsoluteAndMasks(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            uint width = instruction.Type.GetScalarSizeInBits();
            if (SubBinary(right, LlvmOpcode.AShr, out var value, out var amount) && right.HasNUses(2) &&
                SubAPInt(amount) is { } shift && shift.Value == new LlvmAPInt(width, width - 1) && left.HasOneUse && SubPair(left, LlvmOpcode.Xor, value, right))
            {
                var negative = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, value, LlvmConstant.GetNullValue(value.Type));
                var negation = instruction.HasNoUnsignedWrap ? LlvmConstant.GetNullValue(value.Type) : Builder.CreateNeg(value, nsw: instruction.HasNoSignedWrap);
                return SubRewrite(new LlvmSelectInstruction(negative, negation, value), "absolute_value");
            }
            if (SubBinary(left, LlvmOpcode.Add, out var x, out var addend) && SubAPInt(addend) is { } addConstant &&
                SubBinary(right, LlvmOpcode.And, out var masked, out var mask) && masked == x && SubAPInt(mask) is { } andConstant)
            {
                uint zeros = addConstant.Value.CountTrailingZeros();
                var high = LlvmAPInt.GetHighBitsSet(width, width - zeros);
                if ((high & andConstant.Value).IsZero)
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, left, SubConstant(instruction.Type, ~andConstant.Value)), "clear_low_mask");
            }
            return null;
        }
        private LlvmInstruction? SubSaturatingAndSquares(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            var type = instruction.Type;
            uint width = type.GetScalarSizeInBits();
            if (right.HasOneUse && SubIntrinsic(right, LlvmIntrinsicKind.USubSat, out var saturation) && saturation.GetArgument(0) == left)
                return SubReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.UMin, [left, saturation.GetArgument(1)]), "subtract_usub_sat");
            if (left.HasOneUse && SubMinMaxWith(left, LlvmIntrinsicKind.UMax, right, out var other))
                return SubReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.USubSat, [other, right]), "umax_minus_value");
            if (right.HasOneUse && SubMinMaxWith(right, LlvmIntrinsicKind.UMin, left, out other))
                return SubReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.USubSat, [left, other]), "value_minus_umin");
            if (right.HasOneUse && SubMinMaxWith(right, LlvmIntrinsicKind.UMax, left, out other))
                return SubRewrite(SubMakeNeg(Builder.CreateIntrinsic(LlvmIntrinsicKind.USubSat, [other, left])), "value_minus_umax");
            if (left.HasOneUse && SubMinMaxWith(left, LlvmIntrinsicKind.UMin, right, out other))
                return SubRewrite(SubMakeNeg(Builder.CreateIntrinsic(LlvmIntrinsicKind.USubSat, [right, other])), "umin_minus_value");
            if (SubAPInt(left, true) is { } count && count.Value == new LlvmAPInt(width, width) && right.HasOneUse && SubIntrinsic(right, LlvmIntrinsicKind.CTPop, out var population))
                return SubReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [Builder.CreateNot(population.GetArgument(0))]), "width_minus_ctpop");
            if (left.HasOneUse && right.HasOneUse && SubBinary(left, LlvmOpcode.Mul, out var x, out var x2) && x == x2 &&
                SubBinary(right, LlvmOpcode.Mul, out var y, out var y2) && y == y2)
            {
                bool signed = instruction.HasNoSignedWrap && SubNSW(left) && SubNSW(right) && width > 2;
                bool unsigned = instruction.HasNoUnsignedWrap && SubNUW(left) && SubNUW(right) && width > 1;
                var sum = Builder.CreateAdd(x, y, "add", unsigned, signed);
                var difference = Builder.CreateSub(x, y, "sub", unsigned, signed);
                return SubReplace(instruction, Builder.CreateMul(sum, difference, nuw: unsigned, nsw: signed), "difference_squares");
            }
            if (left.HasOneUse && right.HasOneUse && (instruction.HasNoUnsignedWrap || instruction.HasNoSignedWrap) &&
                SubIntrinsic(left, LlvmIntrinsicKind.SMax, out var maximum) && SubMinMaxWith(right, LlvmIntrinsicKind.SMin, maximum.GetArgument(0), out other) && other == maximum.GetArgument(1))
            {
                var difference = Builder.CreateSub(maximum.GetArgument(0), maximum.GetArgument(1), "sub", nsw: true);
                return SubReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.Abs, [difference, LlvmConstantInt.GetBool(instruction.Context, true)]), "signed_range_abs");
            }
            return null;
        }
    }
}
