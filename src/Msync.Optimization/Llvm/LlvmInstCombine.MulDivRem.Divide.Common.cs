// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// commonIDivTransforms and foldIDivShl, scalar represented paths.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? CommonIDivTransforms(LlvmBinaryOperator instruction)
        {
            if (CommonIDivRemTransforms(instruction) is { } common) return common;
            var left = instruction.Left; var right = instruction.Right;
            bool signed = instruction.Opcode == LlvmOpcode.SDiv;
            if (right is LlvmConstantInt divisor)
            {
                if (CommonIDivConstants(instruction, divisor, signed) is { } constants) return constants;
                if (!divisor.IsZero && FoldBinOpIntoSelectOrPhi(instruction) is { } threaded) return threaded;
            }
            if (MdrOne(left))
            {
                if (!signed)
                    return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, right, left), instruction.Type), "instcombine.idiv.one_unsigned");
                var frozen = MdrFreeze(right);
                var increment = Builder.CreateAdd(frozen, left);
                var comparison = Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, increment, MdrInt(left, 3));
                return MdrReplace(instruction, Builder.CreateSelect(comparison, frozen, MdrInt(left, 0)), "instcombine.idiv.one_signed");
            }
            if (SimplifyDemandedInstructionBits(instruction)) return instruction;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Right: LlvmBinaryOperator remainder } difference &&
                remainder.Opcode == (signed ? LlvmOpcode.SRem : LlvmOpcode.URem) && ReferenceEquals(remainder.Left, difference.Left) && ReferenceEquals(remainder.Right, right))
                return MdrRewrite(MdrBin(instruction.Opcode, difference.Left, right), "instcombine.idiv.subtract_remainder");
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shift && ReferenceEquals(shift.Left, right) &&
                (signed ? shift.HasNoSignedWrap : shift.HasNoUnsignedWrap))
                return MdrRewrite(MdrBin(LlvmOpcode.Shl, MdrInt(left, 1), shift.Right, !signed, signed), "instcombine.idiv.divisor_shift");
            if (MdrMatchMul(right, left, out var factor) && right is LlvmBinaryOperator denominator &&
                (signed ? denominator.HasNoSignedWrap : denominator.HasNoUnsignedWrap))
            {
                ReplaceOperand(instruction, 0, MdrInt(left, 1));
                return MdrRewrite(ReplaceOperand(instruction, 1, factor), "instcombine.idiv.divisor_multiple");
            }
            if (!signed && right is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasNoUnsignedWrap: true, HasOneUse: true } &&
                left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true } numerator &&
                MdrMatchMul(right, numerator.Left, out var other))
                return MdrRewrite(MdrBin(LlvmOpcode.UDiv, Builder.CreateBinary(LlvmOpcode.Shl, MdrInt(left, 1), numerator.Right, nuw: true), other,
                    exact: instruction.IsExact), "instcombine.idiv.cancel_shifted_factor");
            if (FoldIDivShl(instruction) is { } shifted) return MdrReplace(instruction, shifted, "instcombine.idiv.common_shift");
            if (left is LlvmBinaryOperator inner && inner.Opcode == instruction.Opcode &&
                MdrMatchMul(inner.Left, right, out var innerFactor) && inner.Left is LlvmBinaryOperator innerProduct &&
                (signed ? innerProduct.HasNoSignedWrap : innerProduct.HasNoUnsignedWrap))
                return MdrRewrite(MdrBin(instruction.Opcode, innerFactor, inner.Right, exact: instruction.IsExact && inner.IsExact), "instcombine.idiv.nested_cancel_factor");
            if (right is LlvmSelectInstruction { HasOneUse: true } select)
            {
                bool trueOne = MdrOne(select.TrueValue), falseOne = MdrOne(select.FalseValue);
                if ((trueOne || falseOne) && MdrSafeSelectDivisor(trueOne ? select.FalseValue : select.TrueValue, signed, instruction))
                {
                    var div = Builder.CreateBinary(instruction.Opcode, left, trueOne ? select.FalseValue : select.TrueValue, exact: instruction.IsExact);
                    return MdrReplace(instruction, Builder.CreateSelect(select.Condition, trueOne ? left : div, trueOne ? div : left), "instcombine.idiv.sink_select_one");
                }
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } lhs && right is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } rhs)
            {
                if (MdrMatchMul(rhs, lhs.Left, out var z) && MdrCancelProductDiv(instruction, lhs.Right, z, lhs, rhs) is { } a) return a;
                if (MdrMatchMul(rhs, lhs.Right, out z) && MdrCancelProductDiv(instruction, lhs.Left, z, lhs, rhs) is { } b) return b;
            }
            return null;
        }

        private bool MdrSafeSelectDivisor(LlvmValue value, bool signed, LlvmInstruction context) => signed ?
            value is LlvmConstantInt { IsZero: false, IsMinusOne: false } : MdrNonZero(value, context) &&
            LlvmValueTracking.IsGuaranteedNotToBePoison(value, DT, context, Metrics, AC, SQ.AllowEphemerals);
        private LlvmInstruction? MdrCancelProductDiv(LlvmBinaryOperator original, LlvmValue a, LlvmValue b, LlvmBinaryOperator lhs, LlvmBinaryOperator rhs)
        {
            bool signed = original.Opcode == LlvmOpcode.SDiv;
            if (signed && lhs.HasNoSignedWrap && rhs.HasNoSignedWrap && b is LlvmConstantInt { IsMinusOne: false })
                return MdrRewrite(MdrBin(LlvmOpcode.SDiv, a, b), "instcombine.idiv.cancel_products_signed");
            if (!signed && lhs.HasNoUnsignedWrap && (rhs.HasNoUnsignedWrap ||
                a is LlvmConstantInt ca && b is LlvmConstantInt cb && cb.Value.Ule(ca.Value)))
                return MdrRewrite(MdrBin(LlvmOpcode.UDiv, a, b), "instcombine.idiv.cancel_products_unsigned");
            return null;
        }

        private LlvmInstruction? CommonIDivConstants(LlvmBinaryOperator instruction, LlvmConstantInt divisor, bool signed)
        {
            var c2 = divisor.Value;
            if (instruction.Left is LlvmBinaryOperator inner && inner.Opcode == instruction.Opcode && inner.Right is LlvmConstantInt c1)
            {
                bool overflow;
                var combined = signed ? c1.Value.SMulOverflow(c2, out overflow) : c1.Value.UMulOverflow(c2, out overflow);
                if (!overflow) return MdrRewrite(MdrBin(instruction.Opcode, inner.Left, MdrInt(divisor, combined)), "instcombine.idiv.combine_constant_divisors");
            }
            if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt multiplier } mul &&
                (signed ? mul.HasNoSignedWrap : mul.HasNoUnsignedWrap))
            {
                if (MdrMultiple(c2, multiplier.Value, signed, out var mulQuotient))
                    return MdrRewrite(MdrBin(instruction.Opcode, mul.Left, MdrInt(divisor, mulQuotient), exact: instruction.IsExact), "instcombine.idiv.divide_constant_factor");
                if (MdrMultiple(multiplier.Value, c2, signed, out mulQuotient))
                    return MdrRewrite(MdrBin(LlvmOpcode.Mul, mul.Left, MdrInt(divisor, mulQuotient), !signed && mul.HasNoUnsignedWrap, mul.HasNoSignedWrap), "instcombine.idiv.fold_to_multiply");
                if (mul.HasOneUse)
                {
                    Count("instcombine.idiv.gcd_queries");
                    var gcd = LlvmAPInt.GreatestCommonDivisor(multiplier.Value, c2, signed);
                    if (gcd.Ugt(new LlvmAPInt(gcd.BitWidth, 1)))
                    {
                        var a = signed ? multiplier.Value.SDiv(gcd) : multiplier.Value.UDiv(gcd);
                        var b = signed ? c2.SDiv(gcd) : c2.UDiv(gcd);
                        var product = Builder.CreateMul(mul.Left, MdrInt(divisor, a), mul.Name, mul.HasNoUnsignedWrap, mul.HasNoSignedWrap);
                        return MdrRewrite(MdrBin(instruction.Opcode, product, MdrInt(divisor, b), exact: instruction.IsExact), "instcombine.idiv.reduce_gcd");
                    }
                }
            }
            if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt amount } shift &&
                (signed ? shift.HasNoSignedWrap && amount.Value.Ult(new LlvmAPInt(amount.Value.BitWidth, amount.Value.BitWidth - 1)) :
                    shift.HasNoUnsignedWrap && amount.Value.Ult(new LlvmAPInt(amount.Value.BitWidth, amount.Value.BitWidth))))
            {
                uint bits = (uint)amount.GetZExtValue();
                var scale = LlvmAPInt.GetOneBitSet(c2.BitWidth, bits);
                if (MdrMultiple(c2, scale, signed, out var shiftQuotient))
                    return MdrRewrite(MdrBin(instruction.Opcode, shift.Left, MdrInt(divisor, shiftQuotient), exact: instruction.IsExact), "instcombine.idiv.divide_shift_factor");
                if (MdrMultiple(scale, c2, signed, out shiftQuotient))
                    return MdrRewrite(MdrBin(LlvmOpcode.Mul, shift.Left, MdrInt(divisor, shiftQuotient), !signed && shift.HasNoUnsignedWrap, shift.HasNoSignedWrap), "instcombine.idiv.shift_to_multiply");
                uint common = Math.Min(c2.CountTrailingZeros(), bits);
                if (shift.HasOneUse && common > 0)
                {
                    var reduced = Builder.CreateBinary(LlvmOpcode.Shl, shift.Left, MdrInt(divisor, bits - common), shift.Name, shift.HasNoUnsignedWrap, shift.HasNoSignedWrap);
                    return MdrRewrite(MdrBin(instruction.Opcode, reduced, MdrInt(divisor, signed ? c2.AShr(common) : c2.LShr(common)), exact: instruction.IsExact), "instcombine.idiv.reduce_shift_factor");
                }
            }
            if (instruction.Left is LlvmBinaryOperator add && add.Right is LlvmConstantInt addend &&
                (add.Opcode == LlvmOpcode.Add || add.Opcode == LlvmOpcode.Or && add.IsDisjoint) &&
                add.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt coefficient } multiplyAdd && coefficient.Value == c2)
            {
                if (signed && (add.Opcode == LlvmOpcode.Or || add.HasNoSignedWrap) && multiplyAdd.HasNoSignedWrap && MdrMultiple(addend.Value, c2, true, out var quotient))
                    return MdrRewrite(MdrBin(LlvmOpcode.Add, multiplyAdd.Left, MdrInt(divisor, quotient), nsw: true), "instcombine.idiv.distribute_signed_add");
                if (!signed && (add.Opcode == LlvmOpcode.Or || add.HasNoUnsignedWrap) && multiplyAdd.HasNoUnsignedWrap)
                    return MdrRewrite(MdrBin(LlvmOpcode.Add, multiplyAdd.Left, MdrInt(divisor, addend.Value.UDiv(c2)), nuw: true), "instcombine.idiv.distribute_unsigned_add");
            }
            return null;
        }

        private LlvmValue? FoldIDivShl(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right; bool signed = instruction.Opcode == LlvmOpcode.SDiv;
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shl && MdrMatchMul(left, shl.Left, out var factor) && left is LlvmBinaryOperator mul)
            {
                bool nuw = mul.HasNoUnsignedWrap && shl.HasNoUnsignedWrap, nsw = mul.HasNoSignedWrap && shl.HasNoSignedWrap;
                if (!signed && nuw) return Builder.CreateBinary(LlvmOpcode.LShr, factor, shl.Right, exact: instruction.IsExact);
                if (signed && nsw && (mul.HasOneUse || shl.HasOneUse))
                    return Builder.CreateBinary(LlvmOpcode.SDiv, factor, Builder.CreateBinary(LlvmOpcode.Shl, MdrInt(left, 1), shl.Right), exact: instruction.IsExact);
            }
            if (left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } a || right is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } b) return null;
            if (ReferenceEquals(a.Right, b.Right))
            {
                if (!signed && ((a.HasNoUnsignedWrap && b.HasNoUnsignedWrap) || (a.HasNoUnsignedWrap && a.HasNoSignedWrap && b.HasNoSignedWrap)))
                    return Builder.CreateBinary(LlvmOpcode.UDiv, a.Left, b.Left, exact: instruction.IsExact);
                if (signed && a.HasNoSignedWrap && b.HasNoSignedWrap && b.HasNoUnsignedWrap)
                    return Builder.CreateBinary(LlvmOpcode.SDiv, a.Left, b.Left, exact: instruction.IsExact);
            }
            if (ReferenceEquals(a.Left, b.Left) && (signed ? a.HasNoSignedWrap && b.HasNoSignedWrap : a.HasNoUnsignedWrap && b.HasNoUnsignedWrap))
            {
                var numerator = Builder.CreateBinary(LlvmOpcode.Shl, MdrInt(left, 1), a.Right, "shl.dividend", true,
                    signed ? a.HasNoUnsignedWrap || b.HasNoUnsignedWrap : a.HasNoSignedWrap);
                return Builder.CreateBinary(LlvmOpcode.LShr, numerator, b.Right, exact: instruction.IsExact);
            }
            return null;
        }
    }
}
