// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// visitMul, foldMulSelectToNegate and foldMulShl1 (represented scalar cases).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitMul(LlvmBinaryOperator instruction)
        {
            Count("instcombine.mul.visits");
            if (MdrSimplify(instruction) is { } simple) return MdrReplace(instruction, simple, "instcombine.mul.simplify");
            if (SimplifyAssociativeOrCommutative(instruction)) return instruction;
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            if (FoldUsingDistributiveLaws(instruction) is { } distributed) return MdrReplace(instruction, distributed, "instcombine.mul.distribute");
            var left = instruction.Left; var right = instruction.Right;
            uint width = instruction.Type.GetIntegerBitWidth();
            bool nsw = instruction.HasNoSignedWrap, nuw = instruction.HasNoUnsignedWrap;
            if (MdrAllOnes(right)) return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(instruction, 0), left, nsw: nsw), "instcombine.mul.minus_one");
            if (right is LlvmConstantInt coefficient)
            {
                if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstant shiftConstant } shift &&
                    LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Shl, coefficient, shiftConstant, DL, Metrics) is { } shifted)
                    return MdrRewrite(MdrBin(LlvmOpcode.Mul, shift.Left, shifted, nuw && shift.HasNoUnsignedWrap,
                        nsw && shift.HasNoSignedWrap && shifted is LlvmConstantInt { Value.IsMinSignedValue: false }), "instcombine.mul.combine_constant_shift");
                if (coefficient.Value.IsPowerOf2)
                {
                    uint powerAmount = coefficient.Value.GetActiveBits() - 1;
                    return MdrRewrite(MdrBin(LlvmOpcode.Shl, left, MdrInt(instruction, powerAmount), nuw, nsw && powerAmount != width - 1), "instcombine.mul.power_to_shift");
                }
            }
            if (width > 2 && left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr or LlvmOpcode.AShr, IsExact: true, Right: LlvmConstantInt shrAmount } shr &&
                right is LlvmConstantInt multiplier && (multiplier.Value - new LlvmAPInt(width, 1)).IsPowerOf2 &&
                shrAmount.Value == new LlvmAPInt(width, multiplier.Value.GetActiveBits() - 1))
            {
                LlvmValue operand = shr;
                if (nuw && shr.Opcode == LlvmOpcode.AShr && shr.HasOneUse)
                    operand = Builder.CreateBinary(LlvmOpcode.LShr, shr.Left, shrAmount, exact: true);
                return MdrRewrite(MdrBin(LlvmOpcode.Add, shr.Left, operand, nuw,
                    nsw && (nuw || shr.Opcode == LlvmOpcode.LShr || shrAmount.Value.Ult(new LlvmAPInt(width, width - 1)))), "instcombine.mul.exact_shr_plus_one");
            }
            if (left.HasOneUse && right is LlvmConstantInt negativePower && (-negativePower.Value).IsPowerOf2)
            {
                if (Negator.Negate(true, nsw, left, this) is { } negative)
                    return MdrReplace(instruction, Builder.CreateMul(negative, MdrInt(right, -negativePower.Value), nsw: nsw && !negativePower.Value.IsMinSignedValue), "instcombine.mul.sink_negative_power");
                if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } cast &&
                    negativePower.Value.CountTrailingZeros() >= width - cast.Operand.Type.GetIntegerBitWidth())
                {
                    var neg = Builder.CreateNeg(cast.Operand, cast.Operand.Name + ".neg");
                    var extended = Builder.CreateZExt(neg, instruction.Type);
                    return MdrRewrite(MdrBin(LlvmOpcode.Shl, extended, MdrInt(instruction, negativePower.Value.CountTrailingZeros())), "instcombine.mul.narrow_negate_shift");
                }
            }
            if (FoldBinOpIntoSelectOrPhi(instruction) is { } threaded) return threaded;
            if (FoldBinOpSelectBinOp(instruction) is { } logic) return logic;
            if (FoldMulSelectToNegate(instruction) is { } selectNeg) return MdrReplace(instruction, selectNeg, "instcombine.mul.select_negate");
            if (FoldMulSelectConstants(instruction) is { } selectConstants) return selectConstants;
            if (right is LlvmConstant constant && left is LlvmBinaryOperator { HasOneUse: true, Right: LlvmConstant addend } add &&
                (add.Opcode == LlvmOpcode.Add || add.Opcode == LlvmOpcode.Or && add.IsDisjoint))
            {
                var folded = Builder.CreateMul(addend, constant);
                var product = Builder.CreateMul(add.Left, constant);
                bool preserveNUW = nuw && (add.Opcode == LlvmOpcode.Or || add.HasNoUnsignedWrap);
                if (preserveNUW && product is LlvmBinaryOperator newProduct) newProduct.HasNoUnsignedWrap = true;
                return MdrRewrite(MdrBin(LlvmOpcode.Add, product, folded, preserveNUW), "instcombine.mul.distribute_constant_add");
            }
            if (FoldMulAbs(instruction) is { } abs) return abs;
            if (MdrNegated(left) is { } negatedLeft && right is LlvmConstant negatedConstant &&
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Sub, MdrInt(instruction, 0), negatedConstant, DL, Metrics) is { } constantNegation)
                return MdrRewrite(MdrBin(LlvmOpcode.Mul, negatedLeft, constantNegation), "instcombine.mul.negate_constant");
            var a = MdrNegated(left); var b = MdrNegated(right);
            if (a is not null && b is not null)
                return MdrRewrite(MdrBin(LlvmOpcode.Mul, a, b, nsw: nsw && ((LlvmBinaryOperator)left).HasNoSignedWrap && ((LlvmBinaryOperator)right).HasNoSignedWrap), "instcombine.mul.cancel_negations");
            if (a is not null && left.HasOneUse)
                return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(instruction, 0), Builder.CreateMul(a, right)), "instcombine.mul.sink_negation_left");
            if (b is not null && right.HasOneUse)
                return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(instruction, 0), Builder.CreateMul(b, left)), "instcombine.mul.sink_negation_right");
            if (b is not null && Negator.Negate(false, false, left, this) is { } negativeFactor)
                return MdrRewrite(MdrBin(LlvmOpcode.Mul, negativeFactor, b), "instcombine.mul.cancel_nested_negation");
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.UDiv or LlvmOpcode.SDiv, IsExact: true, HasOneUse: true, Right: LlvmConstantInt divisor } division &&
                right is LlvmConstantInt factor && !factor.IsZero &&
                MdrMultiple(divisor.Value, factor.Value, division.Opcode == LlvmOpcode.SDiv, out var quotient) &&
                (division.Opcode != LlvmOpcode.SDiv || !quotient.IsAllOnes))
                return MdrRewrite(MdrBin(division.Opcode, division.Left, Builder.CreateBinOp(division.Opcode, divisor, factor), exact: true), "instcombine.mul.cancel_exact_divisor");
            if (FoldMulDivisionPair(instruction) is { } divisionPair) return divisionPair;
            if (instruction.Type.IsIntegerTy(1) || MdrIsAndOne(left) && MdrIsAndOne(right))
                return MdrRewrite(MdrBin(LlvmOpcode.And, left, right), "instcombine.mul.boolean_and");
            if (FoldMulShl1(instruction, false) is { } lhsShift) return MdrReplace(instruction, lhsShift, "instcombine.mul.shift_factor_left");
            if (FoldMulShl1(instruction, true) is { } rhsShift) return MdrReplace(instruction, rhsShift, "instcombine.mul.shift_factor_right");
            if (FoldMulBoolean(instruction) is { } boolean) return boolean;
            if (FoldMulSignExtract(instruction) is { } signExtract) return signExtract;
            if (NarrowMathIfNoOverflow(instruction) is { } narrow) return narrow;
            if (FoldBinOpOfSelectAndCastOfSelectCondition(instruction) is { } foldedCondition) return foldedCondition;
            if (TryGetLog2(left) is { } lhsLog) return MdrRewrite(MdrBin(LlvmOpcode.Shl, right, lhsLog, nuw), "instcombine.mul.log2_left");
            if (TryGetLog2(right) is { } rhsLog) return MdrRewrite(MdrBin(LlvmOpcode.Shl, left, rhsLog, nuw), "instcombine.mul.log2_right");
            bool changed = false;
            if (!nsw && WillNotOverflowSignedMul(left, right, instruction)) { instruction.HasNoSignedWrap = true; changed = true; Count("instcombine.mul.infer_nsw"); }
            if (!nuw && WillNotOverflowUnsignedMul(left, right, instruction, instruction.HasNoSignedWrap)) { instruction.HasNoUnsignedWrap = true; changed = true; Count("instcombine.mul.infer_nuw"); }
            return changed ? instruction : null;
        }

        private static bool MdrIsAndOne(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt { IsOne: true } };
        private LlvmValue? FoldMulSelectToNegate(LlvmBinaryOperator instruction)
        {
            return FoldSide(instruction.Left, instruction.Right) ?? FoldSide(instruction.Right, instruction.Left);
            LlvmValue? FoldSide(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmSelectInstruction { HasOneUse: true } select) return null;
                if (instruction.Opcode == LlvmOpcode.Mul)
                {
                    bool trueOne = MdrOne(select.TrueValue) && MdrAllOnes(select.FalseValue);
                    bool falseOne = MdrAllOnes(select.TrueValue) && MdrOne(select.FalseValue);
                    if (!trueOne && !falseOne) return null;
                    var negated = Builder.CreateNeg(other, nsw: instruction.HasNoSignedWrap || instruction.HasNoUnsignedWrap);
                    return Builder.CreateSelect(select.Condition, trueOne ? other : negated, trueOne ? negated : other);
                }
                bool fpTrue = MdrSpecificFP(select.TrueValue, 1) && MdrSpecificFP(select.FalseValue, -1);
                bool fpFalse = MdrSpecificFP(select.TrueValue, -1) && MdrSpecificFP(select.FalseValue, 1);
                if (!fpTrue && !fpFalse) return null;
                var neg = Builder.CreateFNeg(other, fastMathFlags: instruction.FastMathFlags);
                return Builder.CreateSelect(select.Condition, fpTrue ? other : neg, fpTrue ? neg : other, fastMathFlags: instruction.FastMathFlags);
            }
        }
        private LlvmValue? FoldMulShl1(LlvmBinaryOperator instruction, bool commute)
        {
            var x = commute ? instruction.Right : instruction.Left; var y = commute ? instruction.Left : instruction.Right;
            if (y is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } power)
                return Builder.CreateBinary(LlvmOpcode.Shl, x, power.Right, instruction.Name, instruction.HasNoUnsignedWrap, instruction.HasNoSignedWrap && power.HasNoSignedWrap);
            if (y is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt { IsOne: true }, Left: LlvmBinaryOperator
                { Opcode: LlvmOpcode.Shl, HasOneUse: true, Left: LlvmConstantInt { IsOne: true } } shift })
            {
                var frozen = MdrFreeze(x); bool nsw = instruction.HasNoSignedWrap && shift.HasNoSignedWrap;
                return Builder.CreateAdd(Builder.CreateBinary(LlvmOpcode.Shl, frozen, shift.Right, "mulshl", instruction.HasNoUnsignedWrap, nsw), frozen,
                    instruction.Name, instruction.HasNoUnsignedWrap, nsw);
            }
            if (MdrNot(y) is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true, Left: LlvmConstantInt { IsMinusOne: true } } notShift && y.HasOneUse)
            {
                var frozen = MdrFreeze(x);
                return Builder.CreateSub(Builder.CreateBinary(LlvmOpcode.Shl, frozen, notShift.Right, "mulshl"), frozen, instruction.Name);
            }
            return null;
        }
        private static LlvmValue? MdrNot(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } binary ?
            MdrAllOnes(binary.Right) ? binary.Left : MdrAllOnes(binary.Left) ? binary.Right : null : null;
    }
}
