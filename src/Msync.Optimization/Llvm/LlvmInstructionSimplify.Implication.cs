// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// isImpliedCondition, isImpliedCondICmps, isImpliedCondAndOr,
// isImpliedByDomCondition; IR/Instructions.cpp isImpliedByMatchingCmp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal static bool? IsImpliedByDomCondition(LlvmValue condition, BinaryQuery q)
    {
        if (q.Context?.Parent is not { } block) return null;
        using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
        if (!predecessors.MoveNext()) return null;
        var predecessor = predecessors.Current;
        if (predecessors.MoveNext() || predecessor.GetTerminatorOrNull() is not LlvmCondBrInstruction branch ||
            branch.GetSuccessor(0) == branch.GetSuccessor(1)) return null;
        Count(q.Metrics, "value_tracking.dom_condition.boolean_branch");
        return IsImpliedCondition(branch.Condition, condition, q, branch.GetSuccessor(0) == block);
    }
    internal static bool? IsImpliedByDomCondition(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        Count(q.Metrics, "value_tracking.dom_condition.queries");
        if (q.Context?.Parent is not { } block) return null;
        using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
        if (!predecessors.MoveNext()) return null;
        var predecessor = predecessors.Current;
        if (predecessors.MoveNext() || predecessor.GetTerminatorOrNull() is not LlvmCondBrInstruction branch ||
            branch.GetSuccessor(0) == branch.GetSuccessor(1)) return null;
        Count(q.Metrics, "value_tracking.dom_condition.branch");
        return IsImpliedCondition(branch.Condition, p, lhs, rhs, q, branch.GetSuccessor(0) == block);
    }

    internal static bool? IsImpliedCondition(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, bool lhsIsTrue = true, uint depth = 0)
    {
        Count(q.Metrics, "value_tracking.implication.queries");
        if (lhs == rhs) return lhsIsTrue;
        bool invert = false;
        if (ExtractIntegerNot(rhs) is { } not)
        {
            rhs = not;
            if (lhs == rhs) return !lhsIsTrue;
            invert = true;
        }
        if (rhs is LlvmICmpInstruction cmp)
        {
            bool? result = IsImpliedCondition(lhs, cmp.Predicate, cmp.Left, cmp.Right, q, lhsIsTrue, depth);
            return invert && result.HasValue ? !result.Value : result;
        }
        if (rhs is LlvmFCmpInstruction fp)
        {
            bool? result = IsImpliedCondition(lhs, fp.Predicate, fp.Left, fp.Right, q, lhsIsTrue, depth);
            return invert && result.HasValue ? !result.Value : result;
        }
        if (depth == 6) { Count(q.Metrics, "value_tracking.implication.depth_limit"); return null; }
        if (MatchIntegerLogical(rhs, false, out var a, out var b) &&
            (IsImpliedCondition(lhs, a, q, lhsIsTrue, depth + 1) == true ||
             IsImpliedCondition(lhs, b, q, lhsIsTrue, depth + 1) == true)) return !invert;
        if (MatchIntegerLogical(rhs, true, out a, out b) &&
            (IsImpliedCondition(lhs, a, q, lhsIsTrue, depth + 1) == false ||
             IsImpliedCondition(lhs, b, q, lhsIsTrue, depth + 1) == false)) return invert;
        return null;
    }

    internal static bool? IsImpliedCondition(LlvmValue lhs, LlvmCmpPredicate rp, LlvmValue r0, LlvmValue r1,
        BinaryQuery q, bool lhsIsTrue = true, uint depth = 0)
    {
        Count(q.Metrics, "value_tracking.implication.compare_queries");
        if (depth == 6) { Count(q.Metrics, "value_tracking.implication.depth_limit"); return null; }
        if (ExtractIntegerNot(lhs) is { } not) { lhs = not; lhsIsTrue = !lhsIsTrue; }
        if (lhs is LlvmICmpInstruction lc && (byte)rp >= 32)
            return ImpliedIntegerCompares(lc.Predicate, lc.Left, lc.Right, rp, r0, r1, q, lhsIsTrue);
        if (lhs is LlvmFCmpInstruction fc && (byte)rp <= 15)
            return ImpliedFloatingCompares(fc.Predicate, fc.Left, fc.Right, rp, r0, r1, lhsIsTrue);
        if (MatchIntegerLogical(lhs, lhsIsTrue, out var a, out var b))
            return IsImpliedCondition(a, rp, r0, r1, q, lhsIsTrue, depth + 1) ??
                IsImpliedCondition(b, rp, r0, r1, q, lhsIsTrue, depth + 1);
        return null;
    }

    private static bool MatchIntegerLogical(LlvmValue value, bool and, out LlvmValue a, out LlvmValue b)
    {
        if (!value.Type.IsIntegerTy(1)) { a = b = null!; return false; }
        if (value is LlvmBinaryOperator bin && bin.Opcode == (and ? LlvmOpcode.And : LlvmOpcode.Or))
        { a = bin.Left; b = bin.Right; return true; }
        if (value is LlvmSelectInstruction select && (and ? IsIntegerZero(select.FalseValue) : IsIntegerOne(select.TrueValue)))
        { a = select.Condition; b = and ? select.TrueValue : select.FalseValue; return true; }
        a = b = null!;
        return false;
    }

    private static bool? ImpliedIntegerCompares(LlvmCmpPredicate lp, LlvmValue l0, LlvmValue l1,
        LlvmCmpPredicate rp, LlvmValue r0, LlvmValue r1, BinaryQuery q, bool lhsIsTrue)
    {
        if (!lhsIsTrue) lp = LlvmCmpInstruction.GetInversePredicate(lp);
        if (l0 == r1) { (r0, r1) = (r1, r0); rp = LlvmCmpInstruction.GetSwappedPredicate(rp); }
        if (r0 == l1) { (l0, l1) = (l1, l0); lp = LlvmCmpInstruction.GetSwappedPredicate(lp); }
        if (l1 == r1 && (l0 != r0 || l0 is LlvmConstantInt))
        {
            (l0, l1) = (l1, l0); lp = LlvmCmpInstruction.GetSwappedPredicate(lp);
            (r0, r1) = (r1, r0); rp = LlvmCmpInstruction.GetSwappedPredicate(rp);
        }
        if (l0 == r0 && l1.Type is LlvmIntegerType && (l1 is LlvmConstantInt || r1 is LlvmConstantInt))
        {
            var lr = LlvmConstantRangeAnalysis.Compute(l1, IsIntegerSignedPredicate(lp), 5, metrics: q.Metrics);
            var rr = LlvmConstantRangeAnalysis.Compute(r1, IsIntegerSignedPredicate(rp), 5, metrics: q.Metrics);
            var region = LlvmConstantRange.MakeAllowedICmpRegion(lp, lr);
            if (region.ICmp(rp, rr)) return true;
            if (region.ICmp(LlvmCmpInstruction.GetInversePredicate(rp), rr)) return false;
            if (l1 is LlvmConstantInt && r1 is LlvmConstantInt) return null;
        }
        if (l0 == r0 && l1 == r1) return ImpliedMatchingIntegerCompare(lp, rp);
        if (r0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } sub &&
            sub.Left == l0 && sub.Right == l1 && r1 is LlvmConstantInt c)
        {
            if (lp is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge &&
                (c.Value.IsNegative || c.IsZero) && ImpliedMatchingIntegerCompare(lp, rp) == false) return false;
            if (lp is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle &&
                !c.Value.IsNegative && ImpliedMatchingIntegerCompare(lp, rp) == true) return true;
        }
        if (IsIntegerEquality(rp) && l0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } diff && l1 is LlvmConstantInt limit &&
            !LlvmConstantFolding.CompareInteger(LlvmAPInt.GetZero(limit.Value.BitWidth), limit.Value, lp) &&
            (diff.Left == r0 && diff.Right == r1 || diff.Left == r1 && diff.Right == r0))
            return rp == LlvmCmpPredicate.ICmpNe;
        if (l0 == r0 && lp is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge &&
            rp is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge &&
            l0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } sum &&
            (sum.Left == l1 && sum.Right == r1 || sum.Left == r1 && sum.Right == l1)) return lp == rp;
        if (lp == rp && ImpliedIntegerCompareOperands(lp, l0, l1, r0, r1)) return true;
        if (l1 is LlvmConstantInt lc && r1 is LlvmConstantInt rc &&
            r0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } mask && mask.Left == l0 && mask.Right is LlvmConstantInt mc)
        {
            var range = LlvmConstantRange.MakeExactICmpRegion(lp, lc.Value).BinaryAnd(new LlvmConstantRange(mc.Value));
            if (range.ICmp(rp, new(rc.Value))) return true;
            if (range.ICmp(LlvmCmpInstruction.GetInversePredicate(rp), new(rc.Value))) return false;
        }
        return null;
    }

    private static bool? ImpliedMatchingIntegerCompare(LlvmCmpPredicate lp, LlvmCmpPredicate rp)
    {
        if (ImpliedMatchingIntegerTrue(lp, rp)) return true;
        if (ImpliedMatchingIntegerTrue(lp, LlvmCmpInstruction.GetInversePredicate(rp))) return false;
        return null;
    }

    private static bool ImpliedMatchingIntegerTrue(LlvmCmpPredicate lp, LlvmCmpPredicate rp) => lp == rp || (lp switch
    {
        LlvmCmpPredicate.ICmpEq => rp is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle,
        LlvmCmpPredicate.ICmpUgt => rp is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUge,
        LlvmCmpPredicate.ICmpUlt => rp is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUle,
        LlvmCmpPredicate.ICmpSgt => rp is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSge,
        LlvmCmpPredicate.ICmpSlt => rp is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSle,
        _ => false
    });
}
