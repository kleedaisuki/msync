// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyFCmpInst, scalar represented slice.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    public static LlvmValue? SimplifyFCmp(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        LlvmFastMathFlags flags, LlvmDataLayout layout, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) =>
        SimplifyFloatingCompare(predicate, lhs, rhs, flags, new(layout, context, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals), 3);

    private static LlvmValue? SimplifyFloatingCompare(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        LlvmFastMathFlags flags, BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.fcmp.queries");
        if (lhs is LlvmConstant left)
        {
            if (rhs is LlvmConstant right)
            {
                if (LlvmConstantFolding.ConstantFoldCompareInstOperands(predicate, left, right, q.Layout,
                    q.Context?.Parent?.Parent, q.Metrics) is { } folded) return folded;
            }
            else { (lhs, rhs) = (rhs, lhs); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        }
        if (predicate == LlvmCmpPredicate.FCmpFalse) return LlvmConstantInt.GetFalse(lhs.Context);
        if (predicate == LlvmCmpPredicate.FCmpTrue) return LlvmConstantInt.GetTrue(lhs.Context);
        if (lhs is LlvmPoisonValue || rhs is LlvmPoisonValue) return LlvmPoisonValue.Get(LlvmIntegerType.Get(lhs.Context, 1));
        if (q.IsUndef(lhs) || q.IsUndef(rhs)) return LlvmConstantInt.GetBool(lhs.Context, BitwiseUnorderedFP(predicate));
        if (lhs == rhs)
        {
            if (predicate is LlvmCmpPredicate.FCmpUeq or LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpUle)
                return LlvmConstantInt.GetTrue(lhs.Context);
            if (predicate is LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOlt)
                return LlvmConstantInt.GetFalse(lhs.Context);
        }
        if (predicate is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno)
        {
            var rightClass = LlvmKnownFPClasses.Compute(rhs, LlvmFPClassTest.All, q.Context, q.Tree, q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            var leftClass = LlvmKnownFPClasses.Compute(lhs, LlvmFPClassTest.All, q.Context, q.Tree, q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            if (Has(flags, LlvmFastMathFlags.NoNaNs) || rightClass.IsKnownNeverNaN() && leftClass.IsKnownNeverNaN())
                return LlvmConstantInt.GetBool(lhs.Context, predicate == LlvmCmpPredicate.FCmpOrd);
            if (rightClass.IsKnownAlways(LlvmFPClassTest.NaN) || leftClass.IsKnownAlways(LlvmFPClassTest.NaN))
                return LlvmConstantInt.GetBool(lhs.Context, predicate == LlvmCmpPredicate.FCmpUno);
        }
        if (IsImpliedByDomCondition(predicate, lhs, rhs, q) is { } implied) return LlvmConstantInt.GetBool(lhs.Context, implied);
        LlvmKnownFPClass? fullKnownClass = null;
        if (rhs is LlvmConstantFP constant)
        {
            if (q.Context?.Parent?.Parent is { } contextFunction)
            {
                var (classValue, classes) = LlvmKnownFPClasses.FCmpToClassTest(predicate, contextFunction, lhs, rhs, metrics: q.Metrics);
                if (classValue is not null)
                {
                    fullKnownClass = ComputeLhs(LlvmFPClassTest.All);
                    var possible = fullKnownClass.Value.GetKnownFPClasses();
                    if ((possible & classes) == LlvmFPClassTest.None) return LlvmConstantInt.GetFalse(lhs.Context);
                    if ((possible & ~classes) == LlvmFPClassTest.None) return LlvmConstantInt.GetTrue(lhs.Context);
                }
            }
            if (constant.Value.IsNaN) return LlvmConstantInt.GetBool(lhs.Context, BitwiseUnorderedFP(predicate));
            if (constant.Value.IsNegative && !IsFPNegativeZero(constant))
            {
                if (predicate is LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUne &&
                    ComputeLhs(LlvmKnownFPClass.OrderedLessThanZeroMask).CannotBeOrderedLessThanZero())
                    return LlvmConstantInt.GetTrue(lhs.Context);
                if (predicate is LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpOlt &&
                    ComputeLhs(LlvmKnownFPClass.OrderedLessThanZeroMask).CannotBeOrderedLessThanZero())
                    return LlvmConstantInt.GetFalse(lhs.Context);
            }
        }
        if (IsFPZero(rhs))
        {
            if (predicate is LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUlt)
            {
                var interest = LlvmKnownFPClass.OrderedLessThanZeroMask;
                if (!Has(flags, LlvmFastMathFlags.NoNaNs)) interest |= LlvmFPClassTest.NaN;
                var known = ComputeLhs(interest);
                if ((Has(flags, LlvmFastMathFlags.NoNaNs) || known.IsKnownNeverNaN()) && known.CannotBeOrderedLessThanZero())
                    return LlvmConstantInt.GetBool(lhs.Context, predicate == LlvmCmpPredicate.FCmpOge);
            }
            if (predicate is LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpOlt &&
                ComputeLhs(LlvmKnownFPClass.OrderedLessThanZeroMask).CannotBeOrderedLessThanZero())
                return LlvmConstantInt.GetBool(lhs.Context, predicate == LlvmCmpPredicate.FCmpUge);
        }
        if ((lhs is LlvmSelectInstruction || rhs is LlvmSelectInstruction) && ScalarCmpSelect(predicate, lhs, rhs, q, depth) is { } select)
            return select;
        if ((lhs is LlvmPhiInstruction || rhs is LlvmPhiInstruction) && ScalarCmpPhi(predicate, lhs, rhs, q, depth) is { } phi)
            return phi;
        return null;

        LlvmKnownFPClass ComputeLhs(LlvmFPClassTest interest) => fullKnownClass ??
            LlvmKnownFPClasses.Compute(lhs, flags, interest, q.Context, q.Tree, q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
    }
}
