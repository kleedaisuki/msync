// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 2761-3067.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpSRemConstant(LlvmICmpInstruction compare, LlvmBinaryOperator remainder, LlvmAPInt constant)
        {
            Count("icmp.constant.srem.queries");
            var predicate = compare.Predicate;
            if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUlt)
            {
                if (remainder.Right is not LlvmConstantInt divisor || divisor.Value.IsZero) return null;
                var normalized = constant;
                if (predicate == LlvmCmpPredicate.ICmpUlt) { Debug.Assert(!normalized.IsZero); normalized -= IccInt(normalized, 1); }
                if (constant.IsNegative) normalized = ~normalized;
                var absolute = divisor.Value.IsNegative ? -divisor.Value : divisor.Value;
                if (!normalized.Uge(absolute - IccInt(absolute, 1))) return null;
                Count("icmp.constant.srem.unsigned-sign");
                return IccCompare(predicate == LlvmCmpPredicate.ICmpUgt ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSgt,
                    remainder, IccConstant(remainder, predicate == LlvmCmpPredicate.ICmpUgt ? LlvmAPInt.GetZero(constant.BitWidth) : LlvmAPInt.GetAllOnes(constant.BitWidth)));
            }
            if (predicate is not (LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) || !remainder.HasOneUse ||
                remainder.Right is not LlvmConstantInt power || !power.Value.IsPowerOf2) return null;
            if (ICmpIsEquality(predicate) ? !constant.IsStrictlyPositive : !constant.IsZero) return null;
            var sign = LlvmAPInt.GetSignedMinValue(constant.BitWidth);
            var mask = sign | (power.Value - IccInt(constant, 1));
            LlvmValue and = Builder.CreateAnd(remainder.Left, IccConstant(remainder, mask));
            Count("icmp.constant.srem.power-mask");
            return ICmpIsEquality(predicate) || predicate == LlvmCmpPredicate.ICmpSgt
                ? IccCompare(predicate, and, IccConstant(and, constant))
                : IccCompare(LlvmCmpPredicate.ICmpUgt, and, IccConstant(and, sign));
        }

        private LlvmInstruction? FoldICmpUDivConstant(LlvmICmpInstruction compare, LlvmBinaryOperator division, LlvmAPInt constant)
        {
            Count("icmp.constant.udiv-lhs.queries");
            if (division.Left is not LlvmConstantInt numerator) return null;
            Debug.Assert(!numerator.Value.IsZero);
            if (compare.Predicate == LlvmCmpPredicate.ICmpUgt)
            {
                Debug.Assert(!constant.IsMaxValue);
                Count("icmp.constant.udiv-lhs.gt");
                return IccCompare(LlvmCmpPredicate.ICmpUle, division.Right, IccConstant(division, numerator.Value.UDiv(constant + IccInt(constant, 1))));
            }
            if (compare.Predicate == LlvmCmpPredicate.ICmpUlt)
            {
                Debug.Assert(!constant.IsZero);
                Count("icmp.constant.udiv-lhs.lt");
                return IccCompare(LlvmCmpPredicate.ICmpUgt, division.Right, IccConstant(division, numerator.Value.UDiv(constant)));
            }
            return null;
        }

        private LlvmInstruction? FoldICmpDivConstant(LlvmICmpInstruction compare, LlvmBinaryOperator division, LlvmAPInt constant)
        {
            Count("icmp.constant.div.queries");
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue input = division.Left, divisor = division.Right;
            bool signed = division.Opcode == LlvmOpcode.SDiv;
            if (ICmpIsEquality(predicate) && division.HasOneUse && constant.IsNegative && (!signed || constant.IsMinSignedValue))
            {
                Count("icmp.constant.div.extreme-equality");
                return new LlvmBinaryOperator(predicate == LlvmCmpPredicate.ICmpEq ? LlvmOpcode.And : LlvmOpcode.Or,
                    Builder.CreateICmp(predicate, input, IccConstant(input, constant)), Builder.CreateICmp(predicate, divisor, IccConstant(divisor, 1)));
            }
            if (divisor is not LlvmConstantInt divisorConstant) return null;
            var factor = divisorConstant.Value;
            if (!ICmpIsEquality(predicate) && signed != ICmpIsSigned(predicate))
            {
                if (!signed || !factor.IsStrictlyPositive || !IsKnownNonNegative(input, compare)) return null;
                signed = false;
            }
            if (factor.IsZero || factor.IsOne || signed && factor.IsAllOnes) return null;
            var product = constant * factor;
            bool productOverflow = (signed ? product.SDiv(factor) : product.UDiv(factor)) != constant;
            var size = division.IsExact ? IccInt(factor, 1) : factor;
            int lowOverflow = 0, highOverflow = 0;
            var low = LlvmAPInt.GetZero(constant.BitWidth); var high = low;
            var one = IccInt(constant, 1);
            if (!signed)
            {
                low = product; highOverflow = lowOverflow = productOverflow ? 1 : 0;
                if (highOverflow == 0) high = Add(low, size, false, out highOverflow);
            }
            else if (factor.IsStrictlyPositive)
            {
                if (constant.IsZero) { low = -(size - one); high = size; }
                else if (constant.IsStrictlyPositive)
                {
                    low = product; highOverflow = lowOverflow = productOverflow ? 1 : 0;
                    if (highOverflow == 0) high = Add(product, size, true, out highOverflow);
                }
                else
                {
                    high = product + one; lowOverflow = highOverflow = productOverflow ? -1 : 0;
                    if (lowOverflow == 0) { low = Add(high, -size, true, out lowOverflow); lowOverflow = lowOverflow == 0 ? 0 : -1; }
                }
            }
            else
            {
                if (division.IsExact) size = -size;
                if (constant.IsZero)
                {
                    low = size + one; high = -size;
                    if (high == factor) highOverflow = 1;
                }
                else if (constant.IsStrictlyPositive)
                {
                    high = product + one; highOverflow = lowOverflow = productOverflow ? -1 : 0;
                    if (lowOverflow == 0) { low = Add(high, size, true, out lowOverflow); lowOverflow = lowOverflow == 0 ? 0 : -1; }
                }
                else
                {
                    low = product; lowOverflow = highOverflow = productOverflow ? 1 : 0;
                    if (highOverflow == 0) { high = product.SSubOverflow(size, out bool overflow); highOverflow = overflow ? 1 : 0; }
                }
                predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            }
            Count("icmp.constant.div.range-derived");
            if (lowOverflow != 0) Count(lowOverflow < 0 ? "icmp.constant.div.low-underflow" : "icmp.constant.div.low-overflow");
            if (highOverflow != 0) Count(highOverflow < 0 ? "icmp.constant.div.high-underflow" : "icmp.constant.div.high-overflow");
            switch (predicate)
            {
                case LlvmCmpPredicate.ICmpEq:
                case LlvmCmpPredicate.ICmpNe:
                    bool eq = predicate == LlvmCmpPredicate.ICmpEq;
                    if (lowOverflow != 0 && highOverflow != 0) return Boolean(!eq);
                    if (highOverflow != 0) return New(eq ? GE() : LT(), low);
                    if (lowOverflow != 0) return New(eq ? LT() : GE(), high);
                    return ReplaceInstUsesWith(compare, InsertRangeTest(input, low, high, signed, eq));
                case LlvmCmpPredicate.ICmpUlt:
                case LlvmCmpPredicate.ICmpSlt:
                    if (lowOverflow != 0) return Boolean(lowOverflow == 1);
                    return New(predicate, low);
                case LlvmCmpPredicate.ICmpUgt:
                case LlvmCmpPredicate.ICmpSgt:
                    if (highOverflow != 0) return Boolean(highOverflow == -1);
                    return New(predicate == LlvmCmpPredicate.ICmpUgt ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpSge, high);
                default: throw new System.Diagnostics.UnreachableException();
            }
            LlvmCmpPredicate GE() => signed ? LlvmCmpPredicate.ICmpSge : LlvmCmpPredicate.ICmpUge;
            LlvmCmpPredicate LT() => signed ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt;
            LlvmInstruction? Boolean(bool value) { Count("icmp.constant.div.constant-outcome"); return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Context, value)); }
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmAPInt rhs) => IccCompare(pred, input, IccConstant(input, rhs));
            static LlvmAPInt Add(LlvmAPInt a, LlvmAPInt b, bool signed, out int overflow)
            {
                LlvmAPInt result = signed ? a.SAddOverflow(b, out bool signedOverflow) : a.UAddOverflow(b, out signedOverflow);
                overflow = signedOverflow ? 1 : 0; return result;
            }
        }
    }
}
