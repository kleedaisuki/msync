// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// canonicalizeSPF, foldSPFofSPF, canoncalizeSelectICmpMinMax/foldSelectICmpMinMax.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmIntrinsicKind SelectMinMaxIntrinsic(LlvmSelectPatternFlavor flavor) => flavor switch
        {
            LlvmSelectPatternFlavor.SMin => LlvmIntrinsicKind.SMin, LlvmSelectPatternFlavor.SMax => LlvmIntrinsicKind.SMax,
            LlvmSelectPatternFlavor.UMin => LlvmIntrinsicKind.UMin, LlvmSelectPatternFlavor.UMax => LlvmIntrinsicKind.UMax,
            _ => throw new System.Diagnostics.UnreachableException()
        };
        private void DropPoisonGeneratingAnnotationsForSPFCanonicalization(LlvmValue condition, LlvmValue left, LlvmValue right)
        {
            if (!LlvmValueTracking.ImpliesPoison(left, condition, Metrics) && left is LlvmInstruction leftInstruction)
                leftInstruction.DropPoisonGeneratingAnnotations();
            if (!LlvmValueTracking.ImpliesPoison(right, condition, Metrics) && right is LlvmInstruction rightInstruction)
                rightInstruction.DropPoisonGeneratingAnnotations();
        }
        private LlvmValue? CanonicalizeSPF(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (!whenTrue.Type.IsIntegerTy()) return null;
            LlvmSelectPatternResult pattern = LlvmSelectPattern.MatchDecomposed(compare, whenTrue, whenFalse, metrics: Metrics);
            LlvmValue left = pattern.LHS!, right = pattern.RHS!;
            if (pattern.Flavor is LlvmSelectPatternFlavor.Abs or LlvmSelectPatternFlavor.NAbs)
            {
                if (!compare.HasOneUse && !right.HasOneUse) return null;
                bool intMinIsPoison = pattern.Flavor == LlvmSelectPatternFlavor.Abs &&
                    right is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } negative &&
                    SelectIsZero(negative.Left) && ReferenceEquals(negative.Right, left);
                LlvmValue flag = LlvmConstantInt.Get(LlvmType.GetInt1Ty(compare.Context), intMinIsPoison ? 1UL : 0UL);
                LlvmValue abs = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.Abs, left, flag);
                Count("select.spf.abs_rewrites");
                return pattern.Flavor == LlvmSelectPatternFlavor.NAbs ? Builder.CreateNeg(abs) : abs;
            }
            if (!pattern.IsMinOrMax) return null;
            DropPoisonGeneratingAnnotationsForSPFCanonicalization(compare, left, right);
            Count("select.spf.minmax_rewrites");
            return Builder.CreateBinaryIntrinsic(SelectMinMaxIntrinsic(pattern.Flavor), left, right);
        }
        private LlvmInstruction? FoldSPFofSPF(LlvmInstruction inner, LlvmSelectPatternFlavor innerFlavor,
            LlvmValue left, LlvmValue right, LlvmInstruction outer, LlvmSelectPatternFlavor outerFlavor, LlvmValue other)
        {
            if (!ReferenceEquals(outer.Type, inner.Type)) return null;
            if ((ReferenceEquals(other, left) || ReferenceEquals(other, right)) && innerFlavor == outerFlavor &&
                innerFlavor is LlvmSelectPatternFlavor.SMin or LlvmSelectPatternFlavor.SMax or LlvmSelectPatternFlavor.UMin or LlvmSelectPatternFlavor.UMax)
                return ReplaceInstUsesWith(outer, inner);
            return null;
        }
        private LlvmValue? CanoncalizeSelectICmpMinMax(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse,
            LlvmInstructionSimplify.BinaryQuery query)
        {
            LlvmValue left = compare.Left, right = compare.Right;
            LlvmCmpPredicate predicate = compare.Predicate;
            if (SelectIsZero(whenFalse)) { (whenTrue, whenFalse) = (whenFalse, whenTrue); predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
            if (!SelectIsZero(whenTrue)) return null;
            if (predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge)
            { (left, right) = (right, left); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (predicate is not (LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle) ||
                whenFalse is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } sub ||
                !ReferenceEquals(sub.Left, left) || !ReferenceEquals(sub.Right, right) ||
                !LlvmValueTracking.IsGuaranteedNotToBeUndef(left, query.Tree, query.Context, query.Metrics, assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals)) return null;
            LlvmValue minimum = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMin, right, left);
            return Builder.CreateSub(left, minimum, nsw: true);
        }
        private LlvmValue? FoldSelectICmpMinMax(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse,
            LlvmInstructionSimplify.BinaryQuery query)
        {
            if (CanoncalizeSelectICmpMinMax(compare, whenTrue, whenFalse, query) is { } canonical) return canonical;
            LlvmValue left = compare.Left, right = compare.Right;
            LlvmCmpPredicate predicate = compare.Predicate;
            if (ReferenceEquals(right, whenTrue)) { (left, right) = (right, left); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (!ReferenceEquals(left, whenTrue) || whenFalse is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add || !ReferenceEquals(add.Left, right)) return null;
            LlvmIntrinsicKind? intrinsic = null;
            if (predicate == LlvmCmpPredicate.ICmpSgt && add.HasNoSignedWrap && SelectIsOne(add.Right))
            { add.HasNoUnsignedWrap = false; intrinsic = LlvmIntrinsicKind.SMax; }
            else if (predicate == LlvmCmpPredicate.ICmpSlt && add.HasNoSignedWrap && SelectIsOnes(add.Right))
            { add.HasNoUnsignedWrap = false; intrinsic = LlvmIntrinsicKind.SMin; }
            else if (predicate == LlvmCmpPredicate.ICmpUgt && add.HasNoUnsignedWrap && SelectIsOne(add.Right))
            { add.HasNoSignedWrap = false; intrinsic = LlvmIntrinsicKind.UMax; }
            else if (predicate == LlvmCmpPredicate.ICmpUlt && SelectIsOnes(add.Right) &&
                LlvmKnownBitsAnalysis.IsKnownNonZero(right, context: query.Context, tree: query.Tree, metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC))
            { add.HasNoSignedWrap = false; add.HasNoUnsignedWrap = false; intrinsic = LlvmIntrinsicKind.UMin; }
            return intrinsic.HasValue ? Builder.CreateBinaryIntrinsic(intrinsic.Value, whenTrue, whenFalse) : null;
        }
    }
}
