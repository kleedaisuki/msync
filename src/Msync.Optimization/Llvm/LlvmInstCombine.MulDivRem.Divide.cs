// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// visitUDiv and visitSDiv, scalar integer paths.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitUDiv(LlvmBinaryOperator instruction)
        {
            Count("instcombine.udiv.visits");
            if (MdrSimplify(instruction) is { } simplified) return MdrReplace(instruction, simplified, "instcombine.udiv.simplify");
            if (CommonIDivTransforms(instruction) is { } common) return common;
            var left = instruction.Left; var right = instruction.Right;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt amount } shift && right is LlvmConstantInt divisor)
            {
                var shifted = divisor.Value.UShlOverflow(amount.Value, out bool overflow);
                if (!overflow) return MdrRewrite(MdrBin(LlvmOpcode.UDiv, shift.Left, MdrInt(divisor, shifted), exact: instruction.IsExact && shift.IsExact), "instcombine.udiv.fold_lshr_divisor");
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.UDiv, HasOneUse: true } inner && WillNotOverflowUnsignedMul(inner.Right, right, instruction))
                return MdrRewrite(MdrBin(LlvmOpcode.UDiv, inner.Left, Builder.CreateMul(inner.Right, right, nuw: true), exact: instruction.IsExact && inner.IsExact), "instcombine.udiv.combine_variable_divisors");
            if (MdrKnown(right, instruction).IsNegative)
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpUge, left, right), instruction.Type), "instcombine.udiv.high_bit_divisor");
            if (right is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext && sext.Operand.Type.IsIntegerTy(1))
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, left,
                    MdrInt(left, LlvmAPInt.GetAllOnes(left.Type.GetIntegerBitWidth()))), instruction.Type), "instcombine.udiv.sext_boolean_divisor");
            if (NarrowUDivURem(instruction) is { } narrow) return narrow;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasNoUnsignedWrap: true } mul } shr &&
                MdrMatchMul(mul, right, out var factor))
                return MdrRewrite(MdrBin(LlvmOpcode.LShr, factor, shr.Right, exact: instruction.IsExact && shr.IsExact), "instcombine.udiv.cancel_shifted_product");
            var log = TryGetLog2(right, true);
            if (log is null && MdrPower(right, true, instruction))
                log = Builder.CreateIntrinsic(LlvmIntrinsicKind.CTTZ, [right, LlvmConstantInt.GetTrue(instruction.Context)]);
            return log is null ? null : MdrReplace(instruction, Builder.CreateBinary(LlvmOpcode.LShr, left, log, instruction.Name, exact: instruction.IsExact), "instcombine.udiv.power_to_shift");
        }

        private LlvmInstruction? VisitSDiv(LlvmBinaryOperator instruction)
        {
            Count("instcombine.sdiv.visits");
            if (MdrSimplify(instruction) is { } simplified) return MdrReplace(instruction, simplified, "instcombine.sdiv.simplify");
            if (CommonIDivTransforms(instruction) is { } common) return common;
            var left = instruction.Left; var right = instruction.Right;
            uint width = instruction.Type.GetIntegerBitWidth();
            if (MdrAllOnes(right) || right is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext && sext.Operand.Type.IsIntegerTy(1))
                return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(left, 0), left, nsw: true), "instcombine.sdiv.negate");
            if (right is LlvmConstantInt { Value.IsMinSignedValue: true })
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, left, right), instruction.Type), "instcombine.sdiv.signed_min_divisor");
            if (instruction.IsExact)
            {
                if (right is LlvmConstantInt c && c.Value.IsPowerOf2 && !c.IsNegative)
                    return MdrRewrite(MdrBin(LlvmOpcode.AShr, left, MdrInt(right, c.Value.GetActiveBits() - 1), exact: true), "instcombine.sdiv.exact_power_to_shift");
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoSignedWrap: true, Left: LlvmConstantInt { IsOne: true } } power)
                    return MdrRewrite(MdrBin(LlvmOpcode.AShr, left, power.Right, exact: true), "instcombine.sdiv.exact_variable_power");
                if (right is LlvmConstantInt negative && (-negative.Value).IsPowerOf2)
                {
                    var shift = Builder.CreateBinary(LlvmOpcode.AShr, left, MdrInt(right, (-negative.Value).GetActiveBits() - 1), instruction.Name + ".neg", exact: true);
                    return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(left, 0), shift, nsw: true), "instcombine.sdiv.exact_negative_power");
                }
            }
            if (right is LlvmConstantInt divisor)
            {
                if (left is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } cast && cast.Operand.Type.GetIntegerBitWidth() >= divisor.Value.GetSignificantBits())
                {
                    var narrow = MdrInt(cast.Operand, divisor.Value.Trunc(cast.Operand.Type.GetIntegerBitWidth()));
                    return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.SExt, Builder.CreateBinary(LlvmOpcode.SDiv, cast.Operand, narrow), instruction.Type), "instcombine.sdiv.narrow_sext");
                }
                if (!divisor.Value.IsMinSignedValue && left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true, Left: LlvmConstantInt { IsZero: true } } neg)
                    return MdrRewrite(MdrBin(LlvmOpcode.SDiv, neg.Right, MdrInt(right, -divisor.Value), exact: instruction.IsExact), "instcombine.sdiv.move_constant_negation");
            }
            var a = DynCastNegVal(left); var b = DynCastNegVal(right);
            if (a is not null && b is not null && (!MdrKnown(a, instruction).GetSignedMinValue().IsMinSignedValue ||
                left is LlvmBinaryOperator { HasNoSignedWrap: true } && !MdrKnown(b, instruction).Zero.IsZero))
                return MdrRewrite(MdrBin(LlvmOpcode.SDiv, a, b, exact: instruction.IsExact), "instcombine.sdiv.cancel_negations");
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true, HasOneUse: true, Left: LlvmConstantInt { IsZero: true } } negated)
                return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(left, 0), Builder.CreateBinary(LlvmOpcode.SDiv, negated.Right, right, instruction.Name, exact: instruction.IsExact), nsw: true), "instcombine.sdiv.sink_negation");
            LlvmValue? absoluteOperand = null;
            if (left.HasOneUse && MdrAbs(left, out var absLeft, out bool leftPoison) && leftPoison && ReferenceEquals(absLeft, right)) absoluteOperand = absLeft;
            else if (right.HasOneUse && MdrAbs(right, out var absRight, out bool rightPoison) && rightPoison && ReferenceEquals(absRight, left)) absoluteOperand = absRight;
            if (absoluteOperand is not null)
            {
                var cmp = Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, absoluteOperand, MdrInt(instruction, LlvmAPInt.GetAllOnes(width)));
                return MdrReplace(instruction, Builder.CreateSelect(cmp, MdrInt(instruction, 1), MdrInt(instruction, LlvmAPInt.GetAllOnes(width))), "instcombine.sdiv.abs_sign");
            }
            var known = MdrKnown(left, instruction);
            if (!instruction.IsExact && right is LlvmConstantInt powerConstant &&
                (powerConstant.Value.IsPowerOf2 || (-powerConstant.Value).IsPowerOf2) && known.CountMinTrailingZeros() >= powerConstant.Value.CountTrailingZeros())
            { instruction.IsExact = true; Count("instcombine.sdiv.infer_exact"); return instruction; }
            if (known.IsNonNegative)
            {
                if (MdrKnown(right, instruction).IsNonNegative)
                    return MdrRewrite(MdrBin(LlvmOpcode.UDiv, left, right, exact: instruction.IsExact), "instcombine.sdiv.nonnegative_to_unsigned");
                if (right is LlvmConstantInt negativePower && (-negativePower.Value).IsPowerOf2)
                {
                    var shift = Builder.CreateBinary(LlvmOpcode.LShr, left, MdrInt(right, (-negativePower.Value).GetActiveBits() - 1), exact: instruction.IsExact);
                    return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(left, 0), shift), "instcombine.sdiv.nonnegative_negative_power");
                }
                if (MdrPower(right, true, instruction))
                    return MdrRewrite(MdrBin(LlvmOpcode.UDiv, left, right, exact: instruction.IsExact), "instcombine.sdiv.nonnegative_variable_power");
            }
            if (LlvmKnownBitsAnalysis.IsKnownNegation(left, right))
            {
                var cmp = Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, left, MdrInt(left, LlvmAPInt.GetSignedMinValue(width)));
                return MdrReplace(instruction, Builder.CreateSelect(cmp, MdrInt(left, 1), MdrInt(left, LlvmAPInt.GetAllOnes(width))), "instcombine.sdiv.known_negation");
            }
            return null;
        }
    }
}
