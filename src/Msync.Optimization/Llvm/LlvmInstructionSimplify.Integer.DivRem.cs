// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp,
// isDivZero, simplifyDivRem, simplifyDiv, simplifyRem, simplify[SU][DivRem]Inst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyIntegerDivRem(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        bool exact, BinaryQuery q, uint depth)
    {
        if (op == LlvmOpcode.SDiv && LlvmKnownBitsAnalysis.IsKnownNegation(lhs, rhs, true))
            return IntegerAllOnes(lhs);
        if (op == LlvmOpcode.SRem)
        {
            if (rhs is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } ext &&
                ext.Operand.Type is LlvmIntegerType { BitWidth: 1 }) return IntegerZero(lhs);
            if (LlvmKnownBitsAnalysis.IsKnownNegation(lhs, rhs)) return IntegerZero(lhs);
        }
        if (FoldOrCommuteConstant(op, ref lhs, ref rhs, q) is { } folded) return folded;
        if (SimplifyIntegerDivRemCommon(op, lhs, rhs, q, depth) is { } common) return common;
        bool div = op is LlvmOpcode.SDiv or LlvmOpcode.UDiv;
        if (div && exact && rhs is LlvmConstantInt divisor)
        {
            uint trailing = divisor.Value.CountTrailingZeros();
            if (trailing != 0)
            {
                var known = LlvmKnownBitsAnalysis.Compute(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
                if (known.One.CountTrailingZeros() < trailing) return LlvmPoisonValue.Get(lhs.Type);
            }
            if (!divisor.Value.IsPowerOf2 && lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } mul &&
                mul.Right == rhs && (op == LlvmOpcode.UDiv ? mul.HasNoSignedWrap : mul.HasNoUnsignedWrap))
                return mul.Left;
        }
        if (div || !q.UseInstructionInfo) return null;
        bool signed = op == LlvmOpcode.SRem;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shift && shift.Left == rhs &&
            (signed ? shift.HasNoSignedWrap : shift.HasNoUnsignedWrap)) return IntegerZero(lhs);
        if (rhs is LlvmConstantInt d && lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } product &&
            product.Right is LlvmConstantInt coefficient &&
            (signed ? product.HasNoSignedWrap : product.HasNoUnsignedWrap) &&
            (signed ? coefficient.Value.SRem(d.Value) : coefficient.Value.URem(d.Value)).IsZero)
            return IntegerZero(lhs);
        return null;
    }

    private static LlvmValue? SimplifyIntegerDivRemCommon(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        bool div = op is LlvmOpcode.SDiv or LlvmOpcode.UDiv;
        bool signed = op is LlvmOpcode.SDiv or LlvmOpcode.SRem;
        if (q.IsUndef(rhs) || rhs is LlvmPoisonValue || IsIntegerZero(rhs)) return LlvmPoisonValue.Get(lhs.Type);
        if (lhs is LlvmPoisonValue) return lhs;
        if (q.IsUndef(lhs) || IsIntegerZero(lhs)) return IntegerZero(lhs);
        if (lhs == rhs) return div ? IntegerOne(lhs) : IntegerZero(lhs);
        var known = LlvmKnownBitsAnalysis.Compute(rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
        if (known.Zero.IsAllOnes) return LlvmPoisonValue.Get(lhs.Type);
        if (known.Zero.CountLeadingOnes() == ((LlvmIntegerType)rhs.Type).BitWidth - 1)
            return div ? lhs : IntegerZero(lhs);
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } mul &&
            MatchCommutative(mul, LlvmOpcode.Mul, rhs) is { } other &&
            ((q.UseInstructionInfo && (signed ? mul.HasNoSignedWrap : mul.HasNoUnsignedWrap)) ||
             other is LlvmBinaryOperator quot && quot.Opcode == (signed ? LlvmOpcode.SDiv : LlvmOpcode.UDiv) && quot.Right == rhs))
            return div ? other : IntegerZero(lhs);
        if (IsIntegerDivZero(lhs, rhs, q, depth, signed)) return div ? IntegerZero(lhs) : lhs;
        return SimplifyIntegerByDomEq(op, lhs, rhs, q, depth) ?? ThreadIntegerBin(op, lhs, rhs, q, depth);
    }

    private static bool IsIntegerDivZero(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth, bool signed)
    {
        if (depth == 0) { Count(q.Metrics, "simplify.integer.divzero_recursion_limit"); return false; }
        --depth;
        if (signed)
        {
            if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.SRem } rem && rem.Right == rhs) return true;
            if (lhs is LlvmConstantInt dividend && !dividend.Value.IsMinSignedValue)
            {
                var abs = dividend.Value.IsNegative ? -dividend.Value : dividend.Value;
                if (IsIntegerCmpTrue(LlvmCmpPredicate.ICmpSlt, rhs, LlvmConstantInt.Get(lhs.Type.Context, -abs), q, depth) ||
                    IsIntegerCmpTrue(LlvmCmpPredicate.ICmpSgt, rhs, LlvmConstantInt.Get(lhs.Type.Context, abs), q, depth)) return true;
            }
            if (rhs is not LlvmConstantInt divisor) return false;
            if (divisor.Value.IsMinSignedValue) return IsIntegerCmpTrue(LlvmCmpPredicate.ICmpNe, lhs, rhs, q, depth);
            var divisorAbs = divisor.Value.IsNegative ? -divisor.Value : divisor.Value;
            return IsIntegerCmpTrue(LlvmCmpPredicate.ICmpSgt, lhs, LlvmConstantInt.Get(lhs.Type.Context, -divisorAbs), q, depth) &&
                IsIntegerCmpTrue(LlvmCmpPredicate.ICmpSlt, lhs, LlvmConstantInt.Get(lhs.Type.Context, divisorAbs), q, depth);
        }
        if (rhs is LlvmConstantInt c && (~LlvmKnownBitsAnalysis.Compute(lhs, context: q.Context, tree: q.Tree,
            metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext).Zero).Ult(c.Value)) return true;
        return IsIntegerCmpTrue(LlvmCmpPredicate.ICmpUlt, lhs, rhs, q, depth);
    }
}
