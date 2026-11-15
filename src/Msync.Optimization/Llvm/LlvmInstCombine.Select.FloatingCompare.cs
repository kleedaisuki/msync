// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// visitSelectInst scalar floating compare and minnum/maxnum canonicalization.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectFloatingComparisons(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmFCmpInstruction compare) return null;
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue left = compare.Left, right = compare.Right, whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            if (((ReferenceEquals(left, whenTrue) && ReferenceEquals(right, whenFalse)) ||
                (ReferenceEquals(left, whenFalse) && ReferenceEquals(right, whenTrue))) && compare.HasOneUse && SelectIsUnordered(predicate))
            {
                LlvmValue newCondition = Builder.CreateFCmp(LlvmCmpInstruction.GetInversePredicate(predicate), left, right, compare.Name + ".inv", compare.FastMathFlags);
                LlvmFastMathFlags flags = select.FastMathFlags | (compare.FastMathFlags & (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs));
                return ReplaceInstUsesWith(select, Builder.CreateSelect(newCondition, whenFalse, whenTrue, fastMathFlags: flags));
            }
            if (!select.Type.IsFloatingPointTy()) return null;
            LlvmValue? matchLeft = null, matchRight = null;
            if (predicate is LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpUeq) { matchLeft = whenFalse; matchRight = whenTrue; }
            else if (predicate is LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUne) { matchLeft = whenTrue; matchRight = whenFalse; }
            if (ReferenceEquals(left, matchLeft) && MatchFMulByZeroIfResultEqZero(left, right, matchRight!, matchLeft!, select,
                (select.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0)) return ReplaceInstUsesWith(select, left);
            if (predicate is not (LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno) || !SelectIsPositiveZeroFP(right)) return null;
            matchLeft = predicate == LlvmCmpPredicate.FCmpOrd ? whenTrue : whenFalse;
            matchRight = predicate == LlvmCmpPredicate.FCmpOrd ? whenFalse : whenTrue;
            bool reciprocalIfNaN = matchRight is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, Left: LlvmConstantFP one } divide &&
                one.Value.BitwiseIsEqual(LlvmAPFloat.GetOne(one.Value.Semantics)) && ReferenceEquals(divide.Right, left);
            bool canonicalizeIfNotNaN = matchLeft is LlvmCallInstruction canonical && canonical.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Canonicalize &&
                ReferenceEquals(canonical.GetArgument(0), left);
            if (!reciprocalIfNaN && !canonicalizeIfNotNaN) return null;
            LlvmDenormalModeInfo mode = F.GetDenormalMode(select.Type);
            if (reciprocalIfNaN && mode == LlvmDenormalModeInfo.IEEE)
            {
                if (canonicalizeIfNotNaN) return ReplaceInstUsesWith(select, left);
                int operandIndex = predicate == LlvmCmpPredicate.FCmpOrd ? 2 : 1;
                if (LlvmValueTracking.IsGuaranteedNotToBeUndef(left, DT, select, Metrics, assumptions: AC)) return ReplaceOperand(select, operandIndex, left);
                LlvmFreezeInstruction frozen = new(left); frozen.SetName(left.Name + ".fr");
                InsertNewInstBefore(frozen, compare); ReplaceOperand(compare, 0, frozen);
                return ReplaceOperand(select, operandIndex, frozen);
            }
            if (canonicalizeIfNotNaN)
            {
                if (mode == LlvmDenormalModeInfo.IEEE) return ReplaceOperand(select, predicate == LlvmCmpPredicate.FCmpOrd ? 1 : 2, left);
                if (reciprocalIfNaN && (mode.InputsAreZero || mode.OutputsAreZero)) return ReplaceInstUsesWith(select, matchLeft);
            }
            return null;
        }
        private LlvmInstruction? FoldSelectFloatingMinMax(LlvmSelectInstruction select)
        {
            if (!select.Type.IsFloatingPointTy() || select.Condition is not LlvmFCmpInstruction compare ||
                (compare.FastMathFlags & LlvmFastMathFlags.NoNaNs) == 0 ||
                ((select.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) == 0 && !IsSelectZeroSignInsignificant(select))) return null;
            LlvmIntrinsicKind kind; LlvmValue? left, right;
            if (LlvmSelectPattern.TryMatchOrdOrUnordFMax(select, out left, out right, Metrics)) kind = LlvmIntrinsicKind.MaxNum;
            else if (LlvmSelectPattern.TryMatchOrdOrUnordFMin(select, out left, out right, Metrics)) kind = LlvmIntrinsicKind.MinNum;
            else return null;
            LlvmValue result = Builder.CreateBinaryIntrinsic(kind, left!, right!);
            if (result is ILlvmFastMathInstruction floating)
            {
                LlvmFastMathFlags flags = select.FastMathFlags | LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.NoNaNs;
                floating.FastMathFlags = (compare.FastMathFlags & LlvmFastMathFlags.NoInfs) != 0 ? flags | LlvmFastMathFlags.NoInfs : flags & ~LlvmFastMathFlags.NoInfs;
            }
            return ReplaceInstUsesWith(select, result);
        }
    }
}
