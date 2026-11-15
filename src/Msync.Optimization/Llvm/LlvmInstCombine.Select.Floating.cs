// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectWithFCmpToFabs/foldSelectOfOrderedFAbsCmpOfNaNScrubbedValue.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool SelectIsAnyZeroFP(LlvmValue value) => value is LlvmConstantFP { Value.IsZero: true };
        private static bool SelectIsPositiveZeroFP(LlvmValue value) => value is LlvmConstantFP { Value.IsZero: true, Value.IsNegative: false };
        private static bool SelectIsOrdered(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.FCmpFalse and <= LlvmCmpPredicate.FCmpOrd;
        private static bool SelectIsUnordered(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.FCmpUno and <= LlvmCmpPredicate.FCmpTrue;
        private LlvmValue SelectFAbs(LlvmValue value, LlvmFastMathFlags flags)
        {
            return Builder.CreateFAbs(value, flags);
        }
        private LlvmInstruction? FoldSelectWithFCmpToFabs(LlvmSelectInstruction select)
        {
            LlvmValue condition = select.Condition;
            bool changed = false;
            for (int swap = 0; swap != 2; ++swap)
            {
                LlvmValue whenTrue = swap == 0 ? select.TrueValue : select.FalseValue;
                LlvmValue value = swap == 0 ? select.FalseValue : select.TrueValue;
                if (condition is not LlvmFCmpInstruction compare || !ReferenceEquals(compare.Left, value) || !SelectIsAnyZeroFP(compare.Right)) continue;
                LlvmCmpPredicate predicate = compare.Predicate;
                bool noNaNs = (select.FastMathFlags & LlvmFastMathFlags.NoNaNs) != 0;
                bool compareNoNaNs = (compare.FastMathFlags & LlvmFastMathFlags.NoNaNs) != 0;
                if (whenTrue is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } positiveSub && SelectIsPositiveZeroFP(positiveSub.Left) && ReferenceEquals(positiveSub.Right, value) &&
                    (compareNoNaNs || noNaNs || (select.HasOneUse && LlvmValueTracking.CanIgnoreSignBitOfNaN(select.FirstUse!, Metrics)) ||
                    LlvmKnownFPClasses.Compute(value, LlvmFPClassTest.NaN, compare, DT, Metrics, assumptions: AC, conditions: SQ.DC).IsKnownNeverNaN()))
                {
                    if ((swap == 0 && predicate is LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUle) ||
                        (swap != 0 && predicate is LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpUgt))
                        return ReplaceInstUsesWith(select, SelectFAbs(value, select.FastMathFlags));
                }
                if (!SelectMatchFNeg(whenTrue, out LlvmValue negative) || !ReferenceEquals(negative, value)) return null;
                LlvmFastMathFlags propagate = compare.FastMathFlags & (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs);
                LlvmFastMathFlags added = propagate & ~select.FastMathFlags;
                if (added != 0) { select.FastMathFlags |= added; changed = true; }
                if ((select.FastMathFlags & LlvmFastMathFlags.NoNaNs) == 0 &&
                    (((ILlvmFastMathInstruction)whenTrue).FastMathFlags & LlvmFastMathFlags.NoNaNs) != 0 &&
                    (swap != 0 ? SelectIsOrdered(predicate) : SelectIsUnordered(predicate)))
                { select.FastMathFlags |= LlvmFastMathFlags.NoNaNs; changed = true; }
                if ((select.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) == 0 && (!select.HasOneUse || !LlvmValueTracking.CanIgnoreSignBitOfZero(select.FirstUse!, Metrics))) return null;
                if ((select.FastMathFlags & LlvmFastMathFlags.NoNaNs) == 0 && (!select.HasOneUse || !LlvmValueTracking.CanIgnoreSignBitOfNaN(select.FirstUse!, Metrics))) return null;
                if (swap != 0) predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
                if (predicate is LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle)
                    return ReplaceInstUsesWith(select, SelectFAbs(value, select.FastMathFlags));
                if (predicate is LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUge)
                    return new LlvmUnaryOperator(SelectFAbs(value, select.FastMathFlags)) { FastMathFlags = select.FastMathFlags };
            }
            for (int swap = 0; swap != 2; ++swap)
            {
                LlvmValue whenTrue = swap == 0 ? select.TrueValue : select.FalseValue;
                LlvmValue value = swap == 0 ? select.FalseValue : select.TrueValue;
                if (condition is not LlvmICmpInstruction { Left: LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast, Right: LlvmConstantInt constant } compare ||
                    !ReferenceEquals(cast.Operand, value) || !LlvmValueTracking.IsSignBitCheck(compare.Predicate, constant.Value, out bool trueIfSigned)) continue;
                if (!SelectMatchFNeg(whenTrue, out LlvmValue negative) || !ReferenceEquals(negative, value)) return null;
                if ((swap != 0) == trueIfSigned && !condition.HasOneUse && !whenTrue.HasOneUse) return null;
                LlvmValue abs = SelectFAbs(value, select.FastMathFlags);
                if ((swap != 0) != trueIfSigned) return ReplaceInstUsesWith(select, abs);
                return new LlvmUnaryOperator(abs) { FastMathFlags = select.FastMathFlags };
            }
            return changed ? select : null;
        }
        private const LlvmFastMathFlags SelectRewriteFlags = LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal | LlvmFastMathFlags.AllowContract | LlvmFastMathFlags.ApproxFunc;
        private const LlvmFastMathFlags SelectValueFlags = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoSignedZeros;
        private LlvmInstruction? FoldSelectOfOrderedFAbsCmpOfNaNScrubbedValue(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmFCmpInstruction outerCompare || !outerCompare.HasOneUse || !SelectIsOrdered(outerCompare.Predicate) ||
                select.TrueValue is not LlvmSelectInstruction inner || inner.Condition is not LlvmFCmpInstruction { Predicate: LlvmCmpPredicate.FCmpOrd } innerCompare ||
                !innerCompare.HasOneUse || !SelectIsAnyZeroFP(innerCompare.Right) || !ReferenceEquals(inner.TrueValue, innerCompare.Left) ||
                !ReferenceEquals(inner.FalseValue, select.FalseValue)) return null;
            LlvmValue x = innerCompare.Left, y = select.FalseValue, other = outerCompare.Right;
            LlvmCmpPredicate predicate = outerCompare.Predicate;
            bool MatchAbs(LlvmValue value, out LlvmCallInstruction call)
            {
                call = value as LlvmCallInstruction ?? null!;
                return call is not null && call.HasOneUse && call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.FAbs && ReferenceEquals(call.GetArgument(0), inner);
            }
            if (!MatchAbs(outerCompare.Left, out LlvmCallInstruction abs))
            {
                if (!MatchAbs(outerCompare.Right, out abs)) return null;
                other = outerCompare.Left; predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            }
            LlvmFastMathFlags commonRewrite = abs.FastMathFlags & outerCompare.FastMathFlags & SelectRewriteFlags;
            LlvmFastMathFlags absFlags = (commonRewrite | (abs.FastMathFlags & SelectValueFlags)) & ~LlvmFastMathFlags.NoNaNs;
            LlvmFastMathFlags cmpFlags = (commonRewrite | (outerCompare.FastMathFlags & SelectValueFlags)) & ~LlvmFastMathFlags.NoNaNs;
            LlvmValue newAbs = SelectFAbs(x, absFlags);
            LlvmValue newCompare = Builder.CreateFCmp(predicate, newAbs, other, fastMathFlags: cmpFlags);
            LlvmValue newSelect = Builder.CreateSelect(newCompare, x, y, fastMathFlags: select.FastMathFlags);
            return ReplaceInstUsesWith(select, newSelect);
        }
    }
}
