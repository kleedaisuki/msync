// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp::foldAbsDiff.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldAbsDiff(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            Count("select.absdiff.candidates");
            if (whenTrue is not LlvmInstruction ti || whenFalse is not LlvmInstruction fi) return null;
            var predicate = compare.Predicate switch
            {
                LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSgt,
                LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSlt,
                LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUgt,
                LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUlt,
                _ => compare.Predicate
            };
            var a = compare.Left; var b = compare.Right;
            if (SaturationSub(fi, a, b)) { (ti,fi) = (fi,ti); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (predicate == LlvmCmpPredicate.ICmpSgt && SaturationSub(ti, a, b) && SaturationSub(fi, b, a) &&
                ti is LlvmBinaryOperator tSub && fi is LlvmBinaryOperator fSub &&
                (tSub.HasNoSignedWrap || tSub.HasNoUnsignedWrap) && (fSub.HasNoSignedWrap || fSub.HasNoUnsignedWrap))
            {
                tSub.HasNoUnsignedWrap = false;
                if (!tSub.HasNoSignedWrap) tSub.HasNoSignedWrap = tSub.HasOneUse;
                Count("select.absdiff.nowrap_flags_rewritten");
                return SaturationIntrinsic(LlvmIntrinsicKind.Abs, ti, LlvmConstantInt.GetTrue(ti.Context), "absdiff_nowrap");
            }
            bool NswSub(LlvmInstruction value, LlvmValue left, LlvmValue right) =>
                value is LlvmBinaryOperator { HasNoSignedWrap: true } && SaturationSub(value, left, right);
            bool Negates(LlvmValue value, LlvmValue operand) => SelectMatchNeg(value, out var inner) && inner == operand;
            LlvmValue? result = null;
            if (predicate == LlvmCmpPredicate.ICmpSgt && NswSub(ti, a, b) && Negates(fi, ti)) result = ti;
            else if (predicate == LlvmCmpPredicate.ICmpSlt && NswSub(fi, a, b) && Negates(ti, fi)) result = fi;
            else if (predicate == LlvmCmpPredicate.ICmpSgt && NswSub(fi, b, a) && Negates(ti, fi)) result = fi;
            else if (predicate == LlvmCmpPredicate.ICmpSlt && NswSub(ti, b, a) && Negates(fi, ti)) result = ti;
            return result is null ? null : SaturationIntrinsic(LlvmIntrinsicKind.Abs, result, LlvmConstantInt.GetFalse(result.Context), "absdiff_nsw_negate");
        }
    }
}
