// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp
// commonShiftTransforms / FoldShiftByConstant / foldLShrOverflowBit.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? CommonShiftTransforms(LlvmBinaryOperator instruction)
        {
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (right is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } extension)
                return ShiftResult(new LlvmBinaryOperator(instruction.Opcode, left, Builder.CreateZExt(extension.Operand, instruction.Type, extension.Name)), "instcombine.shift.sext_amount_to_zext");
            if (SimplifyDemandedInstructionBits(instruction)) return instruction;
            if (left is LlvmConstant && right is LlvmSelectInstruction select && FoldOpIntoSelect(instruction, select) is { } foldedSelect) return foldedSelect;
            if (right is LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue && FoldShiftByConstant(left, (LlvmConstant)right, instruction) is { } folded) return folded;
            if (ReassociateShiftAmtsOfTwoSameDirectionShifts(instruction, SQ) is LlvmInstruction reassociated) return reassociated;
            if (left is LlvmConstant constant && right is LlvmBinaryOperator addLike &&
                ((addLike.Opcode == LlvmOpcode.Add && addLike.HasNoUnsignedWrap) || (addLike.Opcode == LlvmOpcode.Or && addLike.IsDisjoint)) && addLike.Right is LlvmConstant offset)
            {
                LlvmValue preshift = Builder.CreateBinOp(instruction.Opcode, constant, offset);
                var result = new LlvmBinaryOperator(instruction.Opcode, preshift, addLike.Left);
                if (instruction.Opcode == LlvmOpcode.Shl) { result.HasNoSignedWrap = instruction.HasNoSignedWrap; result.HasNoUnsignedWrap = instruction.HasNoUnsignedWrap; }
                else result.IsExact = instruction.IsExact;
                return ShiftResult(result, "instcombine.shift.preshift_nuw_offset");
            }
            uint width = instruction.Type.GetScalarSizeInBits();
            if (left is LlvmConstantInt integer)
            {
                LlvmAPInt value = integer.Value;
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt negative } add && negative.Value.IsNegative && (-negative.Value).GetLimitedValue(width) < width)
                {
                    uint amount = (uint)(-negative.Value).GetZExtValue();
                    bool suitable = instruction.Opcode switch
                    {
                        LlvmOpcode.Shl => (instruction.HasNoSignedWrap || instruction.HasNoUnsignedWrap) && value == value.LShr(amount).Shl(amount),
                        LlvmOpcode.LShr => instruction.IsExact && value == value.Shl(amount).LShr(amount),
                        LlvmOpcode.AShr => instruction.IsExact && value == value.Shl(amount).AShr(amount),
                        _ => false
                    };
                    if (suitable)
                    {
                        var result = new LlvmBinaryOperator(instruction.Opcode, ShiftInt(instruction.Type, instruction.Opcode == LlvmOpcode.Shl ? value.LShr(amount) : value.Shl(amount)), add.Left);
                        if (instruction.Opcode == LlvmOpcode.Shl) result.HasNoUnsignedWrap = instruction.HasNoUnsignedWrap;
                        else result.IsExact = true;
                        return ShiftResult(result, "instcombine.shift.preshift_negative_offset");
                    }
                }
                LlvmValue? x = null; ulong c2 = 0;
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true, Left: LlvmConstantInt baseAmount } sub && baseAmount.Value.TryZExtValue() is { } subAmount)
                { x = sub.Right; c2 = subAmount; }
                else if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt xorAmount } xor && xorAmount.Value.TryZExtValue() is { } xorBits && (xorAmount.Value | ShiftKnown(xor.Left, instruction).Zero).IsAllOnes)
                { x = xor.Left; c2 = xorBits; }
                if (x is not null)
                {
                    if (instruction.Opcode == LlvmOpcode.Shl)
                    {
                        if (value.CountLeadingZeros() >= c2)
                            return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.LShr, ShiftInt(instruction.Type, value.Shl((uint)c2)), x) { IsExact = true }, "instcombine.shift.reverse_constant_lshr");
                        if (value.CountLeadingOnes() > c2)
                            return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.AShr, ShiftInt(instruction.Type, value.Shl((uint)c2)), x) { IsExact = true }, "instcombine.shift.reverse_constant_ashr");
                    }
                    else if (value.CountTrailingZeros() >= c2)
                    {
                        var result = new LlvmBinaryOperator(LlvmOpcode.Shl, ShiftInt(instruction.Type, instruction.Opcode == LlvmOpcode.AShr && value.IsNegative ? value.AShr((uint)c2) : value.LShr((uint)c2)), x);
                        result.HasNoUnsignedWrap = !value.IsNegative || instruction.Opcode == LlvmOpcode.LShr;
                        result.HasNoSignedWrap = !value.IsNegative || instruction.Opcode == LlvmOpcode.AShr;
                        return ShiftResult(result, "instcombine.shift.reverse_constant_shl");
                    }
                }
            }
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.SRem, HasOneUse: true, Right: LlvmConstantInt divisor } remainder && divisor.Value.IsPowerOf2)
                return ReplaceOperand(instruction, 1, Builder.CreateAnd(remainder.Left, ShiftInt(instruction.Type, divisor.Value - new LlvmAPInt(width, 1)), remainder.Name));
            if (FoldShiftOfShiftedBinOp(instruction) is { } shiftedBinary) return shiftedBinary;
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt allBits } && allBits.Value.GetLimitedValue(width) == width - 1)
                return ReplaceOperand(instruction, 1, ShiftInt(instruction.Type, width - 1));
            if (IsRightShiftOp(instruction.Opcode) && left is LlvmCallBase comparison && comparison.HasOneUse &&
                comparison.CalledFunction?.IntrinsicDescriptor is { HasComparisonRange: true } descriptor && right is LlvmConstantInt signAmount && signAmount.Value.GetLimitedValue(width) == width - 1)
            {
                LlvmCmpPredicate predicate = descriptor.Kind == LlvmIntrinsicKind.SCmp ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt;
                LlvmValue compare = Builder.CreateICmp(predicate, comparison.GetArgument(0), comparison.GetArgument(1));
                return ShiftResult(new LlvmCastInstruction(instruction.Opcode == LlvmOpcode.LShr ? LlvmOpcode.ZExt : LlvmOpcode.SExt, compare, instruction.Type), "instcombine.shift.compare_signbit");
            }
            return null;
        }

        private static bool CanShiftBinOpWithConstantRHS(LlvmBinaryOperator shift, LlvmBinaryOperator binary) => binary.Opcode switch
        {
            LlvmOpcode.Add => shift.Opcode == LlvmOpcode.Shl,
            LlvmOpcode.And or LlvmOpcode.Or => true,
            LlvmOpcode.Xor => !(shift.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr && ShiftNot(binary, out _)),
            _ => false
        };
        private LlvmInstruction? FoldShiftByConstant(LlvmValue operand, LlvmConstant constant, LlvmBinaryOperator instruction)
        {
            bool left = instruction.Opcode == LlvmOpcode.Shl;
            if (operand is LlvmBinaryOperator inner && inner.Opcode == instruction.Opcode && inner.Left is LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue)
            {
                var result = new LlvmBinaryOperator(instruction.Opcode, Builder.CreateBinOp(instruction.Opcode, inner.Left, constant), inner.Right);
                if (left) { result.HasNoUnsignedWrap = instruction.HasNoUnsignedWrap && inner.HasNoUnsignedWrap; result.HasNoSignedWrap = instruction.HasNoSignedWrap && inner.HasNoSignedWrap; }
                else result.IsExact = instruction.IsExact && inner.IsExact;
                return ShiftResult(result, "instcombine.shift.constant_reassociate");
            }
            uint width = instruction.Type.GetScalarSizeInBits();
            if (!left && constant is LlvmConstantInt signShift && signShift.Value.GetLimitedValue(width) == width - 1 &&
                operand is LlvmBinaryOperator { Opcode: LlvmOpcode.SDiv, Right: LlvmConstantInt divisor } divide && !divisor.Value.IsZero && !divisor.Value.IsMinSignedValue)
            {
                LlvmValue compare = Builder.CreateICmp(divisor.Value.IsNegative ? LlvmCmpPredicate.ICmpSge : LlvmCmpPredicate.ICmpSle, divide.Left, ShiftInt(instruction.Type, -divisor.Value));
                return ShiftResult(new LlvmCastInstruction(instruction.Opcode == LlvmOpcode.AShr ? LlvmOpcode.SExt : LlvmOpcode.ZExt, compare, instruction.Type), "instcombine.shift.sdiv_signbit");
            }
            if (!left && constant is LlvmConstantInt half && half.Value.GetLimitedValue(width) == width / 2 &&
                operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true, Left: LlvmCastInstruction extLeft, Right: LlvmCastInstruction extRight } multiply &&
                extLeft.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt && extLeft.Opcode == extRight.Opcode &&
                ReferenceEquals(extLeft.Operand.Type, extRight.Operand.Type) && width == 2 * extLeft.Operand.Type.GetScalarSizeInBits())
            {
                LlvmValue high = Builder.CreateBinaryIntrinsic(extLeft.Opcode == LlvmOpcode.SExt ? LlvmIntrinsicKind.SMulH : LlvmIntrinsicKind.UMulH, extLeft.Operand, extRight.Operand);
                return ShiftResult(new LlvmCastInstruction(instruction.Opcode == LlvmOpcode.LShr ? LlvmOpcode.ZExt : LlvmOpcode.SExt, high, instruction.Type), "instcombine.shift.multiply_high");
            }
            if (constant is not LlvmConstantInt amount) return null;
            uint bits = (uint)amount.Value.GetZExtValue();
            if (instruction.Opcode != LlvmOpcode.AShr && CanEvaluateShifted(operand, bits, left, left ? ShiftSemantics.Lossy : ShiftSemantics.Unsigned, instruction))
            {
                Count("instcombine.shift.constant.expression_propagation");
                return ReplaceInstUsesWith(instruction, GetShiftedValue(operand, bits, left, left ? ShiftSemantics.Lossy : ShiftSemantics.Unsigned));
            }
            if (FoldBinOpIntoSelectOrPhi(instruction) is { } folded) return folded;
            if (!operand.HasOneUse) return null;
            if (operand is LlvmBinaryOperator binary && binary.Right is LlvmConstantInt && CanShiftBinOpWithConstantRHS(instruction, binary))
            {
                LlvmValue rhs = Builder.CreateBinOp(instruction.Opcode, binary.Right, constant);
                LlvmValue newShift = Builder.CreateBinOp(instruction.Opcode, binary.Left, constant);
                newShift.TakeName(binary);
                return ShiftResult(new LlvmBinaryOperator(binary.Opcode, newShift, rhs), "instcombine.shift.constant.pull_binary");
            }
            if (operand is LlvmSelectInstruction select)
            {
                if (select.TrueValue is LlvmBinaryOperator { HasOneUse: true, Right: LlvmConstantInt } whenTrue &&
                    select.FalseValue is not LlvmConstant && ReferenceEquals(whenTrue.Left, select.FalseValue) && CanShiftBinOpWithConstantRHS(instruction, whenTrue))
                {
                    LlvmValue rhs = Builder.CreateBinOp(instruction.Opcode, whenTrue.Right, constant);
                    LlvmValue shifted = Builder.CreateBinOp(instruction.Opcode, select.FalseValue, constant);
                    return ShiftResult(new LlvmSelectInstruction(select.Condition, Builder.CreateBinOp(whenTrue.Opcode, shifted, rhs), shifted), "instcombine.shift.constant.pull_select_true_binary");
                }
                if (select.FalseValue is LlvmBinaryOperator { HasOneUse: true, Right: LlvmConstantInt } whenFalse &&
                    select.TrueValue is not LlvmConstant && ReferenceEquals(whenFalse.Left, select.TrueValue) && CanShiftBinOpWithConstantRHS(instruction, whenFalse))
                {
                    LlvmValue rhs = Builder.CreateBinOp(instruction.Opcode, whenFalse.Right, constant);
                    LlvmValue shifted = Builder.CreateBinOp(instruction.Opcode, select.TrueValue, constant);
                    return ShiftResult(new LlvmSelectInstruction(select.Condition, shifted, Builder.CreateBinOp(whenFalse.Opcode, shifted, rhs)), "instcombine.shift.constant.pull_select_false_binary");
                }
            }
            return null;
        }

        private LlvmInstruction? FoldLShrOverflowBit(LlvmBinaryOperator instruction)
        {
            if (instruction.Type.GetScalarSizeInBits() < 3 || instruction.Right is not LlvmConstantInt amount ||
                instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } left, Right: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } right } add) return null;
            ulong bits = amount.Value.GetZExtValue();
            if (bits == 1 || left.Operand.Type.GetScalarSizeInBits() != bits || right.Operand.Type.GetScalarSizeInBits() != bits) return null;
            if (!add.HasOneUse)
                foreach (LlvmUser user in add.Users)
                    if (!ReferenceEquals(user, instruction) && (user is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc || trunc.Type.GetScalarSizeInBits() > bits)) return null;
            Builder.SetInsertPoint(add);
            LlvmValue narrowed = Builder.CreateAdd(left.Operand, right.Operand, "add.narrowed");
            LlvmValue overflow = Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, narrowed, left.Operand, "add.narrowed.overflow");
            if (!add.HasOneUse)
            { ReplaceInstUsesWith(add, Builder.CreateZExt(narrowed, instruction.Type)); EraseInstFromFunction(add); }
            return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, overflow, instruction.Type), "instcombine.shift.lshr_overflow_bit");
        }
    }
}
