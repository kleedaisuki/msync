// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp visitShl.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitShl(LlvmBinaryOperator instruction)
        {
            Count("instcombine.shift.shl.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simplified) return ReplaceInstUsesWith(instruction, simplified);
            if (CommonShiftTransforms(instruction) is { } common) return common;
            if (DropRedundantMaskingOfLeftShiftInput(instruction) is { } masking) return masking;
            LlvmValue operand = instruction.Left, amount = instruction.Right;
            LlvmType type = instruction.Type;
            uint width = type.GetScalarSizeInBits();
            if (amount is LlvmConstantInt constant)
            {
                uint bits = (uint)constant.Value.GetZExtValue();
                LlvmAPInt highMask = LlvmAPInt.GetHighBitsSet(width, width - bits);
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } extension)
                {
                    uint sourceWidth = extension.Operand.Type.GetScalarSizeInBits();
                    if (bits < sourceWidth && ShiftMaskedZero(extension.Operand, LlvmAPInt.GetHighBitsSet(sourceWidth, bits), instruction))
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinary(LlvmOpcode.Shl, extension.Operand, ShiftInt(extension.Operand.Type, bits)), type), "instcombine.shift.shl.narrow_zext");
                }
                if (operand is LlvmBinaryOperator shrink && IsRightShiftOp(shrink.Opcode) && ReferenceEquals(shrink.Right, amount))
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, shrink.Left, ShiftInt(type, highMask)), "instcombine.shift.shl.cancel_right_mask");
                if (operand is LlvmBinaryOperator right && IsRightShiftOp(right.Opcode) && right.Right is LlvmConstantInt rightAmount && rightAmount.Value.GetLimitedValue(width) < width)
                {
                    uint rightBits = (uint)rightAmount.Value.GetZExtValue();
                    if (right.IsExact)
                    {
                        if (rightBits < bits)
                            return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Shl, right.Left, ShiftInt(type, bits - rightBits))
                            { HasNoUnsignedWrap = instruction.HasNoUnsignedWrap || (rightBits != 0 && right.Opcode == LlvmOpcode.LShr && instruction.HasNoSignedWrap), HasNoSignedWrap = instruction.HasNoSignedWrap }, "instcombine.shift.shl.cancel_exact_right");
                        if (rightBits > bits)
                            return ShiftResult(new LlvmBinaryOperator(right.Opcode, right.Left, ShiftInt(type, rightBits - bits)) { IsExact = true }, "instcombine.shift.shl.reduce_exact_right");
                    }
                    if (right.HasOneUse && rightBits != bits)
                    {
                        LlvmBinaryOperator shifted;
                        if (rightBits < bits)
                            shifted = new(LlvmOpcode.Shl, right.Left, ShiftInt(type, bits - rightBits))
                            { HasNoUnsignedWrap = instruction.HasNoUnsignedWrap || (rightBits != 0 && right.Opcode == LlvmOpcode.LShr && instruction.HasNoSignedWrap), HasNoSignedWrap = instruction.HasNoSignedWrap };
                        else shifted = new(right.Opcode, right.Left, ShiftInt(type, rightBits - bits)) { IsExact = right.IsExact };
                        Builder.Insert(shifted);
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, shifted, ShiftInt(type, highMask)), "instcombine.shift.shl.combine_right_with_mask");
                    }
                }
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true, Operand: LlvmBinaryOperator inner } &&
                    inner.HasOneUse && IsRightShiftOp(inner.Opcode) && inner.Right is LlvmConstantInt innerAmount)
                {
                    uint rightBits = (uint)innerAmount.Value.GetZExtValue();
                    uint difference = rightBits > bits ? rightBits - bits : bits - rightBits;
                    LlvmValue shifted = Builder.CreateBinOp(rightBits > bits ? inner.Opcode : LlvmOpcode.Shl, inner.Left, ShiftInt(inner.Left.Type, difference), "sh.diff");
                    LlvmValue truncation = Builder.CreateTrunc(shifted, type, "tr.sh.diff");
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, truncation, ShiftInt(type, highMask)), "instcombine.shift.shl.truncated_opposite");
                }
                if (operand is LlvmBinaryOperator binary && binary.HasOneUse && binary.Opcode is LlvmOpcode.Add or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.Sub)
                {
                    LlvmValue shifted = binary.Left, y = binary.Right;
                    bool RightShiftSame(LlvmValue value) => value is LlvmBinaryOperator candidate && IsRightShiftOp(candidate.Opcode) && ReferenceEquals(candidate.Right, amount);
                    bool MaskedRightShiftSame(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Left: LlvmBinaryOperator candidate, Right: LlvmConstantInt } && candidate.HasOneUse && IsRightShiftOp(candidate.Opcode) && ReferenceEquals(candidate.Right, amount);
                    if (binary.IsCommutative && y.HasOneUse && (RightShiftSame(y) || MaskedRightShiftSame(y))) (shifted, y) = (y, shifted);
                    if (shifted is LlvmBinaryOperator shr && shr.HasOneUse && RightShiftSame(shr))
                    {
                        LlvmValue yShifted = Builder.CreateBinOp(LlvmOpcode.Shl, y, amount, binary.Name);
                        LlvmValue combined = Builder.CreateBinOp(binary.Opcode, shr.Left, yShifted, shr.Name);
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, combined, ShiftInt(type, highMask)), "instcombine.shift.shl.reorder_binop_right");
                    }
                    if (shifted is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Left: LlvmBinaryOperator maskedShr, Right: LlvmConstantInt mask } &&
                        maskedShr.HasOneUse && RightShiftSame(maskedShr))
                    {
                        LlvmValue yShifted = Builder.CreateBinOp(LlvmOpcode.Shl, y, amount, binary.Name);
                        LlvmValue xMasked = Builder.CreateAnd(maskedShr.Left, ShiftInt(type, mask.Value.Shl(constant.Value)), maskedShr.Left.Name + ".mask");
                        return ShiftResult(new LlvmBinaryOperator(binary.Opcode, xMasked, yShifted) { IsDisjoint = binary.IsDisjoint }, "instcombine.shift.shl.reorder_masked_binop_right");
                    }
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true, Left: LlvmConstantInt leftConstant } subtract)
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Sub, ShiftInt(type, leftConstant.Value.Shl(constant.Value)), Builder.CreateBinOp(LlvmOpcode.Shl, subtract.Right, amount)), "instcombine.shift.shl.distribute_constant_sub");
            }
            if (SetShiftFlags(instruction)) return instruction;
            if (operand is LlvmBinaryOperator shrVariable && shrVariable.HasOneUse && IsRightShiftOp(shrVariable.Opcode) && ReferenceEquals(shrVariable.Right, amount))
            {
                LlvmValue mask = Builder.CreateBinOp(LlvmOpcode.Shl, ShiftInt(type, LlvmAPInt.GetAllOnes(width)), amount);
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, mask, shrVariable.Left), "instcombine.shift.shl.variable_cancel_mask");
            }
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmConstantInt { IsMinusOne: true } } allOnes && ReferenceEquals(allOnes.Right, amount))
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Shl, ShiftInt(type, LlvmAPInt.GetAllOnes(width)), amount), "instcombine.shift.shl.variable_allones");
            if (amount is LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue)
            {
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue } multiply)
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Mul, multiply.Left, Builder.CreateBinOp(LlvmOpcode.Shl, multiply.Right, amount)), "instcombine.shift.shl.multiply_constant");
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } cast && cast.Operand.Type.IsIntegerTy(1))
                    return ShiftResult(new LlvmSelectInstruction(cast.Operand, Builder.CreateBinOp(LlvmOpcode.Shl, ShiftInt(type, 1), amount), LlvmConstant.GetNullValue(type)), "instcombine.shift.shl.boolean_select");
            }
            if (operand is LlvmConstantInt { IsOne: true })
            {
                if (amount is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt max } subtraction && max.Value.GetLimitedValue(width) == width - 1)
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.LShr, ShiftInt(type, LlvmAPInt.GetSignedMinValue(width)), subtraction.Right), "instcombine.shift.shl.reverse_signbit");
                if (amount.HasOneUse && ShiftIntrinsic(amount, LlvmIntrinsicKind.CTTZ, out var cttz))
                {
                    LlvmValue x = cttz.GetArgument(0);
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNeg(x, "neg"), x), "instcombine.shift.shl.cttz_lowbit");
                }
            }
            if (amount.HasOneUse && ShiftIntrinsic(amount, LlvmIntrinsicKind.CTTZ, out var trailing))
            {
                LlvmValue x = trailing.GetArgument(0);
                LlvmValue lowBit = Builder.CreateAnd(Builder.CreateNeg(x, "neg"), x);
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Mul, lowBit, operand) { HasNoUnsignedWrap = instruction.HasNoUnsignedWrap }, "instcombine.shift.shl.cttz_multiply");
            }
            return null;
        }
    }
}
