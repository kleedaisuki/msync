// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// matchEquivZeroRHS, simplifyICmpWithZero, simplifyICmpWithConstant, simplifyICmpInst casts.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyIntegerCompareZero(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        if (rhs is not LlvmConstantInt c) return null;
        if (!c.IsZero)
        {
            p = c.IsMinusOne ? p switch
            {
                LlvmCmpPredicate.ICmpSgt => LlvmCmpPredicate.ICmpSge,
                LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSlt,
                _ => (LlvmCmpPredicate)255
            } : c.IsOne ? p switch
            {
                LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSgt,
                LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUgt,
                LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSle,
                LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUle,
                _ => (LlvmCmpPredicate)255
            } : (LlvmCmpPredicate)255;
            if ((byte)p == 255) return null;
        }
        if (p == LlvmCmpPredicate.ICmpUlt) return CompareBool(lhs, false);
        if (p == LlvmCmpPredicate.ICmpUge) return CompareBool(lhs, true);
        if (p is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUgt)
            return LlvmKnownBitsAnalysis.IsKnownNonZero(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext)
                ? CompareBool(lhs, p is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUgt) : null;
        var known = LlvmKnownBitsAnalysis.Compute(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
        return p switch
        {
            LlvmCmpPredicate.ICmpSlt when known.IsNegative => CompareBool(lhs, true),
            LlvmCmpPredicate.ICmpSlt when known.IsNonNegative => CompareBool(lhs, false),
            LlvmCmpPredicate.ICmpSge when known.IsNegative => CompareBool(lhs, false),
            LlvmCmpPredicate.ICmpSge when known.IsNonNegative => CompareBool(lhs, true),
            LlvmCmpPredicate.ICmpSle when known.IsNegative => CompareBool(lhs, true),
            LlvmCmpPredicate.ICmpSgt when known.IsNegative => CompareBool(lhs, false),
            LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSgt when known.IsNonNegative &&
                LlvmKnownBitsAnalysis.IsKnownNonZero(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext) =>
                CompareBool(lhs, p == LlvmCmpPredicate.ICmpSgt),
            _ => null
        };
    }

    private static LlvmValue? SimplifyIntegerCompareConstant(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        if (rhs is not LlvmConstantInt c) return null;
        if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, Operand: LlvmCastInstruction { Opcode: LlvmOpcode.UIToFP } })
        {
            bool? sign = p switch
            {
                LlvmCmpPredicate.ICmpSlt when c.IsZero => true,
                LlvmCmpPredicate.ICmpSle when c.IsMinusOne => true,
                LlvmCmpPredicate.ICmpSgt when c.IsMinusOne => false,
                LlvmCmpPredicate.ICmpSge when c.IsZero => false,
                LlvmCmpPredicate.ICmpUgt when c.Value.IsMaxSignedValue => true,
                LlvmCmpPredicate.ICmpUge when c.Value.IsMinSignedValue => true,
                LlvmCmpPredicate.ICmpUlt when c.Value.IsMinSignedValue => false,
                LlvmCmpPredicate.ICmpUle when c.Value.IsMaxSignedValue => false,
                _ => null
            };
            if (sign.HasValue) return CompareBool(lhs, !sign.Value);
        }
        var region = LlvmConstantRange.MakeExactICmpRegion(p, c.Value);
        if (region.IsEmptySet) return CompareBool(lhs, false);
        if (region.IsFullSet) return CompareBool(lhs, true);
        var range = LlvmConstantRangeAnalysis.Compute(lhs, IsIntegerSignedPredicate(p), context: q.Context,
            tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
        if (!range.IsFullSet)
        {
            if (region.Contains(range)) return CompareBool(lhs, true);
            if (region.Inverse().Contains(range)) return CompareBool(lhs, false);
        }
        if (q.UseInstructionInfo && IsIntegerEquality(p) && lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt mul } b && !mul.IsZero &&
            (b.HasNoUnsignedWrap && !c.Value.URem(mul.Value).IsZero || b.HasNoSignedWrap && !c.Value.SRem(mul.Value).IsZero))
            return CompareBool(lhs, p == LlvmCmpPredicate.ICmpNe);
        if (p == LlvmCmpPredicate.ICmpUge && c.IsOne &&
            LlvmKnownBitsAnalysis.IsKnownNonZero(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext)) return CompareBool(lhs, true);
        return null;
    }

    private static LlvmValue? SimplifyIntegerCompareCasts(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (lhs is not LlvmCastInstruction cast || cast.Operand.Type is not LlvmIntegerType source ||
            cast.Type is not LlvmIntegerType || cast.Opcode is not (LlvmOpcode.ZExt or LlvmOpcode.SExt)) return null;
        LlvmValue src = cast.Operand;
        if (rhs is LlvmCastInstruction r)
        {
            if (r.Opcode == cast.Opcode && src.Type == r.Operand.Type && depth != 0)
                return SimplifyIntegerCompare(cast.Opcode == LlvmOpcode.ZExt ? IntegerUnsignedPredicate(p) : p, src, r.Operand, q, depth - 1);
            if (r.Operand == src && r.Opcode is LlvmOpcode.SExt or LlvmOpcode.ZExt)
            {
                if (cast.Opcode == LlvmOpcode.ZExt)
                {
                    if (p is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge) return CompareBool(lhs, true);
                    if (p is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSlt) return CompareBool(lhs, false);
                }
                else
                {
                    if (p is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle) return CompareBool(lhs, true);
                    if (p is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSgt) return CompareBool(lhs, false);
                }
            }
            return null;
        }
        if (rhs is not LlvmConstantInt c) return null;
        var trunc = c.Value.Trunc(source.BitWidth);
        var ext = cast.Opcode == LlvmOpcode.ZExt ? trunc.ZExt(c.Value.BitWidth) : trunc.SExt(c.Value.BitWidth);
        if (ext == c.Value)
            return depth != 0 ? SimplifyIntegerCompare(cast.Opcode == LlvmOpcode.ZExt ? IntegerUnsignedPredicate(p) : p,
                src, LlvmConstantInt.Get(source.Context, trunc), q, depth - 1) : null;
        if (IsIntegerEquality(p)) return CompareBool(lhs, p == LlvmCmpPredicate.ICmpNe);
        if (cast.Opcode == LlvmOpcode.ZExt)
            return CompareBool(lhs, p switch
            {
                LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle => true,
                LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge => false,
                LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge => c.Value.IsNegative,
                _ => !c.Value.IsNegative
            });
        if (IsIntegerSignedPredicate(p))
            return CompareBool(lhs, p is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge ? c.Value.IsNegative : !c.Value.IsNegative);
        if (depth == 0) return null;
        return SimplifyIntegerCompare(p is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge,
            src, IntegerZero(src), q, depth - 1);
    }
}
