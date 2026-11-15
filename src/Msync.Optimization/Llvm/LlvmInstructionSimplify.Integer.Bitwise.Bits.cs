// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp shifted masks/rotated all-ones/masked-add rules.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmKnownBits BitwiseKnownBits(LlvmValue value, BinaryQuery q) =>
        LlvmKnownBitsAnalysis.Compute(value, context: q.Context, tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
    private static bool BitwisePowerOfTwo(LlvmValue value, bool orZero, BinaryQuery q) =>
        LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(value, orZero, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
    private static bool BitwiseNonZero(LlvmValue value, BinaryQuery q) =>
        LlvmKnownBitsAnalysis.IsKnownNonZero(value, context: q.Context, tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);

    private static LlvmValue? SimplifyAndShiftedOr(LlvmValue value, LlvmAPInt mask, BinaryQuery q)
    {
        if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or) return null;
        if (or.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true, Right: LlvmConstantInt amount } shift)
            return SimplifyAndShiftedOrArm(shift, amount.Value, or.Right, mask, q);
        if (or.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true, Right: LlvmConstantInt rightAmount } rightShift)
            return SimplifyAndShiftedOrArm(rightShift, rightAmount.Value, or.Left, mask, q);
        return null;
    }

    private static LlvmValue? SimplifyAndShiftedOrArm(LlvmBinaryOperator shifted, LlvmAPInt amount,
        LlvmValue other, LlvmAPInt mask, BinaryQuery q)
    {
        uint width = ((LlvmIntegerType)shifted.Type).BitWidth;
        uint shiftCount = (uint)amount.GetLimitedValue(width);
        uint yWidth = BitwiseKnownBits(other, q).CountMaxActiveBits();
        if (yWidth > shiftCount) return null;
        uint xWidth = BitwiseKnownBits(shifted.Left, q).CountMaxActiveBits();
        LlvmAPInt yBits = LlvmAPInt.GetLowBitsSet(width, yWidth);
        LlvmAPInt xBits = LlvmAPInt.GetLowBitsSet(width, xWidth).Shl(shiftCount);
        if ((yBits & ~mask).IsZero && (xBits & mask).IsZero) return other;
        if ((xBits & ~mask).IsZero && (yBits & mask).IsZero) return shifted;
        return null;
    }

    private static bool BitwiseRotatedAllOnes(LlvmValue lhs, LlvmValue rhs)
    {
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } leftShift && IsIntegerAllOnes(leftShift.Left) &&
            rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr } rightShift && IsIntegerAllOnes(rightShift.Left))
            return BitwiseComplementaryShiftCounts(leftShift.Right, rightShift.Right);
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } swappedLeft && IsIntegerAllOnes(swappedLeft.Left) &&
            lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr } swappedRight && IsIntegerAllOnes(swappedRight.Left))
            return BitwiseComplementaryShiftCounts(swappedLeft.Right, swappedRight.Right);
        return false;
    }
    private static bool BitwiseComplementaryShiftCounts(LlvmValue x, LlvmValue y) =>
        BitwiseComplementaryShiftCount(x, y) || BitwiseComplementaryShiftCount(y, x);
    private static bool BitwiseComplementaryShiftCount(LlvmValue x, LlvmValue y) =>
        x is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt bound } sub && sub.Right == y &&
        bound.Value.Ule(new LlvmAPInt(bound.BitWidth, ((LlvmIntegerType)x.Type).BitWidth));

    private static LlvmValue? SimplifyOrMaskedAdd(LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt c1 } a ||
            rhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt c2 } b ||
            c1.Value != ~c2.Value) return null;
        if (BitwiseIsLowMask(c2.Value) && MatchCommutative(a.Left, LlvmOpcode.Add, b.Left) is { } n &&
            LlvmKnownBitsAnalysis.MaskedValueIsZero(n, c2.Value, context: q.Context, tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext)) return a.Left;
        if (BitwiseIsLowMask(c1.Value) && MatchCommutative(b.Left, LlvmOpcode.Add, a.Left) is { } m &&
            LlvmKnownBitsAnalysis.MaskedValueIsZero(m, c1.Value, context: q.Context, tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext)) return b.Left;
        return null;
    }

    private static LlvmValue? SimplifyBitwiseImplied(LlvmValue lhs, LlvmValue rhs, bool isAnd, BinaryQuery q)
    {
        if (IsImpliedCondition(lhs, rhs, q, isAnd) is { } implied)
            return implied == isAnd ? lhs : LlvmConstantInt.GetBool(lhs.Context, !isAnd);
        if (IsImpliedCondition(rhs, lhs, q, isAnd) is { } reverse)
            return reverse == isAnd ? rhs : LlvmConstantInt.GetBool(rhs.Context, !isAnd);
        return null;
    }
}
