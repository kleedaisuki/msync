// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// scalar PatternMatch adapters and source ValueTracking query plumbing.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmConstant LogicC(LlvmType type, LlvmAPInt value) => LlvmConstantInt.Get(type, value);
        private static LlvmConstant LogicC(LlvmType type, ulong value) => LogicC(type, new LlvmAPInt(type.GetScalarSizeInBits(), value));
        private static LlvmConstant LogicZero(LlvmType type) => LogicC(type, 0UL);
        private static LlvmConstant LogicOnes(LlvmType type) => LogicC(type, LlvmAPInt.GetAllOnes(type.GetScalarSizeInBits()));
        private static LlvmBinaryOperator? LogicBin(LlvmValue value, LlvmOpcode opcode) => value is LlvmBinaryOperator binary && binary.Opcode == opcode ? binary : null;
        private static bool LogicOther(LlvmValue value, LlvmOpcode opcode, LlvmValue specific, out LlvmValue other)
        {
            if (LogicBin(value, opcode) is { } binary)
            {
                if (ReferenceEquals(binary.Left, specific)) { other = binary.Right; return true; }
                if (ReferenceEquals(binary.Right, specific)) { other = binary.Left; return true; }
            }
            other = null!; return false;
        }
        private static bool LogicPair(LlvmValue value, LlvmOpcode opcode, LlvmValue a, LlvmValue b) =>
            LogicOther(value, opcode, a, out var other) && ReferenceEquals(other, b);
        private static bool LogicIntegerPredicate(LlvmValue value, bool allOnes)
        {
            if (value is LlvmConstantInt integer) return allOnes ? integer.IsMinusOne : integer.IsZero;
            return value.Type.IsIntOrIntVectorTy() && value.Type.IsVectorTy() && value is LlvmConstant constant &&
                constant.GetSplatValue(true) is LlvmConstantInt splat && (allOnes ? splat.IsMinusOne : splat.IsZero);
        }
        private static bool LogicOperator(LlvmValue value, LlvmOpcode opcode, out LlvmValue left, out LlvmValue right)
        {
            if (value is LlvmBinaryOperator binary && binary.Opcode == opcode)
            { left = binary.Left; right = binary.Right; return true; }
            if (value is LlvmConstantExpr expression && expression.Opcode == opcode)
            { left = expression.GetConstantOperand(0); right = expression.GetConstantOperand(1); return true; }
            left = right = null!; return false;
        }
        private static bool LogicNot(LlvmValue value, out LlvmValue operand)
        {
            if (LogicOperator(value, LlvmOpcode.Xor, out var left, out var right))
            {
                if (LogicIntegerPredicate(left, true)) { operand = right; return true; }
                if (LogicIntegerPredicate(right, true)) { operand = left; return true; }
            }
            operand = null!; return false;
        }
        private static bool LogicNeg(LlvmValue value, out LlvmValue operand)
        {
            if (LogicOperator(value, LlvmOpcode.Sub, out var left, out var right) && LogicIntegerPredicate(left, false))
            { operand = right; return true; }
            operand = null!; return false;
        }
        private static LlvmValue LogicCastOrSelf(LlvmValue value, LlvmOpcode opcode) =>
            value is LlvmCastInstruction cast && cast.Opcode == opcode ? cast.Operand : value;
        private static bool LogicMask(LlvmAPInt value) => !value.IsZero && value.CountTrailingOnes() + value.CountLeadingZeros() == value.BitWidth;
        private static bool LogicMask(LlvmAPInt value, uint bits) => value == LlvmAPInt.GetLowBitsSet(value.BitWidth, bits);
        private static void LogicTakeName(LlvmValue value, LlvmValue source)
        { if (value is LlvmInstruction instruction) instruction.TakeName(source); }
        private LlvmKnownBits LogicKnown(LlvmValue value, LlvmInstruction context) =>
            LlvmKnownBitsAnalysis.Compute(value, context: context, tree: DT, metrics: Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC);
        private bool LogicMaskedZero(LlvmValue value, LlvmAPInt mask, LlvmInstruction context) =>
            LlvmKnownBitsAnalysis.MaskedValueIsZero(value, mask, context: context, tree: DT, metrics: Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC);
        private bool LogicPower(LlvmValue value, bool orZero, LlvmInstruction context) =>
            LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(value, orZero, context: context, tree: DT, metrics: Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC);
        private bool LogicNotUndef(LlvmValue value, LlvmInstruction context) =>
            LlvmValueTracking.IsGuaranteedNotToBeUndef(value, DT, context, Metrics, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC);
        private LlvmInstruction LogicResult(LlvmInstruction instruction, string sourcePattern)
        { if (Metrics is not null) Count("instcombine.logic." + sourcePattern); return instruction; }
        private LlvmInstruction? LogicReplace(LlvmInstruction instruction, LlvmValue replacement, string sourcePattern)
        { if (Metrics is not null) Count("instcombine.logic." + sourcePattern); return ReplaceInstUsesWith(instruction, replacement); }
        private LlvmInstruction LogicSelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse, string sourcePattern) =>
            LogicResult(new LlvmSelectInstruction(condition, whenTrue, whenFalse), sourcePattern);
        private static LlvmBinaryOperator LogicNotInstruction(LlvmValue value) => new(LlvmOpcode.Xor, value, LogicOnes(value.Type));
        private static bool LogicLogical(LlvmValue value, bool and, out LlvmValue a, out LlvmValue b) =>
            SharedLogicalMatch(value, and, out a, out b);
    }
}
