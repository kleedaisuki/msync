// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp isTruePredicate.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool IsTrueNonEqualPredicate(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
    {
        if (left == right && predicate is LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpUle) return true;
        if (predicate == LlvmCmpPredicate.ICmpSle)
        {
            if (right is LlvmBinaryOperator { Right: LlvmConstantInt c } sum && sum.Left == left &&
                (sum.Opcode == LlvmOpcode.Or || sum.Opcode == LlvmOpcode.Add && sum.HasNoSignedWrap))
                return !c.IsNegative;
            return MatchNonEqualAddLike(left, true, out var a, out var ac) &&
                MatchNonEqualAddLike(right, true, out var b, out var bc) && a == b && ac.Sle(bc);
        }
        if (predicate != LlvmCmpPredicate.ICmpUle) return false;
        if (right is LlvmBinaryOperator upper && (upper.Left == left || upper.Right == left) &&
            (upper.Opcode == LlvmOpcode.Or || upper.Opcode == LlvmOpcode.Add && upper.HasNoUnsignedWrap)) return true;
        if (left is LlvmBinaryOperator lower)
        {
            if (lower.Opcode == LlvmOpcode.LShr && lower.Left == right) return true;
            if (lower.Opcode == LlvmOpcode.UDiv && lower.Left == right &&
                lower.Right is LlvmConstantInt c && c.Value.Ugt(new LlvmAPInt(c.BitWidth, 1))) return true;
            if (lower.Opcode == LlvmOpcode.And && (lower.Left == right || lower.Right == right)) return true;
        }
        return MatchNonEqualAddLike(left, false, out var x, out var xc) &&
            MatchNonEqualAddLike(right, false, out var y, out var yc) && x == y && xc.Ule(yc);
    }

    private static bool MatchNonEqualAddLike(LlvmValue value, bool signed, out LlvmValue variable, out LlvmAPInt constant)
    {
        if (value is LlvmBinaryOperator { Right: LlvmConstantInt c } binary &&
            (binary.Opcode == LlvmOpcode.Or && binary.IsDisjoint ||
             binary.Opcode == LlvmOpcode.Add && (signed ? binary.HasNoSignedWrap : binary.HasNoUnsignedWrap)))
        { variable = binary.Left; constant = c.Value; return true; }
        variable = null!; constant = default; return false;
    }
}
