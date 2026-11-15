// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp 3028–3236.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmConstantInt? FunnelAPInt(LlvmValue value, bool allowPoison = false) => value as LlvmConstantInt ??
            (value.Type.IsVectorTy() ? (value as LlvmConstant)?.GetSplatValue(allowPoison) as LlvmConstantInt : null);
        private static bool FunnelInt(LlvmValue value, ulong integer) => FunnelAPInt(value)?.Value == new LlvmAPInt(value.Type.GetScalarSizeInBits(), integer);
        private static LlvmConstant FunnelConstant(LlvmType type, LlvmAPInt bits) => LlvmConstantInt.Get(type, bits);
        private static LlvmConstant FunnelMergeUndefs(LlvmConstant constant, LlvmConstant other)
        {
            if (constant is LlvmUndefValue) return constant;
            if (other is LlvmUndefValue) return LlvmUndefValue.Get(constant.Type);
            if (constant.Type is not LlvmFixedVectorType type) return constant;
            var elements = new LlvmConstant[type.NumElements];
            bool changed = false;
            for (uint lane = 0; lane < type.NumElements; ++lane)
            {
                elements[lane] = constant.GetAggregateElement(lane)!;
                if (elements[lane] is not LlvmUndefValue && other.GetAggregateElement(lane) is LlvmUndefValue)
                { elements[lane] = LlvmUndefValue.Get(type.ElementType); changed = true; }
            }
            return changed ? LlvmConstantVector.Get(elements) : constant;
        }
        private static bool FunnelConstantsSum(LlvmConstant left, LlvmConstant right, uint width)
        {
            if (left.Type is LlvmFixedVectorType vector)
            {
                bool any = false;
                for (uint lane = 0; lane < vector.NumElements; ++lane)
                {
                    var a = left.GetAggregateElement(lane); var b = right.GetAggregateElement(lane);
                    if (a is LlvmPoisonValue || b is LlvmPoisonValue) continue;
                    if (a is not LlvmConstantInt ai || b is not LlvmConstantInt bi ||
                        ai.Value.GetLimitedValue() >= width || bi.Value.GetLimitedValue() >= width ||
                        ai.Value + bi.Value != new LlvmAPInt(ai.BitWidth, width)) return false;
                    any = true;
                }
                return any;
            }
            return false;
        }
        private (LlvmIntrinsicKind Kind, LlvmValue[] Arguments)? ConvertOrOfShiftsToFunnelShift(LlvmInstruction instruction)
        {
            uint width = instruction.Type.GetScalarSizeInBits();
            if (instruction.GetOperand(0) is not LlvmInstruction first || instruction.GetOperand(1) is not LlvmInstruction second) return null;
            bool isFshl = true;
            LlvmValue[]? arguments = null;
            if (first is LlvmBinaryOperator firstBinary && second is LlvmBinaryOperator secondBinary)
            {
                if (!firstBinary.HasOneUse || !secondBinary.HasOneUse ||
                    firstBinary.Opcode is not (LlvmOpcode.Shl or LlvmOpcode.LShr) ||
                    secondBinary.Opcode is not (LlvmOpcode.Shl or LlvmOpcode.LShr) || firstBinary.Opcode == secondBinary.Opcode) return null;
                if (firstBinary.Opcode == LlvmOpcode.LShr) (firstBinary, secondBinary) = (secondBinary, firstBinary);
                LlvmValue? amount = MatchFunnelShiftAmount(firstBinary.Right, secondBinary.Right, firstBinary.Left, secondBinary.Left, width, instruction);
                if (amount is null)
                {
                    amount = MatchFunnelShiftAmount(secondBinary.Right, firstBinary.Right, firstBinary.Left, secondBinary.Left, width, instruction);
                    isFshl = false;
                }
                if (amount is null) return null;
                arguments = [firstBinary.Left, secondBinary.Left, amount];
            }
            else if (first is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } || second is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt })
            {
                if (second is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt }) (first, second) = (second, first);
                if (first is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true } shift ||
                    FunnelAPInt(shift.Right) is not { } highAmount ||
                    second is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } lowExtension ||
                    shift.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } highExtension) return null;
                uint highSize = highExtension.Operand.Type.GetScalarSizeInBits(), lowSize = lowExtension.Operand.Type.GetScalarSizeInBits();
                if (highAmount.Value.GetLimitedValue() < lowSize || highAmount.Value.GetLimitedValue() > width - highSize) return null;
                foreach (var user in highExtension.Users)
                {
                    if (user is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } concat) continue;
                    LlvmValue x = concat.Left, y = concat.Right;
                    if (y is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt }) (x, y) = (y, x);
                    if (x is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } lowShift || !ReferenceEquals(lowShift.Left, lowExtension) ||
                        !ReferenceEquals(y, highExtension) || FunnelAPInt(lowShift.Right) is not { } lowAmount || !DT.Dominates(concat, instruction)) continue;
                    if (lowAmount.Value + highAmount.Value != new LlvmAPInt(highAmount.BitWidth, width)) continue;
                    concat.DropPoisonGeneratingFlags(); lowShift.DropPoisonGeneratingFlags();
                    arguments = [concat, concat, FunnelConstant(first.Type, highAmount.Value)];
                    break;
                }
            }
            return arguments is null ? null : (isFshl ? LlvmIntrinsicKind.FShl : LlvmIntrinsicKind.FShr, arguments);
        }
        private LlvmValue? MatchFunnelShiftAmount(LlvmValue left, LlvmValue right, LlvmValue shiftedLeft,
            LlvmValue shiftedRight, uint width, LlvmInstruction context)
        {
            if (FunnelAPInt(left, true) is { } li && FunnelAPInt(right, true) is { } ri &&
                li.Value.GetLimitedValue() < width && ri.Value.GetLimitedValue() < width && li.Value + ri.Value == new LlvmAPInt(li.BitWidth, width))
                return FunnelConstant(left.Type, li.Value);
            if (left is LlvmConstant lc && right is LlvmConstant rc && FunnelConstantsSum(lc, rc, width)) return FunnelMergeUndefs(lc, rc);
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true } sub && FunnelInt(sub.Left, width) && ReferenceEquals(sub.Right, left))
                return LogicKnown(left, context).GetMaxValue().GetLimitedValue() < width ? left : null;
            if (!ReferenceEquals(shiftedLeft, shiftedRight) || !BitOperations.IsPow2(width)) return null;
            uint mask = width - 1;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && FunnelInt(and.Right, mask) &&
                right is LlvmBinaryOperator { Opcode: LlvmOpcode.And } rightAnd && FunnelInt(rightAnd.Right, mask) &&
                LogicNeg(rightAnd.Left, out var negated) && ReferenceEquals(negated, and.Left)) return and.Left;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add } addMask &&
                FunnelInt(addMask.Right, mask) && FunnelInt(add.Right, 1) &&
                right is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And } xorAnd } xor &&
                FunnelInt(xor.Right, mask) && FunnelInt(xorAnd.Right, mask) && ReferenceEquals(xorAnd.Left, add.Left)) return add;
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.And } maskedNeg && FunnelInt(maskedNeg.Right, mask) &&
                LogicNeg(maskedNeg.Left, out var negatedLeft) && ReferenceEquals(negatedLeft, left)) return left;
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.And } inputMask } && FunnelInt(inputMask.Right, mask))
            {
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.And } outerMask && FunnelInt(outerMask.Right, mask) &&
                    LogicNeg(outerMask.Left, out var extension) && extension is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.And } nestedMask } &&
                    ReferenceEquals(nestedMask.Left, inputMask.Left) && FunnelInt(nestedMask.Right, mask)) return left;
                if (right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.And } negativeMask } &&
                    FunnelInt(negativeMask.Right, mask) && LogicNeg(negativeMask.Left, out var input) && ReferenceEquals(input, inputMask.Left)) return left;
            }
            return null;
        }
        private LlvmInstruction? MatchFunnelShift(LlvmBinaryOperator instruction)
        {
            if (ConvertOrOfShiftsToFunnelShift(instruction) is not { } match) return null;
            Count("instcombine.logic.funnel_shift");
            return LlvmIntrinsic.CreateIntrinsic(match.Kind, match.Arguments, instruction.Type, F.Parent!, Metrics);
        }
    }
}
