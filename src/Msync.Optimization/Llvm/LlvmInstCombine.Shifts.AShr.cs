// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp
// foldVariableSignZeroExtensionOfVariableHighBitExtract / visitAShr.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool ShiftWidthMinus(LlvmValue amount, LlvmValue? expected, uint width, out LlvmValue bits)
        {
            amount = ShiftZExtOrSelf(amount);
            if (amount is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt constant } subtraction && constant.Value.GetLimitedValue(width + 1UL) == width)
            {
                bits = ShiftZExtOrSelf(subtraction.Right);
                return expected is null || ReferenceEquals(expected, bits);
            }
            bits = amount; return false;
        }
        private LlvmInstruction? FoldVariableSignZeroExtensionOfVariableHighBitExtract(LlvmBinaryOperator instruction)
        {
            uint width = instruction.Type.GetScalarSizeInBits();
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmInstruction maybeTrunc } shl ||
                !ShiftWidthMinus(shl.Right, null, width, out LlvmValue bits) || !ShiftWidthMinus(instruction.Right, bits, width, out _)) return null;
            LlvmCastInstruction? truncation = maybeTrunc as LlvmCastInstruction;
            if (truncation?.Opcode != LlvmOpcode.Trunc) truncation = null;
            LlvmValue extract = truncation?.Operand ?? maybeTrunc;
            if (extract is not LlvmBinaryOperator inner || !IsRightShiftOp(inner.Opcode) ||
                !ShiftWidthMinus(inner.Right, bits, inner.Type.GetScalarSizeInBits(), out _)) return null;
            if (inner.Opcode == instruction.Opcode)
                return ReplaceInstUsesWith(instruction, maybeTrunc);
            if (truncation is not null && !instruction.Left.HasOneUse && !instruction.Right.HasOneUse) return null;
            var result = new LlvmBinaryOperator(LlvmOpcode.AShr, inner.Left, inner.Right) { IsExact = inner.IsExact };
            Count("instcombine.shift.ashr.variable_high_extract");
            if (truncation is null) return result;
            Builder.Insert(result);
            return new LlvmCastInstruction(LlvmOpcode.Trunc, result, instruction.Type);
        }
        private static bool ShiftOrNegation(LlvmValue value, out LlvmValue x)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or)
            {
                if (ShiftNeg(or.Left, out x) && ReferenceEquals(x, or.Right)) return true;
                if (ShiftNeg(or.Right, out x) && ReferenceEquals(x, or.Left)) return true;
            }
            x = value; return false;
        }
        private static bool ShiftDecrementAndNot(LlvmValue value, out LlvmValue x)
        {
            bool Match(LlvmValue left, LlvmValue right, out LlvmValue operand)
            {
                if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsMinusOne: true } } add && ShiftNot(right, out operand) && ReferenceEquals(add.Left, operand)) return true;
                operand = value; return false;
            }
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && (Match(and.Left, and.Right, out x) || Match(and.Right, and.Left, out x))) return true;
            x = value; return false;
        }
        private LlvmInstruction? VisitAShr(LlvmBinaryOperator instruction)
        {
            Count("instcombine.shift.ashr.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simplified) return ReplaceInstUsesWith(instruction, simplified);
            if (CommonShiftTransforms(instruction) is { } common) return common;
            LlvmValue operand = instruction.Left, amount = instruction.Right;
            LlvmType type = instruction.Type;
            uint width = type.GetScalarSizeInBits();
            if (amount is LlvmConstantInt constant && constant.Value.GetLimitedValue(width) < width)
            {
                uint bits = (uint)constant.Value.GetZExtValue();
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } zext } shl &&
                    ReferenceEquals(shl.Right, amount) && bits == width - zext.Operand.Type.GetScalarSizeInBits())
                    return ShiftResult(new LlvmCastInstruction(LlvmOpcode.SExt, zext.Operand, type), "instcombine.shift.ashr.signextend_narrow");
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoSignedWrap: true, Right: LlvmConstantInt innerAmount } left && innerAmount.Value.GetLimitedValue(width) < width)
                {
                    uint leftBits = (uint)innerAmount.Value.GetZExtValue();
                    if (leftBits < bits)
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.AShr, left.Left, ShiftInt(type, bits - leftBits)) { IsExact = instruction.IsExact }, "instcombine.shift.ashr.reduce_nsw_left");
                    if (leftBits > bits)
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Shl, left.Left, ShiftInt(type, leftBits - bits)) { HasNoSignedWrap = true }, "instcombine.shift.ashr.cancel_nsw_left");
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt innerShift } right && innerShift.Value.GetLimitedValue(width) < width)
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.AShr, right.Left, ShiftInt(type, Math.Min(bits + (uint)innerShift.Value.GetZExtValue(), width - 1))), "instcombine.shift.ashr.combine_clamped_amounts");
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } extension && ShouldChangeType(type, extension.Operand.Type))
                {
                    uint narrow = Math.Min(bits, extension.Operand.Type.GetScalarSizeInBits() - 1);
                    LlvmValue shifted = Builder.CreateBinOp(LlvmOpcode.AShr, extension.Operand, ShiftInt(extension.Operand.Type, narrow));
                    return ShiftResult(new LlvmCastInstruction(LlvmOpcode.SExt, shifted, type), "instcombine.shift.ashr.narrow_sext");
                }
                if (bits == width - 1 && operand.HasOneUse)
                {
                    if (ShiftOrNegation(operand, out var x))
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.SExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, x, LlvmConstant.GetNullValue(type)), type), "instcombine.shift.ashr.nonzero_sign_splat");
                    if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } subtract)
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.SExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, subtract.Left, subtract.Right), type), "instcombine.shift.ashr.signed_compare");
                    if (ShiftDecrementAndNot(operand, out var decremented))
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.SExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, decremented, LlvmConstant.GetNullValue(type)), type), "instcombine.shift.ashr.zero_sign_splat");
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true, HasNoSignedWrap: true, Right: LlvmConstantInt multiplier } multiply &&
                    width > 2 && (multiplier.Value - new LlvmAPInt(width, 1)).IsPowerOf2 && multiplier.Value.GetActiveBits() - 1 == bits && bits < width - 1)
                {
                    LlvmValue shifted = Builder.CreateBinary(LlvmOpcode.AShr, multiply.Left, ShiftInt(type, bits), exact: instruction.IsExact);
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Add, multiply.Left, shifted) { HasNoSignedWrap = true, HasNoUnsignedWrap = multiply.HasNoUnsignedWrap }, "instcombine.shift.ashr.multiply_splat");
                }
            }
            if (SetShiftFlags(instruction)) return instruction;
            if (amount is LlvmConstantInt top && top.Value.GetLimitedValue(width) == width - 1 &&
                operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true, Right: LlvmConstantInt inner } lowSplat && inner.Value.GetLimitedValue(width) == width - 1)
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstant.GetNullValue(type), Builder.CreateAnd(lowSplat.Left, ShiftInt(type, 1))), "instcombine.shift.ashr.lowbit_splat");
            if (FoldVariableSignZeroExtensionOfVariableHighBitExtract(instruction) is { } variableExtract) return variableExtract;
            if (ShiftMaskedZero(operand, LlvmAPInt.GetSignedMinValue(width), instruction))
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.LShr, operand, amount) { IsExact = instruction.IsExact }, "instcombine.shift.ashr.to_logical");
            if (operand.HasOneUse && ShiftNot(operand, out var not))
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateBinOp(LlvmOpcode.AShr, not, amount, operand.Name + ".not"), ShiftInt(type, LlvmAPInt.GetAllOnes(width))), "instcombine.shift.ashr.distribute_not");
            return null;
        }
    }
}
