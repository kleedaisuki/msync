// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp
// canEvaluateShiftedShift / canEvaluateShifted / foldShiftedShift / getShiftedValue.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool CanEvaluateShiftedShift(uint outerAmount, bool outerLeft, ShiftSemantics semantics,
            LlvmBinaryOperator inner, LlvmInstruction context)
        {
            if (inner.Right is not LlvmConstantInt constant) return false;
            bool innerLeft = inner.Opcode == LlvmOpcode.Shl;
            if (!outerLeft && semantics == ShiftSemantics.Signed)
                return innerLeft && inner.HasNoSignedWrap && constant.Value.GetLimitedValue(outerAmount + 1UL) == outerAmount;
            if (innerLeft == outerLeft) return semantics == ShiftSemantics.Lossy;
            if (constant.Value.GetLimitedValue(outerAmount + 1UL) == outerAmount) return true;
            uint width = inner.Type.GetScalarSizeInBits();
            ulong amount = constant.Value.GetLimitedValue(width);
            if (amount <= outerAmount || amount >= width) return false;
            uint maskShift = innerLeft ? width - (uint)amount : (uint)amount - outerAmount;
            LlvmAPInt mask = LlvmAPInt.GetLowBitsSet(width, outerAmount).Shl(maskShift);
            return ShiftMaskedZero(inner.Left, mask, context);
        }

        private bool CanEvaluateShifted(LlvmValue value, uint bits, bool left, ShiftSemantics semantics, LlvmInstruction context)
        {
            Count("instcombine.shift.evaluate.visits");
            if (value is LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue)
            {
                if (semantics == ShiftSemantics.Lossy) return true;
                return !left && value is LlvmConstantInt constant && constant.Value.CountTrailingZeros() >= bits;
            }
            if (value is not LlvmInstruction instruction || !instruction.HasOneUse) return false;
            switch (instruction)
            {
                case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor:
                    return CanEvaluateShifted(binary.Left, bits, left, semantics, binary) && CanEvaluateShifted(binary.Right, bits, left, semantics, binary);
                case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr:
                    return CanEvaluateShiftedShift(bits, left, semantics, binary, context);
                case LlvmSelectInstruction select:
                    return CanEvaluateShifted(select.TrueValue, bits, left, semantics, select) && CanEvaluateShifted(select.FalseValue, bits, left, semantics, select);
                case LlvmPhiInstruction phi:
                    for (int index = 0; index < phi.IncomingCount; ++index)
                        if (!CanEvaluateShifted(phi.GetIncomingValue(index), bits, left, semantics, phi)) return false;
                    return true;
                case LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt constant }:
                    return !left && semantics == ShiftSemantics.Unsigned && (-constant.Value).IsPowerOf2 && constant.Value.CountTrailingZeros() == bits;
                case LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add:
                    if (left && semantics != ShiftSemantics.Lossy) return false;
                    if (!left && (semantics == ShiftSemantics.Lossy || !(semantics == ShiftSemantics.Signed ? add.HasNoSignedWrap : add.HasNoUnsignedWrap))) return false;
                    return CanEvaluateShifted(add.Left, bits, left, semantics, add) && CanEvaluateShifted(add.Right, bits, left, semantics, add);
                default: return false;
            }
        }

        private LlvmValue FoldShiftedShift(LlvmBinaryOperator inner, uint outerAmount, bool outerLeft, ShiftSemantics semantics)
        {
            bool innerLeft = inner.Opcode == LlvmOpcode.Shl;
            uint width = inner.Type.GetScalarSizeInBits();
            uint innerAmount = (uint)((LlvmConstantInt)inner.Right).Value.GetZExtValue();
            LlvmValue NewInnerShift(uint amount)
            {
                inner.Right = ShiftInt(inner.Type, amount);
                if (innerLeft) { inner.HasNoUnsignedWrap = false; inner.HasNoSignedWrap = false; }
                else inner.IsExact = false;
                Count("instcombine.shift.evaluate.adjust_inner");
                return inner;
            }
            if (innerLeft == outerLeft)
                return innerAmount + outerAmount >= width ? LlvmConstant.GetNullValue(inner.Type) : NewInnerShift(innerAmount + outerAmount);
            if (innerAmount != outerAmount) return NewInnerShift(innerAmount - outerAmount);
            if (!outerLeft && semantics == ShiftSemantics.Signed) return inner.Left;
            if (!outerLeft && semantics == ShiftSemantics.Unsigned && innerLeft && inner.HasNoUnsignedWrap) return inner.Left;
            LlvmAPInt mask = innerLeft ? LlvmAPInt.GetLowBitsSet(width, width - outerAmount) : LlvmAPInt.GetHighBitsSet(width, width - outerAmount);
            LlvmValue result = Builder.CreateAnd(inner.Left, ShiftInt(inner.Type, mask));
            if (result is LlvmInstruction instruction)
            {
                instruction.RemoveFromParent();
                inner.Parent!.InsertBefore(instruction, inner);
                instruction.TakeName(inner);
            }
            Count("instcombine.shift.evaluate.opposite_mask");
            return result;
        }

        private LlvmValue GetShiftedValue(LlvmValue value, uint bits, bool left, ShiftSemantics semantics)
        {
            Count("instcombine.shift.evaluate.rewrites");
            if (value is LlvmConstant)
                return Builder.CreateBinOp(left ? LlvmOpcode.Shl : semantics == ShiftSemantics.Signed ? LlvmOpcode.AShr : LlvmOpcode.LShr, value, ShiftInt(value.Type, bits));
            LlvmInstruction instruction = (LlvmInstruction)value;
            Worklist.Add(instruction);
            switch (instruction)
            {
                case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor:
                    binary.Left = GetShiftedValue(binary.Left, bits, left, semantics);
                    binary.Right = GetShiftedValue(binary.Right, bits, left, semantics);
                    return binary;
                case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr:
                    return FoldShiftedShift(binary, bits, left, semantics);
                case LlvmSelectInstruction select:
                    select.TrueValue = GetShiftedValue(select.TrueValue, bits, left, semantics);
                    select.FalseValue = GetShiftedValue(select.FalseValue, bits, left, semantics);
                    return select;
                case LlvmPhiInstruction phi:
                    for (int index = 0; index < phi.IncomingCount; ++index)
                        phi.SetIncomingValue(index, GetShiftedValue(phi.GetIncomingValue(index), bits, left, semantics));
                    return phi;
                case LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } multiply:
                    var negation = new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstant.GetNullValue(value.Type), multiply.Left);
                    InsertNewInstWith(negation, instruction);
                    var masked = new LlvmBinaryOperator(LlvmOpcode.And, negation, ShiftInt(value.Type, LlvmAPInt.GetLowBitsSet(value.Type.GetScalarSizeInBits(), value.Type.GetScalarSizeInBits() - bits)));
                    masked.TakeName(instruction);
                    return InsertNewInstWith(masked, instruction);
                case LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add:
                    if (left) { add.HasNoUnsignedWrap = false; add.HasNoSignedWrap = false; }
                    add.Left = GetShiftedValue(add.Left, bits, left, semantics);
                    add.Right = GetShiftedValue(add.Right, bits, left, semantics);
                    return add;
                default: throw new UnreachableException("GetShiftedValue must follow successful CanEvaluateShifted");
            }
        }
    }
}
