// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp:1516-1942 and compare predicate source helpers.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static bool BitwiseUnsignedPredicate(LlvmCmpPredicate p) =>
        p is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle;
    private static bool BitwiseEqualityPredicate(LlvmCmpPredicate p) => p is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;

    private static LlvmValue? SimplifyUnsignedRangeCheck(LlvmICmpInstruction zero, LlvmICmpInstruction unsigned,
        bool isAnd, BinaryQuery q)
    {
        if (!IsIntegerZero(zero.Right) || !BitwiseEqualityPredicate(zero.Predicate)) return null;
        LlvmValue y = zero.Left;
        LlvmCmpPredicate eq = zero.Predicate;
        if (y is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub)
        {
            if (BitwiseComparePair(unsigned, sub.Left, sub.Right, out var pair) && BitwiseUnsignedPredicate(pair))
            {
                if (pair is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUle && eq == LlvmCmpPredicate.ICmpNe && !isAnd)
                    return LlvmConstantInt.GetTrue(zero.Context);
                if (pair is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUgt && eq == LlvmCmpPredicate.ICmpEq && isAnd)
                    return LlvmConstantInt.GetFalse(zero.Context);
                if (eq == LlvmCmpPredicate.ICmpNe && pair is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUgt)
                    return isAnd ? unsigned : zero;
                if (eq == LlvmCmpPredicate.ICmpEq && pair is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUge)
                    return isAnd ? zero : unsigned;
            }
            if (BitwiseComparePair(unsigned, y, sub.Left, out var shifted))
            {
                if (shifted == LlvmCmpPredicate.ICmpUge && isAnd && eq == LlvmCmpPredicate.ICmpNe && BitwiseNonZero(sub.Right, q)) return unsigned;
                if (shifted == LlvmCmpPredicate.ICmpUlt && !isAnd && eq == LlvmCmpPredicate.ICmpEq && BitwiseNonZero(sub.Right, q)) return unsigned;
            }
        }
        LlvmValue x;
        LlvmCmpPredicate predicate;
        if (unsigned.Right == y && BitwiseUnsignedPredicate(unsigned.Predicate))
        { x = unsigned.Left; predicate = unsigned.Predicate; }
        else if (unsigned.Left == y && BitwiseUnsignedPredicate(unsigned.Predicate))
        { x = unsigned.Right; predicate = LlvmCmpInstruction.GetSwappedPredicate(unsigned.Predicate); }
        else return null;
        if (predicate == LlvmCmpPredicate.ICmpUgt && eq == LlvmCmpPredicate.ICmpEq && BitwiseNonZero(x, q))
            return isAnd ? zero : unsigned;
        if (predicate == LlvmCmpPredicate.ICmpUle && eq == LlvmCmpPredicate.ICmpNe && BitwiseNonZero(x, q))
            return isAnd ? unsigned : zero;
        if (predicate == LlvmCmpPredicate.ICmpUlt && eq == LlvmCmpPredicate.ICmpNe) return isAnd ? unsigned : zero;
        if (predicate == LlvmCmpPredicate.ICmpUge && eq == LlvmCmpPredicate.ICmpEq) return isAnd ? zero : unsigned;
        if (predicate == LlvmCmpPredicate.ICmpUlt && eq == LlvmCmpPredicate.ICmpEq && isAnd) return LlvmConstantInt.GetFalse(zero.Context);
        if (predicate == LlvmCmpPredicate.ICmpUge && eq == LlvmCmpPredicate.ICmpNe && !isAnd) return LlvmConstantInt.GetTrue(zero.Context);
        return null;
    }

    private static bool BitwiseComparePair(LlvmICmpInstruction comparison, LlvmValue a, LlvmValue b, out LlvmCmpPredicate p)
    {
        p = comparison.Predicate;
        if (comparison.Left == a && comparison.Right == b) return true;
        if (comparison.Left != b || comparison.Right != a) return false;
        p = LlvmCmpInstruction.GetSwappedPredicate(p);
        return true;
    }

    private static LlvmValue? SimplifyBitwiseICmpConstants(LlvmICmpInstruction lhs, LlvmICmpInstruction rhs, bool isAnd)
    {
        if (lhs.Left != rhs.Left || lhs.Right is not LlvmConstantInt a || rhs.Right is not LlvmConstantInt b) return null;
        var range0 = LlvmConstantRange.MakeExactICmpRegion(lhs.Predicate, a.Value);
        var range1 = LlvmConstantRange.MakeExactICmpRegion(rhs.Predicate, b.Value);
        if (isAnd && range0.IntersectWith(range1).IsEmptySet) return LlvmConstantInt.GetFalse(lhs.Context);
        if (!isAnd && range0.UnionWith(range1).IsFullSet) return LlvmConstantInt.GetTrue(lhs.Context);
        if (range0.Contains(range1)) return isAnd ? rhs : lhs;
        if (range1.Contains(range0)) return isAnd ? lhs : rhs;
        return null;
    }

    private static LlvmValue? SimplifyBitwiseICmpAdd(LlvmICmpInstruction lhs, LlvmICmpInstruction rhs, bool isAnd, BinaryQuery q)
    {
        if (lhs.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt c0 } add ||
            lhs.Right is not LlvmConstantInt c1 || rhs.Left != add.Left || rhs.Right != add.Right) return null;
        LlvmAPInt delta = c1.Value - c0.Value;
        ulong distance = delta.GetLimitedValue();
        bool nsw = q.UseInstructionInfo && add.HasNoSignedWrap;
        bool nuw = q.UseInstructionInfo && add.HasNoUnsignedWrap;
        LlvmCmpPredicate p = lhs.Predicate, r = rhs.Predicate;
        if (!c0.Value.IsNegative && !c0.Value.IsZero)
        {
            if (isAnd && distance == 2 && r == LlvmCmpPredicate.ICmpSgt &&
                (p == LlvmCmpPredicate.ICmpUlt || p == LlvmCmpPredicate.ICmpSlt && nsw)) return LlvmConstantInt.GetFalse(lhs.Context);
            if (isAnd && distance == 1 && r == LlvmCmpPredicate.ICmpSgt &&
                (p == LlvmCmpPredicate.ICmpUle || p == LlvmCmpPredicate.ICmpSle && nsw)) return LlvmConstantInt.GetFalse(lhs.Context);
            if (!isAnd && distance == 2 && r == LlvmCmpPredicate.ICmpSle &&
                (p == LlvmCmpPredicate.ICmpUge || p == LlvmCmpPredicate.ICmpSge && nsw)) return LlvmConstantInt.GetTrue(lhs.Context);
            if (!isAnd && distance == 1 && r == LlvmCmpPredicate.ICmpSle &&
                (p == LlvmCmpPredicate.ICmpUgt || p == LlvmCmpPredicate.ICmpSgt && nsw)) return LlvmConstantInt.GetTrue(lhs.Context);
        }
        if (c0.IsZero || !nuw) return null;
        if (isAnd && distance == 2 && p == LlvmCmpPredicate.ICmpUlt && r == LlvmCmpPredicate.ICmpUgt) return LlvmConstantInt.GetFalse(lhs.Context);
        if (isAnd && distance == 1 && p == LlvmCmpPredicate.ICmpUle && r == LlvmCmpPredicate.ICmpUgt) return LlvmConstantInt.GetFalse(lhs.Context);
        if (!isAnd && distance == 2 && p == LlvmCmpPredicate.ICmpUge && r == LlvmCmpPredicate.ICmpUle) return LlvmConstantInt.GetTrue(lhs.Context);
        if (!isAnd && distance == 1 && p == LlvmCmpPredicate.ICmpUgt && r == LlvmCmpPredicate.ICmpUle) return LlvmConstantInt.GetTrue(lhs.Context);
        return null;
    }

    private static LlvmValue? SimplifyBitwiseICmps(LlvmICmpInstruction lhs, LlvmICmpInstruction rhs, bool isAnd, BinaryQuery q) =>
        SimplifyUnsignedRangeCheck(lhs, rhs, isAnd, q) ?? SimplifyUnsignedRangeCheck(rhs, lhs, isAnd, q) ??
        SimplifyBitwiseICmpConstants(lhs, rhs, isAnd) ??
        SimplifyBitwiseICmpAdd(lhs, rhs, isAnd, q) ?? SimplifyBitwiseICmpAdd(rhs, lhs, isAnd, q);

    private static LlvmValue? SimplifyBitwiseComparisons(LlvmValue lhs, LlvmValue rhs, bool isAnd, BinaryQuery q)
    {
        LlvmCastInstruction? cast = lhs as LlvmCastInstruction;
        if (cast is not null && rhs is LlvmCastInstruction other && cast.Opcode == other.Opcode && cast.Operand.Type == other.Operand.Type)
        { lhs = cast.Operand; rhs = other.Operand; }
        LlvmValue? value = (lhs, rhs) switch
        {
            (LlvmICmpInstruction a, LlvmICmpInstruction b) => SimplifyBitwiseICmps(a, b, isAnd, q),
            (LlvmFCmpInstruction a, LlvmFCmpInstruction b) => SimplifyBitwiseFCmps(a, b, isAnd),
            _ => null
        };
        if (value is null || cast is null) return value;
        return value is LlvmConstant constant
            ? LlvmConstantFolding.ConstantFoldCastOperand(cast.Opcode, constant, cast.Type, q.Layout, q.Metrics) : null;
    }

    private static bool BitwiseOrderedFP(LlvmCmpPredicate p) => (byte)p is >= 1 and <= 7;
    private static bool BitwiseUnorderedFP(LlvmCmpPredicate p) => (byte)p is >= 8 and <= 14;
    private static LlvmValue? SimplifyBitwiseFCmps(LlvmFCmpInstruction lhs, LlvmFCmpInstruction rhs, bool isAnd)
    {
        if (lhs.Left.Type != rhs.Left.Type) return null;
        LlvmCmpPredicate p = lhs.Predicate, r = rhs.Predicate;
        if (p is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno &&
            (BitwiseOrderedFP(r) && isAnd || BitwiseUnorderedFP(r) && !isAnd) &&
            (rhs.Left == lhs.Left || rhs.Right == lhs.Left) && IsFPPositiveZero(lhs.Right))
            return BitwiseOrderedFP(p) == BitwiseOrderedFP(r) ? rhs : LlvmConstantInt.GetBool(lhs.Context, !isAnd);
        if (r is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno &&
            (BitwiseOrderedFP(p) && isAnd || BitwiseUnorderedFP(p) && !isAnd) &&
            (lhs.Left == rhs.Left || lhs.Right == rhs.Left) && IsFPPositiveZero(rhs.Right))
            return BitwiseOrderedFP(p) == BitwiseOrderedFP(r) ? lhs : LlvmConstantInt.GetBool(lhs.Context, !isAnd);
        if (lhs.Left != rhs.Left || lhs.Right is not LlvmConstantFP a || rhs.Right is not LlvmConstantFP b) return null;
        var range0 = LlvmConstantFPRange.MakeExactFCmpRegion(isAnd ? p : LlvmCmpInstruction.GetInversePredicate(p), a.Value);
        var range1 = LlvmConstantFPRange.MakeExactFCmpRegion(isAnd ? r : LlvmCmpInstruction.GetInversePredicate(r), b.Value);
        if (range0 is null || range1 is null) return null;
        if (range0.IntersectWith(range1).IsEmptySet) return LlvmConstantInt.GetBool(lhs.Context, !isAnd);
        if (range0.Contains(range1)) return rhs;
        if (range1.Contains(range0)) return lhs;
        return null;
    }
}
