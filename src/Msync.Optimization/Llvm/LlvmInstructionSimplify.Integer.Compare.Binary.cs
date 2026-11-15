// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// simplifyICmpWithBinOp, simplifyICmpWithBinOpOnLHS, trySimplifyICmpWithAdds.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static bool IntegerCompareNoWrap(LlvmCmpPredicate p, LlvmBinaryOperator b, BinaryQuery q) =>
        IsIntegerEquality(p) || q.UseInstructionInfo && (IsIntegerSignedPredicate(p) ? b.HasNoSignedWrap : b.HasNoUnsignedWrap);

    private static LlvmValue? SimplifyIntegerCompareBinOps(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        var l = lhs as LlvmBinaryOperator;
        var r = rhs as LlvmBinaryOperator;
        if (depth != 0)
        {
            if (l is { Opcode: LlvmOpcode.Add } && IntegerCompareNoWrap(p, l, q) && (l.Left == rhs || l.Right == rhs) &&
                SimplifyIntegerCompare(p, l.Left == rhs ? l.Right : l.Left, IntegerZero(rhs), q, depth - 1) is { } added) return added;
            if (r is { Opcode: LlvmOpcode.Add } && IntegerCompareNoWrap(p, r, q) && (r.Left == lhs || r.Right == lhs) &&
                SimplifyIntegerCompare(p, IntegerZero(lhs), r.Left == lhs ? r.Right : r.Left, q, depth - 1) is { } addedRight) return addedRight;
            if (l is { Opcode: LlvmOpcode.Add } && r is { Opcode: LlvmOpcode.Add } &&
                (IntegerCompareNoWrap(p, l, q) && IntegerCompareNoWrap(p, r, q) || IntegerCompareAddsNoWrap(p, l, r, q)))
            {
                LlvmValue? a = null, b = null;
                if (l.Left == r.Left) { a = l.Right; b = r.Right; }
                else if (l.Left == r.Right) { a = l.Right; b = r.Left; }
                else if (l.Right == r.Left) { a = l.Left; b = r.Right; }
                else if (l.Right == r.Right) { a = l.Left; b = r.Left; }
                if (a is not null && SimplifyIntegerCompare(p, a, b!, q, depth - 1) is { } common) return common;
            }
        }
        if (l is not null && SimplifyIntegerCompareBinaryLeft(p, l, rhs, q) is { } left) return left;
        if (r is not null && SimplifyIntegerCompareBinaryLeft(LlvmCmpInstruction.GetSwappedPredicate(p), r, lhs, q) is { } right) return right;
        if ((IsIntegerSignedPredicate(p) || IsIntegerEquality(p)) &&
            l is { Opcode: LlvmOpcode.Sub, Right: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } } &&
            IsIntegerZero(l.Left) && rhs is LlvmConstantInt c && !c.Value.IsNegative)
        {
            if (!c.IsZero && p is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpNe) return CompareBool(lhs, true);
            if (!c.IsZero && p is LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpEq) return CompareBool(lhs, false);
            if (p == LlvmCmpPredicate.ICmpSle) return CompareBool(lhs, true);
            if (p == LlvmCmpPredicate.ICmpSgt) return CompareBool(lhs, false);
        }
        if (l is { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt power } && power.Value.IsPowerOf2 && rhs is LlvmConstantInt limit)
        {
            if (!limit.Value.IsPowerOf2 && (q.UseInstructionInfo && (l.HasNoSignedWrap || l.HasNoUnsignedWrap) || power.IsOne || !limit.IsZero) && IsIntegerEquality(p))
                return CompareBool(lhs, p == LlvmCmpPredicate.ICmpNe);
            if (limit.Value.IsMinSignedValue && p is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle)
                return CompareBool(lhs, p == LlvmCmpPredicate.ICmpUle);
        }
        if (depth == 0 || l is null || r is null || l.Opcode != r.Opcode) return null;
        if (l.Left == r.Left)
        {
            if (l.Opcode == LlvmOpcode.Shl && q.UseInstructionInfo && l.HasNoUnsignedWrap && r.HasNoUnsignedWrap &&
                (!IsIntegerSignedPredicate(p) || l.HasNoSignedWrap && r.HasNoSignedWrap) &&
                LlvmKnownBitsAnalysis.IsKnownNonZero(l.Left, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext))
                return SimplifyIntegerCompare(p, l.Right, r.Right, q, depth - 1);
            if (l.Opcode is LlvmOpcode.And or LlvmOpcode.Or && !IsIntegerEquality(p) &&
                l.Right is LlvmConstantInt lc && r.Right is LlvmConstantInt rc)
            {
                var a = lc.Value; var b = rc.Value;
                if (!(a & ~b).IsZero) { (a, b) = (b, a); p = LlvmCmpInstruction.GetSwappedPredicate(p); }
                if ((a & ~b).IsZero)
                {
                    if (p == LlvmCmpPredicate.ICmpUle) return CompareBool(lhs, true);
                    if (p == LlvmCmpPredicate.ICmpUgt) return CompareBool(lhs, false);
                    if (a.IsNegative == b.IsNegative && p is LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSgt)
                        return CompareBool(lhs, p == LlvmCmpPredicate.ICmpSle);
                }
            }
        }
        if (l.Right != r.Right || !q.UseInstructionInfo) return null;
        bool cancel = l.Opcode switch
        {
            LlvmOpcode.UDiv or LlvmOpcode.LShr => !IsIntegerSignedPredicate(p) && l.IsExact && r.IsExact,
            LlvmOpcode.SDiv => IsIntegerEquality(p) && l.IsExact && r.IsExact,
            LlvmOpcode.AShr => l.IsExact && r.IsExact,
            LlvmOpcode.Shl => (l.HasNoUnsignedWrap && r.HasNoUnsignedWrap || l.HasNoSignedWrap && r.HasNoSignedWrap) &&
                (!IsIntegerSignedPredicate(p) || l.HasNoSignedWrap && r.HasNoSignedWrap),
            _ => false
        };
        return cancel ? SimplifyIntegerCompare(p, l.Left, r.Left, q, depth - 1) : null;
    }

    private static bool IntegerCompareAddsNoWrap(LlvmCmpPredicate p, LlvmBinaryOperator l, LlvmBinaryOperator r, BinaryQuery q)
    {
        if (!IsIntegerSignedPredicate(p) || !q.UseInstructionInfo) return false;
        if (!r.HasNoSignedWrap) (l, r) = (r, l);
        if (!r.HasNoSignedWrap || l.Left != r.Left || l.Right is not LlvmConstantInt lc || r.Right is not LlvmConstantInt rc) return false;
        return lc.Value.Sle(rc.Value) && !lc.Value.IsNegative || rc.Value.Sle(lc.Value) && (lc.Value.IsNegative || lc.IsZero);
    }

    private static LlvmValue? SimplifyIntegerCompareBinaryLeft(LlvmCmpPredicate p, LlvmBinaryOperator bin, LlvmValue rhs, BinaryQuery q)
    {
        if (bin.Opcode == LlvmOpcode.Or && (bin.Left == rhs || bin.Right == rhs) && p is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge)
        {
            var rk = LlvmKnownBitsAnalysis.Compute(rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            var yk = LlvmKnownBitsAnalysis.Compute(bin.Left == rhs ? bin.Right : bin.Left, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            if (rk.IsNonNegative && yk.IsNegative) return CompareBool(rhs, p == LlvmCmpPredicate.ICmpSlt);
            if (rk.IsNegative || yk.IsNonNegative) return CompareBool(rhs, p == LlvmCmpPredicate.ICmpSge);
        }
        if (bin.Opcode == LlvmOpcode.URem && bin.Right == rhs)
        {
            if (IsIntegerSignedPredicate(p) && !LlvmKnownBitsAnalysis.Compute(rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext).IsNonNegative) return null;
            return CompareBool(rhs, p is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle);
        }
        if (bin.Left == rhs && bin.Right is LlvmConstantInt shift &&
            (bin.Opcode == LlvmOpcode.LShr && !shift.IsZero || bin.Opcode == LlvmOpcode.UDiv && !shift.IsOne) &&
            !IsIntegerSignedPredicate(p) && LlvmKnownBitsAnalysis.IsKnownNonZero(rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext))
            return CompareBool(rhs, p is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle);
        if (bin.Right is LlvmConstantInt denominator && bin.Left is LlvmBinaryOperator factor && factor.Left == rhs &&
            factor.Right is LlvmConstantInt numerator &&
            (bin.Opcode == LlvmOpcode.UDiv && factor.Opcode == LlvmOpcode.Mul && numerator.Value.Ule(denominator.Value) ||
             bin.Opcode == LlvmOpcode.LShr && factor.Opcode == LlvmOpcode.Mul && numerator.Value.Ule(IntegerComparePowerOfTwo(denominator.Value)) ||
             bin.Opcode == LlvmOpcode.UDiv && factor.Opcode == LlvmOpcode.Shl && IntegerComparePowerOfTwo(numerator.Value).Ule(denominator.Value)) &&
            p is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle)
            return CompareBool(rhs, p == LlvmCmpPredicate.ICmpUle);
        if (bin.Opcode == LlvmOpcode.Sub && bin.Right == rhs && bin.Left is LlvmConstantInt c &&
            !(c.Value & new LlvmAPInt(c.Value.BitWidth, 1)).IsZero && IsIntegerEquality(p)) return CompareBool(rhs, p == LlvmCmpPredicate.ICmpNe);
        return null;
    }

    private static LlvmAPInt IntegerComparePowerOfTwo(LlvmAPInt shift) => shift.GetLimitedValue() >= shift.BitWidth
        ? LlvmAPInt.GetZero(shift.BitWidth) : new LlvmAPInt(shift.BitWidth, 1).Shl((uint)shift.GetZExtValue());
}
