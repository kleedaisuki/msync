// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// foldBinOpShiftWithShift and foldBinOpOfSelectAndCastOfSelectCondition.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool AlgebraValidShiftBin(LlvmOpcode opcode) => AlgebraLogic(opcode) || opcode == LlvmOpcode.Add;
        private static bool AlgebraCompletelyDistributable(LlvmOpcode first, LlvmOpcode second, LlvmOpcode shift) =>
            first != LlvmOpcode.Add && second != LlvmOpcode.Add || shift == LlvmOpcode.Shl;
        private static LlvmOpcode AlgebraInverseShift(LlvmOpcode opcode) => opcode == LlvmOpcode.LShr ? LlvmOpcode.Shl : LlvmOpcode.LShr;
        private LlvmInstruction? FoldBinOpShiftWithShift(LlvmBinaryOperator instruction)
        {
            Count("algebra.shift_pair.queries");
            return FoldBinOpShiftArm(instruction, 0) ?? FoldBinOpShiftArm(instruction, 1);
        }
        private LlvmInstruction? FoldBinOpShiftArm(LlvmBinaryOperator instruction, int shiftOperand)
        {
            if (instruction.GetOperand(shiftOperand) is not LlvmBinaryOperator yShift || !yShift.HasOneUse || !AlgebraShift(yShift.Opcode) ||
                instruction.GetOperand(1 - shiftOperand) is not LlvmBinaryOperator wrapped || !wrapped.HasOneUse) return null;
            LlvmBinaryOperator? xShift = null;
            LlvmValue mask;
            if (wrapped.Left is LlvmBinaryOperator l && l.HasOneUse && AlgebraShift(l.Opcode) && l.Right == yShift.Right)
            { xShift = l; mask = wrapped.Right; }
            else if (wrapped.Right is LlvmBinaryOperator r && r.HasOneUse && AlgebraShift(r.Opcode) && r.Right == yShift.Right)
            { xShift = r; mask = wrapped.Left; }
            else return null;
            var shiftOpcode = yShift.Opcode;
            if (xShift.Opcode != shiftOpcode || !AlgebraValidShiftBin(instruction.Opcode) || !AlgebraValidShiftBin(wrapped.Opcode)) return null;
            var x = xShift.Left; var y = yShift.Left; var amount = yShift.Right;
            if (shiftOpcode == LlvmOpcode.AShr)
            {
                if (!AlgebraLogic(instruction.Opcode) || wrapped.Opcode != LlvmOpcode.Xor || mask is not LlvmConstantInt { IsMinusOne: true }) return null;
                var negated = Builder.CreateNot(x);
                var combined = Builder.CreateBinOp(instruction.Opcode, y, negated);
                Count("algebra.shift_pair.arithmetic_not"); return new LlvmBinaryOperator(shiftOpcode, combined, amount);
            }
            if (wrapped.Opcode == instruction.Opcode && AlgebraCompletelyDistributable(instruction.Opcode, wrapped.Opcode, shiftOpcode))
            {
                var combined = Builder.CreateBinOp(instruction.Opcode, x, y);
                var shifted = Builder.CreateBinOp(shiftOpcode, combined, amount);
                Count("algebra.shift_pair.same_binop"); return new LlvmBinaryOperator(instruction.Opcode, shifted, mask);
            }
            if (amount is not LlvmConstant shiftConstant || mask is not LlvmConstant maskConstant) return null;
            if (instruction.Opcode != LlvmOpcode.And)
            {
                if (!AlgebraCompletelyDistributable(instruction.Opcode, wrapped.Opcode, shiftOpcode)) return null;
                if (wrapped.Opcode != LlvmOpcode.And)
                {
                    var unshifted = LlvmConstantFolding.ConstantFoldBinaryOpOperands(AlgebraInverseShift(shiftOpcode), maskConstant, shiftConstant, DL, Metrics)!;
                    if (LlvmConstantFolding.ConstantFoldBinaryOpOperands(shiftOpcode, unshifted, shiftConstant, DL, Metrics) != maskConstant) return null;
                }
            }
            var newMask = LlvmConstantFolding.ConstantFoldBinaryOpOperands(AlgebraInverseShift(shiftOpcode), maskConstant, shiftConstant, DL, Metrics)!;
            var inner = Builder.CreateBinOp(wrapped.Opcode, x, newMask);
            var outer = Builder.CreateBinOp(instruction.Opcode, y, inner);
            Count("algebra.shift_pair.constant_mask"); return new LlvmBinaryOperator(shiftOpcode, outer, shiftConstant);
        }
        private static bool AlgebraSelectLike(LlvmValue value, out LlvmValue condition, out LlvmConstant whenTrue, out LlvmConstant whenFalse)
        {
            if (value is LlvmSelectInstruction { TrueValue: LlvmConstant a, FalseValue: LlvmConstant b } select)
            { condition = select.Condition; whenTrue = a; whenFalse = b; return true; }
            if (value is LlvmCastInstruction cast && cast.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt && cast.Operand.Type.IsIntegerTy(1))
            {
                condition = cast.Operand;
                whenTrue = LlvmConstantInt.Get(value.Context, cast.Opcode == LlvmOpcode.ZExt ? new LlvmAPInt(value.Type.GetIntegerBitWidth(), 1)
                    : LlvmAPInt.GetAllOnes(value.Type.GetIntegerBitWidth()));
                whenFalse = LlvmConstant.GetNullValue(value.Type); return true;
            }
            condition = null!; whenTrue = whenFalse = null!; return false;
        }
        private static bool AlgebraNotOf(LlvmValue value, LlvmValue other) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor &&
            (xor.Left == other && xor.Right is LlvmConstantInt { IsMinusOne: true } || xor.Right == other && xor.Left is LlvmConstantInt { IsMinusOne: true });
        private LlvmInstruction? FoldBinOpOfSelectAndCastOfSelectCondition(LlvmBinaryOperator instruction)
        {
            LlvmValue castValue, condition;
            LlvmConstant castTrue, castFalse;
            LlvmSelectInstruction select;
            if (AlgebraSelectLike(instruction.Left, out condition, out castTrue, out castFalse) && instruction.Right is LlvmSelectInstruction right)
            { castValue = instruction.Left; select = right; }
            else if (AlgebraSelectLike(instruction.Right, out condition, out castTrue, out castFalse) && instruction.Left is LlvmSelectInstruction left)
            { castValue = instruction.Right; select = left; }
            else return null;
            bool inverted = AlgebraNotOf(condition, select.Condition);
            if (condition != select.Condition && !inverted) return null;
            LlvmValue FoldArm(LlvmValue arm, LlvmConstant constant) => castValue == instruction.Right
                ? Builder.CreateBinOp(instruction.Opcode, arm, constant) : Builder.CreateBinOp(instruction.Opcode, constant, arm);
            var trueValue = FoldArm(select.TrueValue, inverted ? castFalse : castTrue);
            var falseValue = FoldArm(select.FalseValue, inverted ? castTrue : castFalse);
            Count("algebra.select_condition_cast"); return new LlvmSelectInstruction(select.Condition, trueValue, falseValue);
        }
    }
}
