// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp,
// isPoisonShift, simplifyShift, simplifyRightShift, simplify{Shl,LShr,AShr}Inst.
using System.Numerics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyIntegerShift(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        bool nsw, bool nuw, bool exact, BinaryQuery q, uint depth)
    {
        if (SimplifyIntegerShiftCommon(op, lhs, rhs, op == LlvmOpcode.Shl && nsw, q, depth) is { } common) return common;
        uint width = ((LlvmIntegerType)lhs.Type).BitWidth;
        if (op == LlvmOpcode.Shl)
        {
            if (q.IsUndef(lhs)) return nsw || nuw ? lhs : IntegerZero(lhs);
            if (q.UseInstructionInfo && lhs is LlvmBinaryOperator
                { Opcode: LlvmOpcode.LShr or LlvmOpcode.AShr, IsExact: true } shr && shr.Right == rhs) return shr.Left;
            if (nuw && lhs is LlvmConstantInt { IsNegative: true }) return lhs;
            if (nsw && nuw && rhs is LlvmConstantInt amount && amount.EqualsInt(width - 1)) return IntegerZero(lhs);
            return null;
        }
        if (lhs == rhs) return IntegerZero(lhs);
        if (q.IsUndef(lhs)) return exact ? lhs : IntegerZero(lhs);
        if (exact && !(LlvmKnownBitsAnalysis.Compute(lhs, context: q.Context, tree: q.Tree,
            metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext).One & new LlvmAPInt(width, 1)).IsZero) return lhs;
        if (q.UseInstructionInfo && lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shift &&
            shift.Right == rhs && (op == LlvmOpcode.LShr ? shift.HasNoUnsignedWrap : shift.HasNoSignedWrap))
            return shift.Left;
        if (op == LlvmOpcode.LShr)
        {
            if (!q.UseInstructionInfo || rhs is not LlvmConstantInt rightAmount ||
                lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or) return null;
            return SimplifyIntegerOrShift(or.Left, or.Right, rightAmount, q) ??
                SimplifyIntegerOrShift(or.Right, or.Left, rightAmount, q);
        }
        if (IsIntegerAllOnes(lhs) || lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } allShift &&
            IsIntegerAllOnes(allShift.Left) && allShift.Right == rhs) return IntegerAllOnes(lhs);
        if (LlvmKnownBitsAnalysis.ComputeNumSignBits(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext) == width)
            return lhs;
        return null;
    }

    private static LlvmValue? SimplifyIntegerOrShift(LlvmValue shifted, LlvmValue other,
        LlvmConstantInt amount, BinaryQuery q)
    {
        if (shifted is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true } shl ||
            shl.Right is not LlvmConstantInt leftAmount || leftAmount.Value != amount.Value) return null;
        uint active = ((LlvmIntegerType)other.Type).BitWidth - LlvmKnownBitsAnalysis.Compute(other,
            context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext).Zero.CountLeadingOnes();
        return amount.Value.Uge(new LlvmAPInt(amount.BitWidth, active)) ? shl.Left : null;
    }

    private static LlvmValue? SimplifyIntegerShiftCommon(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        bool nsw, BinaryQuery q, uint depth)
    {
        if (FoldOrCommuteConstant(op, ref lhs, ref rhs, q) is { } folded) return folded;
        if (lhs is LlvmPoisonValue) return lhs;
        if (IsIntegerZero(lhs)) return IntegerZero(lhs);
        if (IsIntegerZero(rhs) || rhs is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } ext &&
            ext.Operand.Type is LlvmIntegerType { BitWidth: 1 }) return lhs;
        uint width = ((LlvmIntegerType)lhs.Type).BitWidth;
        if (q.IsUndef(rhs) || rhs is LlvmPoisonValue || rhs is LlvmConstantInt amount &&
            amount.Value.Uge(new LlvmAPInt(amount.BitWidth, amount.BitWidth))) return LlvmPoisonValue.Get(lhs.Type);
        if (ThreadIntegerBin(op, lhs, rhs, q, depth) is { } threaded) return threaded;
        var knownAmount = LlvmKnownBitsAnalysis.Compute(rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
        if (knownAmount.One.Uge(new LlvmAPInt(width, width))) return LlvmPoisonValue.Get(lhs.Type);
        uint validBits = (uint)BitOperations.Log2(width) + (BitOperations.IsPow2(width) ? 0u : 1u);
        if (knownAmount.Zero.CountTrailingOnes() >= validBits) return lhs;
        if (nsw)
        {
            var knownValue = LlvmKnownBitsAnalysis.Compute(lhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            var shifted = LlvmKnownBits.Shl(knownValue, knownAmount);
            var sign = LlvmAPInt.GetSignedMinValue(width);
            if (shifted.HasConflict || !((shifted.One & knownValue.Zero & sign) | (shifted.Zero & knownValue.One & sign)).IsZero)
                return LlvmPoisonValue.Get(lhs.Type);
        }
        return null;
    }
}
