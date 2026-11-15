// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 3068-3159,3248-3407.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpSubConstant(LlvmICmpInstruction compare, LlvmBinaryOperator sub, LlvmAPInt constant)
        {
            Count("icmp.constant.sub.queries");
            LlvmValue x = sub.Left, y = sub.Right;
            var predicate = compare.Predicate;
            var one = IccInt(constant, 1);
            if (y is LlvmBinaryOperator { Opcode: LlvmOpcode.URem, Right: LlvmConstantInt d } remainder && ReferenceEquals(remainder.Left, x) && !d.Value.IsZero)
            {
                if (predicate == LlvmCmpPredicate.ICmpUgt && constant.Ult(d.Value)) return New(predicate, x, d.Value - one, "rounded-multiple-gt");
                if (predicate == LlvmCmpPredicate.ICmpUlt && !constant.IsZero && constant.Ule(d.Value)) return New(predicate, x, d.Value, "rounded-multiple-lt");
            }
            if (ICmpIsEquality(predicate) && x is LlvmConstant c)
                return IccCompare(predicate, y, LlvmConstantExpr.GetSub(c, IccConstant(sub, constant)));
            var swapped = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            if (x is LlvmConstantInt lhs && (ICmpIsUnsigned(predicate) && sub.HasNoUnsignedWrap || ICmpIsSigned(predicate) && sub.HasNoSignedWrap))
            {
                LlvmAPInt result = ICmpIsSigned(predicate) ? lhs.Value.SSubOverflow(constant, out bool overflow) : lhs.Value.USubOverflow(constant, out overflow);
                if (!overflow) return New(swapped, y, result, "nowrap-constant");
            }
            if (ICmpIsEquality(predicate) && constant.IsZero && !sub.Users.Any(user => user is LlvmPhiInstruction))
                return IccCompare(predicate, x, y);
            if (!sub.HasOneUse) return null;
            if (sub.HasNoSignedWrap)
            {
                if (predicate == LlvmCmpPredicate.ICmpSgt && constant.IsAllOnes) return IccCompare(LlvmCmpPredicate.ICmpSge, x, y);
                if (predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSlt && constant.IsZero) return IccCompare(predicate, x, y);
                if (predicate == LlvmCmpPredicate.ICmpSlt && constant.IsOne) return IccCompare(LlvmCmpPredicate.ICmpSle, x, y);
            }
            if (x is not LlvmConstantInt value) return null;
            if (predicate == LlvmCmpPredicate.ICmpUlt && constant.IsPowerOf2 && (value.Value & (constant - one)) == constant - one)
                return IccCompare(LlvmCmpPredicate.ICmpEq, Builder.CreateOr(y, IccConstant(y, constant - one)), x);
            if (predicate == LlvmCmpPredicate.ICmpUgt && (constant + one).IsPowerOf2 && (value.Value & constant) == constant)
                return IccCompare(LlvmCmpPredicate.ICmpNe, Builder.CreateOr(y, IccConstant(y, constant)), x);
            Count("icmp.constant.sub.canonical-add");
            LlvmValue add = Builder.CreateAdd(y, IccConstant(y, ~value.Value), "notsub", sub.HasNoUnsignedWrap, sub.HasNoSignedWrap);
            return IccCompare(swapped, add, IccConstant(sub, ~constant));
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmValue input, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.sub." + reason); return IccCompare(pred, input, IccConstant(input, rhs)); }
        }

        private LlvmInstruction? FoldICmpAddConstant(LlvmICmpInstruction compare, LlvmBinaryOperator add, LlvmAPInt constant)
        {
            Count("icmp.constant.add.queries");
            LlvmValue x = add.Left, y = add.Right;
            LlvmCmpPredicate predicate = compare.Predicate;
            uint width = constant.BitWidth;
            if (predicate == LlvmCmpPredicate.ICmpUlt && add.HasNoUnsignedWrap)
            {
                LlvmValue? input = ShiftedAdd(x, y) ?? ShiftedAdd(y, x);
                if (input is not null) return New(predicate, input, constant, "nuw-self-shift");
            }
            if (ICmpIsEquality(predicate) || y is not LlvmConstantInt offset) return null;
            LlvmAPInt delta = offset.Value, one = IccInt(constant, 1);
            if (add.HasNoUnsignedWrap && predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUlt)
            {
                var result = constant.USubOverflow(delta, out bool overflow);
                if (!overflow) return New(predicate, x, result, "nuw-offset");
            }
            var chosen = compare.SameSign ? ICmpSignedPredicate(predicate) : predicate;
            if (add.HasNoSignedWrap && chosen is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSlt)
            {
                var result = constant.SSubOverflow(delta, out bool overflow);
                if (!overflow) return New(chosen, x, result, "nsw-offset");
            }
            if (ICmpIsUnsigned(predicate) && add.HasNoSignedWrap && constant.IsNonNegative && (constant - delta).IsNonNegative)
            {
                Count("icmp.constant.add.range-query");
                var range = LlvmConstantRangeAnalysis.Compute(x, forSigned: true, context: compare, tree: DT, metrics: Metrics,
                    useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC);
                if (range.Add(new LlvmConstantRange(delta)).IsAllNonNegative)
                    return New(ICmpSignedPredicate(predicate), x, constant - delta, "nonnegative-signed");
            }
            var region = LlvmConstantRange.MakeExactICmpRegion(predicate, constant).Subtract(delta);
            if (ICmpIsSigned(predicate))
            {
                if (region.Lower.IsMinSignedValue) return New(LlvmCmpPredicate.ICmpSlt, x, region.Upper, "signed-range-lower");
                if (region.Upper.IsMinSignedValue) return New(LlvmCmpPredicate.ICmpSge, x, region.Lower, "signed-range-upper");
            }
            else
            {
                if (region.Lower.IsMinValue) return New(LlvmCmpPredicate.ICmpUlt, x, region.Upper, "unsigned-range-lower");
                if (region.Upper.IsMinValue) return New(LlvmCmpPredicate.ICmpUge, x, region.Lower, "unsigned-range-upper");
            }
            var max = LlvmAPInt.GetSignedMaxValue(width); var min = LlvmAPInt.GetSignedMinValue(width);
            if (predicate == LlvmCmpPredicate.ICmpUgt && constant == delta + max) return New(LlvmCmpPredicate.ICmpSlt, x, -delta, "unsigned-to-negative");
            if (predicate == LlvmCmpPredicate.ICmpUlt && constant == delta + min) return New(LlvmCmpPredicate.ICmpSgt, x, ~delta, "unsigned-to-nonnegative");
            if (predicate == LlvmCmpPredicate.ICmpSgt && constant == delta - one) return New(LlvmCmpPredicate.ICmpUlt, x, max - constant, "signed-to-upper");
            if (predicate == LlvmCmpPredicate.ICmpSlt && constant == delta) return New(LlvmCmpPredicate.ICmpUgt, x, constant ^ max, "signed-to-lower");
            if (predicate == LlvmCmpPredicate.ICmpUlt && delta.IsAllOnes && IsKnownNonZero(x, compare))
                return New(LlvmCmpPredicate.ICmpUle, x, constant, "nonnull-predecessor");
            if (!add.HasOneUse) return null;
            if (predicate == LlvmCmpPredicate.ICmpUlt && constant.IsPowerOf2 && (delta & (constant - one)).IsZero)
                return New(LlvmCmpPredicate.ICmpEq, Builder.CreateAnd(x, IccConstant(x, -constant)), -delta, "low-range-mask");
            if (predicate == LlvmCmpPredicate.ICmpUlt && delta.IsPowerOf2 && constant == -delta)
                return New(LlvmCmpPredicate.ICmpNe, Builder.CreateAnd(x, IccConstant(x, constant)), constant.Shl(1), "negated-range-mask");
            if (predicate == LlvmCmpPredicate.ICmpUgt && (constant + one).IsPowerOf2 && (delta & constant).IsZero)
                return New(LlvmCmpPredicate.ICmpNe, Builder.CreateAnd(x, IccConstant(x, ~constant)), -delta, "upper-range-mask");
            if (predicate == LlvmCmpPredicate.ICmpUgt)
                return New(LlvmCmpPredicate.ICmpUlt, Builder.CreateAdd(x, IccConstant(x, delta - constant - one)), ~constant, "canonical-range");
            if (x is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext && ShouldChangeType(add.Type, ext.Operand.Type))
            {
                var sourceRegion = region.Truncate(ext.Operand.Type.GetIntegerBitWidth(), LlvmConstantRange.NoUnsignedWrap);
                sourceRegion.GetEquivalentICmp(out var newPredicate, out var rhs, out var equivalentOffset);
                LlvmValue value = equivalentOffset.IsZero ? ext.Operand : Builder.CreateAdd(ext.Operand, IccConstant(ext.Operand, equivalentOffset));
                return New(newPredicate, value, rhs, "narrow-zext-range");
            }
            return null;
            LlvmValue? ShiftedAdd(LlvmValue a, LlvmValue b) => b is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt amount } shift &&
                ReferenceEquals(a, shift.Left) && amount.Value.Ult(new LlvmAPInt(width, width)) && constant.Ule(LlvmAPInt.GetOneBitSet(width, (uint)amount.Value.GetZExtValue())) ? a : null;
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmValue value, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.add." + reason); return IccCompare(pred, value, IccConstant(value, rhs)); }
        }
    }
}
