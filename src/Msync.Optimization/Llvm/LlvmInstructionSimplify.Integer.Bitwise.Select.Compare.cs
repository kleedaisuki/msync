// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifySelectBitTest, simplifySelectWithEquivalence,
// simplifySelectWithICmpCond and simplifySelectWithFCmp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifySelectBitTest(LlvmValue whenTrue, LlvmValue whenFalse, LlvmValue x,
        LlvmAPInt mask, bool trueWhenUnset)
    {
        if (whenFalse == x && whenTrue is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt t } ta &&
            ta.Left == x && mask == ~t.Value) return trueWhenUnset ? whenFalse : whenTrue;
        if (whenTrue == x && whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt f } fa &&
            fa.Left == x && mask == ~f.Value) return trueWhenUnset ? whenFalse : whenTrue;
        if (!mask.IsPowerOf2) return null;
        if (whenFalse == x && whenTrue is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt ot } tor &&
            tor.Left == x && mask == ot.Value)
        {
            if (trueWhenUnset && tor.IsDisjoint) return null;
            return trueWhenUnset ? whenTrue : whenFalse;
        }
        if (whenTrue == x && whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt of } fOr &&
            fOr.Left == x && mask == of.Value)
        {
            if (!trueWhenUnset && fOr.IsDisjoint) return null;
            return trueWhenUnset ? whenTrue : whenFalse;
        }
        return null;
    }

    private static LlvmValue? SimplifySelectEquivalence(
        IReadOnlyList<(LlvmValue Operand, LlvmValue Replacement)> replacements,
        LlvmValue whenTrue, LlvmValue whenFalse, BinaryQuery q, uint depth)
    {
        var simplifiedFalse = BitwiseSimplifyWithOpsReplaced(whenFalse, replacements,
            q with { CanUseUndef = false }, false, depth) ?? whenFalse;
        var simplifiedTrue = BitwiseSimplifyWithOpsReplaced(whenTrue, replacements, q, true, depth) ?? whenTrue;
        return simplifiedFalse == simplifiedTrue ? whenFalse : null;
    }

    private static LlvmValue? SimplifySelectICmp(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse,
        BinaryQuery q, uint depth)
    {
        if (condition is not LlvmICmpInstruction compare) return null;
        LlvmCmpPredicate predicate = compare.Predicate;
        LlvmValue lhs = compare.Left, rhs = compare.Right;
        if (predicate == LlvmCmpPredicate.ICmpNe)
        { predicate = LlvmCmpPredicate.ICmpEq; (whenTrue, whenFalse) = (whenFalse, whenTrue); }
        if (whenTrue.Type.IsIntegerTy())
        {
            var pattern = LlvmSelectPattern.MatchDecomposed(compare, whenTrue, whenFalse, metrics: q.Metrics);
            if (pattern.IsMinOrMax && predicate == LlvmSelectPattern.GetMinMaxPred(pattern.Flavor))
            {
                var limit = LlvmSelectPattern.GetMinMaxLimit(LlvmSelectPattern.GetInverseMinMaxFlavor(pattern.Flavor),
                    ((LlvmIntegerType)pattern.LHS!.Type).BitWidth);
                if (pattern.RHS is LlvmConstantInt constant && constant.Value == limit) return pattern.LHS;
            }
        }
        if (predicate == LlvmCmpPredicate.ICmpEq && IsIntegerZero(rhs) &&
            lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask } and &&
            SimplifySelectBitTest(whenTrue, whenFalse, and.Left, mask.Value, true) is { } bitTest) return bitTest;
        // Funnel-shift/abs/minmax intrinsic patterns require intrinsic identity.
        if (predicate != LlvmCmpPredicate.ICmpEq) return null;
        if ((lhs.Type.IsIntegerTy() || LlvmCmpInstAnalysis.CanReplacePointersIfEqual(lhs, rhs, q.Layout, q.Metrics)) &&
            SimplifySelectEquivalence([(lhs, rhs)], whenTrue, whenFalse, q, depth) is { } forward) return forward;
        if ((lhs.Type.IsIntegerTy() || LlvmCmpInstAnalysis.CanReplacePointersIfEqual(rhs, lhs, q.Layout, q.Metrics)) &&
            SimplifySelectEquivalence([(rhs, lhs)], whenTrue, whenFalse, q, depth) is { } backward) return backward;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or && IsIntegerZero(rhs) &&
            SimplifySelectEquivalence([(or.Left, rhs), (or.Right, rhs)], whenTrue, whenFalse, q, depth) is { } zero) return zero;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And } all && IsIntegerAllOnes(rhs))
            return SimplifySelectEquivalence([(all.Left, rhs), (all.Right, rhs)], whenTrue, whenFalse, q, depth);
        return null;
    }

    private static LlvmValue? SimplifySelectFCmp(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse,
        LlvmFastMathFlags flags, BinaryQuery q, uint depth)
    {
        if (condition is not LlvmFCmpInstruction compare) return null;
        LlvmCmpPredicate predicate = compare.Predicate;
        LlvmValue lhs = compare.Left, rhs = compare.Right;
        bool equivalence = LlvmCmpInstAnalysis.IsEquivalence(compare, metrics: q.Metrics);
        if (LlvmCmpInstAnalysis.IsEquivalence(compare, true, q.Metrics))
        {
            (whenTrue, whenFalse) = (whenFalse, whenTrue);
            predicate = LlvmCmpInstruction.GetInversePredicate(predicate);
            equivalence = true;
        }
        if (equivalence)
        {
            if (SimplifySelectEquivalence([(lhs, rhs)], whenTrue, whenFalse, q, depth) is { } forward) return forward;
            if (SimplifySelectEquivalence([(rhs, lhs)], whenTrue, whenFalse, q, depth) is { } backward) return backward;
        }
        if (lhs == whenFalse && rhs == whenTrue) (lhs, rhs) = (rhs, lhs);
        if (lhs != whenTrue || rhs != whenFalse) return null;
        if (Has(flags, LlvmFastMathFlags.NoSignedZeros))
        {
            if (predicate == LlvmCmpPredicate.FCmpOeq) return whenFalse;
            if (predicate == LlvmCmpPredicate.FCmpUne) return whenTrue;
        }
        return null;
    }
}
