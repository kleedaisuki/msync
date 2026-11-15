// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp
// dropRedundantMaskingOfLeftShiftInput and foldShiftOfShiftedBinOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MatchShiftInputMask(LlvmValue mask, out LlvmValue amount, out bool sum)
        {
            sum = false; amount = mask;
            if (mask is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } shl, Right: LlvmConstantInt { IsMinusOne: true } })
            { amount = shl.Right; sum = true; return true; }
            if (ShiftNot(mask, out var inverse) && inverse is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsMinusOne: true } } inverseShift)
            { amount = inverseShift.Right; sum = true; return true; }
            if (mask is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr } right) return false;
            if (right.Left is LlvmConstantInt { IsMinusOne: true }) { amount = right.Right; return true; }
            if (right.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsMinusOne: true } } left && ReferenceEquals(left.Right, right.Right))
            { amount = right.Right; return true; }
            return false;
        }
        private LlvmInstruction? DropRedundantMaskingOfLeftShiftInput(LlvmBinaryOperator outer)
        {
            LlvmValue masked = outer.Left, shiftAmount = ShiftZExtOrSelf(outer.Right);
            LlvmCastInstruction? truncation = masked as LlvmCastInstruction;
            if (truncation?.Opcode != LlvmOpcode.Trunc) truncation = null;
            if (truncation is not null)
            { if (!truncation.HasOneUse) return null; masked = truncation.Operand; }
            uint widest = masked.Type.GetScalarSizeInBits();
            if ((ulong)widest * 2 > (1U << 23)) return null;
            LlvmType extendedType = LlvmIntegerType.Get(outer.Context, widest * 2);
            LlvmValue x, amount;
            bool sum;
            if (masked is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && MatchShiftInputMask(and.Left, out amount, out sum)) x = and.Right;
            else if (masked is LlvmBinaryOperator { Opcode: LlvmOpcode.And } reverse && MatchShiftInputMask(reverse.Right, out amount, out sum)) x = reverse.Left;
            else if (masked is LlvmBinaryOperator right && IsRightShiftOp(right.Opcode) && right.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } left && ReferenceEquals(left.Right, right.Right))
            { x = left.Left; amount = right.Right; sum = false; }
            else return null;
            amount = ShiftZExtOrSelf(amount);
            if (!CanTryToConstantAddTwoShiftAmounts(outer, shiftAmount, masked, amount)) return null;
            LlvmValue? simplified = SimplifyShiftAmount(sum ? LlvmOpcode.Add : LlvmOpcode.Sub, sum ? amount : shiftAmount, sum ? shiftAmount : amount, outer);
            if (simplified is not LlvmConstant constant) return null;
            if (constant is LlvmUndefValue)
                constant = ShiftInt(constant.Type, sum ? widest * 2 : unchecked((ulong)-(long)widest));
            if (!sum)
            {
                constant = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Sub, ShiftInt(constant.Type, widest), constant, DL, Metrics)!;
                if (constant is null) return null;
            }
            LlvmConstant? extendedAmount = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.ZExt, constant, extendedType, DL, Metrics);
            if (extendedAmount is null) return null;
            LlvmConstant? mask = LlvmConstantFolding.ConstantFoldBinaryOpOperands(sum ? LlvmOpcode.Shl : LlvmOpcode.LShr,
                ShiftInt(extendedType, LlvmAPInt.GetAllOnes(widest * 2)), extendedAmount, DL, Metrics);
            if (mask is null) return null;
            if (sum)
                mask = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Xor, mask, ShiftInt(extendedType, LlvmAPInt.GetAllOnes(widest * 2)), DL, Metrics);
            if (mask is null) return null;
            mask = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.Trunc, mask, outer.Type, DL, Metrics);
            if (mask is null) return null;
            bool needMask = mask is not LlvmConstantInt { IsMinusOne: true };
            if (needMask && (!masked.HasOneUse || masked is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr })) return null;
            if (truncation is not null) x = Builder.CreateTrunc(x, outer.Type);
            var shift = new LlvmBinaryOperator(outer.Opcode, x, outer.Right);
            Count(needMask ? "instcombine.shift.masking.constant_mask" : "instcombine.shift.masking.removed");
            if (!needMask) return shift;
            Builder.Insert(shift);
            return new LlvmBinaryOperator(LlvmOpcode.And, shift, mask);
        }
        private LlvmInstruction? FoldShiftOfShiftedBinOp(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmBinaryOperator binary || !binary.HasOneUse ||
                binary.Opcode is not (LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.Add or LlvmOpcode.Sub) || instruction.Right is not LlvmConstant amount) return null;
            if (binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub && instruction.Opcode != LlvmOpcode.Shl) return null;
            LlvmValue x = binary.Left, y = binary.Right;
            LlvmConstant? sum = null;
            bool MatchFirst(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmBinaryOperator shift || shift.Opcode != instruction.Opcode || shift.Right is not LlvmConstant constant || (!value.HasOneUse && other is not (LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue))) return false;
                sum = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Add, constant, amount, DL, Metrics);
                if (sum is not LlvmConstantInt combined || combined.Value.GetLimitedValue(instruction.Type.GetScalarSizeInBits()) >= instruction.Type.GetScalarSizeInBits()) return false;
                x = shift.Left; return true;
            }
            bool reversedSub = false;
            if (!MatchFirst(binary.Left, binary.Right))
            {
                if (!MatchFirst(binary.Right, binary.Left)) return null;
                y = binary.Left; reversedSub = binary.Opcode == LlvmOpcode.Sub;
            }
            LlvmValue shift1 = Builder.CreateBinOp(instruction.Opcode, x, sum!);
            LlvmValue shift2 = Builder.CreateBinOp(instruction.Opcode, y, amount);
            return ShiftResult(new LlvmBinaryOperator(binary.Opcode, reversedSub ? shift2 : shift1, reversedSub ? shift1 : shift2), "instcombine.shift.distribute_shifted_binary");
        }
    }
}
