// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2847–2930.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? SubSelectAndMinMax(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (SubSinkSelect(instruction, left, right, true) is { } first) return first;
            if (SubSinkSelect(instruction, right, left, false) is { } second) return second;
            if (SubCommuted(right, LlvmOpcode.And, left, out var other) && (right.HasOneUse || other is LlvmConstant))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, left, Builder.CreateNot(other, other.Name + ".not")), "value_minus_masked_value");
            if (SubNot(left, out var x) && SubAnyMinMaxWith(right, left, out var y) && !left.HasNUsesOrMore(3) && IsFreeToInvert(y, y.HasOneUse))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateNot(right), x), "invert_minmax_right");
            if (SubNot(right, out x) && SubAnyMinMaxWith(left, right, out y) && !right.HasNUsesOrMore(3) && IsFreeToInvert(y, y.HasOneUse))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, x, Builder.CreateNot(left)), "invert_minmax_left");
            if (SubIncrementMin(instruction, LlvmIntrinsicKind.SMin, true) is { } signed) return signed;
            return SubIncrementMin(instruction, LlvmIntrinsicKind.UMin, false);
        }
        private LlvmInstruction? SubSinkSelect(LlvmBinaryOperator instruction, LlvmValue candidate, LlvmValue other, bool isLeft)
        {
            if (candidate is not LlvmSelectInstruction { HasOneUse: true } select || other != select.TrueValue && other != select.FalseValue) return null;
            bool trueArm = other == select.TrueValue;
            var value = trueArm ? select.FalseValue : select.TrueValue;
            var subtract = isLeft ? Builder.CreateSub(value, other) : Builder.CreateSub(other, value);
            var zero = LlvmConstant.GetNullValue(instruction.Type);
            return SubRewrite(new LlvmSelectInstruction(select.Condition, trueArm ? zero : subtract, trueArm ? subtract : zero)
                { Source = select.Source }, "sink_select");
        }
        private static bool SubAnyMinMaxWith(LlvmValue value, LlvmValue specific, out LlvmValue other)
        {
            if (value is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsMinMax(descriptor.Kind))
                return SubMinMaxWith(value, descriptor.Kind, specific, out other);
            other = null!; return false;
        }
        private LlvmInstruction? SubIncrementMin(LlvmBinaryOperator instruction, LlvmIntrinsicKind kind, bool signed)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (!SubIntrinsic(left, kind, out var minimum) || instruction.Type.GetScalarSizeInBits() == 1 || !left.HasOneUse && !right.HasOneUse) return null;
            for (int index = 0; index != 2; index++)
            {
                var increment = minimum.GetArgument(index); var other = minimum.GetArgument(1 - index);
                if (!SubAddLike(increment, out var x, out var one) || !SubOne(one) || !(signed ? SubNSW(increment) : SubNUW(increment)) ||
                    !SubMinMaxWith(right, kind, x, out var match) || match != other) continue;
                var comparison = Builder.CreateICmp(signed ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt, x, other);
                return SubRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, comparison, instruction.Type), "difference_incremented_min");
            }
            return null;
        }
    }
}
