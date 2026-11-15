// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitSwitchInst / simplifySwitchOnSelectUsingRanges.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitSwitchInst(LlvmSwitchInstruction instruction)
        {
            LlvmValue condition = instruction.Condition;
            if (condition is LlvmBinaryOperator { HasOneUse: true } binary && MatchInvertibleSwitch(binary) is { } inverse)
            {
                for (int index = 0; index < instruction.CaseCount; ++index)
                {
                    LlvmAPInt value = instruction.GetCaseValue(index).Value;
                    LlvmAPInt rewritten = binary.Opcode switch
                    {
                        LlvmOpcode.Add => value - inverse.Constant,
                        LlvmOpcode.Sub => inverse.Constant - value,
                        _ => value ^ inverse.Constant
                    };
                    instruction.SetCaseValue(index, LlvmConstantInt.Get(instruction.Context, rewritten));
                }
                Count("control.switch.inverted-operation");
                return ReplaceOperand(instruction, 0, inverse.Operand);
            }
            if (TrySimplifyShiftedSwitch(instruction, condition) is { } shifted) return shifted;
            if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } extend)
            {
                uint width = extend.Operand.Type.GetScalarSizeInBits();
                bool isZeroExtend = extend.Opcode == LlvmOpcode.ZExt;
                if (Enumerable.Range(0, instruction.CaseCount).All(index => isZeroExtend
                    ? instruction.GetCaseValue(index).Value.GetActiveBits() <= width
                    : instruction.GetCaseValue(index).Value.GetSignificantBits() <= width))
                {
                    TruncateSwitchCases(instruction, width);
                    Count("control.switch.extension-removed");
                    return ReplaceOperand(instruction, 0, extend.Operand);
                }
            }
            if (condition is LlvmSelectInstruction select)
            {
                LlvmValue? replacement = SimplifySwitchOnSelectUsingRanges(instruction, select, true) ??
                    SimplifySwitchOnSelectUsingRanges(instruction, select, false);
                if (replacement is not null)
                {
                    Count("control.switch.select-removed");
                    return ReplaceOperand(instruction, 0, replacement);
                }
            }
            LlvmKnownBits known = ComputeKnownBits(condition, instruction);
            uint leadingZeros = known.CountMinLeadingZeros(), leadingOnes = known.CountMinLeadingOnes();
            for (int index = 0; index < instruction.CaseCount; ++index)
            {
                LlvmAPInt value = instruction.GetCaseValue(index).Value;
                leadingZeros = Math.Min(leadingZeros, value.CountLeadingZeros());
                leadingOnes = Math.Min(leadingOnes, value.CountLeadingOnes());
            }
            uint newWidth = known.BitWidth - Math.Max(leadingZeros, leadingOnes);
            if (newWidth > 0 && newWidth < known.BitWidth && ShouldChangeType(known.BitWidth, newWidth))
            {
                Builder.SetInsertPoint(instruction);
                LlvmValue truncated = Builder.CreateTrunc(condition, LlvmIntegerType.Get(instruction.Context, newWidth), "trunc");
                TruncateSwitchCases(instruction, newWidth);
                Count("control.switch.condition-narrowed");
                return ReplaceOperand(instruction, 0, truncated);
            }
            if (condition is LlvmUndefValue) HandlePotentiallyDeadSuccessors(instruction.Parent!, null);
            else if (condition is LlvmConstantInt constant)
            {
                int index = instruction.FindCaseValue(constant);
                HandlePotentiallyDeadSuccessors(instruction.Parent!, index < 0 ? instruction.DefaultDestination : instruction.GetCaseDestination(index));
            }
            return null;
        }

        private LlvmInstruction? TrySimplifyShiftedSwitch(LlvmSwitchInstruction instruction, LlvmValue condition)
        {
            if (condition is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt amount } shift ||
                amount.Value.GetActiveBits() > 64) return null;
            ulong wideAmount = amount.Value.GetZExtValue();
            uint width = shift.Left.Type.GetScalarSizeInBits();
            if (wideAmount >= width) return null;
            uint shiftAmount = (uint)wideAmount;
            for (int index = 0; index < instruction.CaseCount; ++index)
                if (instruction.GetCaseValue(index).Value.CountTrailingZeros() < shiftAmount) return null;
            if (!shift.HasNoUnsignedWrap && !shift.HasNoSignedWrap && !shift.HasOneUse) return null;
            LlvmValue conditionValue = shift.Left;
            if (!shift.HasNoUnsignedWrap && !shift.HasNoSignedWrap)
                conditionValue = Builder.CreateAnd(conditionValue,
                    LlvmConstantInt.Get(instruction.Context, LlvmAPInt.GetLowBitsSet(width, width - shiftAmount)));
            for (int index = 0; index < instruction.CaseCount; ++index)
            {
                LlvmAPInt value = instruction.GetCaseValue(index).Value;
                value = shift.HasNoSignedWrap ? value.AShr(shiftAmount) : value.LShr(shiftAmount);
                instruction.SetCaseValue(index, LlvmConstantInt.Get(instruction.Context, value));
            }
            Count("control.switch.shift-removed");
            return ReplaceOperand(instruction, 0, conditionValue);
        }

        private static (LlvmValue Operand, LlvmAPInt Constant)? MatchInvertibleSwitch(LlvmBinaryOperator binary)
        {
            if (binary.Opcode == LlvmOpcode.Add && binary.Right is LlvmConstantInt addition)
                return (binary.Left, addition.Value);
            if (binary.Opcode == LlvmOpcode.Sub && binary.Left is LlvmConstantInt subtraction)
                return (binary.Right, subtraction.Value);
            if (binary.Opcode == LlvmOpcode.Xor && binary.Right is LlvmConstantInt xor &&
                !xor.Value.IsMinSignedValue && !xor.Value.IsMaxSignedValue) return (binary.Left, xor.Value);
            return null;
        }

        private static LlvmValue? SimplifySwitchOnSelectUsingRanges(LlvmSwitchInstruction instruction,
            LlvmSelectInstruction select, bool isTrueArm)
        {
            if ((isTrueArm ? select.TrueValue : select.FalseValue) is not LlvmConstantInt constant) return null;
            int selectedCase = instruction.FindCaseValue(constant);
            if (selectedCase >= 0 && instruction.GetCaseDestination(selectedCase) != instruction.DefaultDestination) return null;
            LlvmValue value = isTrueArm ? select.FalseValue : select.TrueValue;
            if (select.Condition is not LlvmICmpInstruction compare || compare.Left != value || compare.Right is not LlvmConstantInt rhs) return null;
            LlvmCmpPredicate predicate = isTrueArm ? LlvmCmpInstruction.GetInversePredicate(compare.Predicate) : compare.Predicate;
            LlvmConstantRange range = LlvmConstantRange.MakeExactICmpRegion(predicate, rhs.Value);
            for (int index = 0; index < instruction.CaseCount; ++index)
                if (!range.Contains(instruction.GetCaseValue(index).Value)) return null;
            return value;
        }

        private static void TruncateSwitchCases(LlvmSwitchInstruction instruction, uint width)
        {
            for (int index = 0; index < instruction.CaseCount; ++index)
                instruction.SetCaseValue(index, LlvmConstantInt.Get(instruction.Context, instruction.GetCaseValue(index).Value.Trunc(width)));
        }
    }
}
