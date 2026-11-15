// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// simplifyIRemMulShl, commonIRemTransforms, visitURem and visitSRem.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? CommonIRemTransforms(LlvmBinaryOperator instruction)
        {
            if (CommonIDivRemTransforms(instruction) is { } common) return common;
            if (instruction.Right is LlvmConstant)
            {
                if (instruction.Left is LlvmSelectInstruction select && FoldOpIntoSelect(instruction, select) is { } threaded) return threaded;
                if (instruction.Left is LlvmPhiInstruction phi && instruction.Right is LlvmConstantInt { IsZero: false } divisor &&
                    (instruction.Opcode == LlvmOpcode.URem || !divisor.Value.IsMinSignedValue) && FoldOpIntoPhi(instruction, phi) is { } folded) return folded;
                if (instruction.Left is LlvmInstruction && SimplifyDemandedInstructionBits(instruction)) return instruction;
            }
            return SimplifyIRemMulShl(instruction);
        }

        private LlvmInstruction? SimplifyIRemMulShl(LlvmBinaryOperator instruction)
        {
            LlvmValue? variable = null;
            bool lhsPreserves = true, rhsPreserves = true, shiftByVariable = false;
            LlvmAPInt a, b;
            if (MdrMatchShiftOrMulConstant(instruction.Left, ref variable, out a, ref lhsPreserves) &&
                MdrMatchShiftOrMulConstant(instruction.Right, ref variable, out b, ref rhsPreserves)) { }
            else if (MdrMatchShiftConstant(instruction.Left, ref variable, out a) && MdrMatchShiftConstant(instruction.Right, ref variable, out b)) shiftByVariable = true;
            else return null;
            var lhs = (LlvmBinaryOperator)instruction.Left; var rhs = (LlvmBinaryOperator)instruction.Right;
            bool signed = instruction.Opcode == LlvmOpcode.SRem;
            bool lhsNSW = lhsPreserves && lhs.HasNoSignedWrap, rhsNSW = rhsPreserves && rhs.HasNoSignedWrap;
            bool lhsNoWrap = signed ? lhsNSW : lhs.HasNoUnsignedWrap, rhsNoWrap = signed ? rhsNSW : rhs.HasNoUnsignedWrap;
            var remainder = signed ? a.SRem(b) : a.URem(b);
            if (remainder.IsZero && lhsNoWrap) return MdrReplace(instruction, MdrInt(instruction, 0), "instcombine.irem.multiple_constants");
            LlvmBinaryOperator Build(LlvmAPInt value) => shiftByVariable ? MdrBin(LlvmOpcode.Shl, MdrInt(instruction, value), variable!) : MdrBin(LlvmOpcode.Mul, variable!, MdrInt(instruction, value));
            if (remainder == a && rhsNoWrap)
            {
                var result = Build(a); result.HasNoSignedWrap = signed || lhsNSW; result.HasNoUnsignedWrap = !signed || lhs.HasNoUnsignedWrap;
                return MdrRewrite(result, "instcombine.irem.divisor_multiple");
            }
            if (a.Uge(b) && (signed ? lhsNSW && rhsNSW : lhs.HasNoUnsignedWrap))
            {
                var result = Build(remainder); result.HasNoSignedWrap = true; result.HasNoUnsignedWrap = lhs.HasNoUnsignedWrap;
                return MdrRewrite(result, "instcombine.irem.factor_constants");
            }
            return null;
        }
        private static bool MdrMatchShiftOrMulConstant(LlvmValue value, ref LlvmValue? variable, out LlvmAPInt constant, ref bool preserveNSW)
        {
            constant = default;
            if (value is LlvmBinaryOperator { Right: LlvmConstantInt rhs } binary &&
                (binary.Opcode == LlvmOpcode.Mul || binary.Opcode == LlvmOpcode.Shl) &&
                (variable is null || ReferenceEquals(variable, binary.Left)))
            {
                variable = binary.Left;
                constant = binary.Opcode == LlvmOpcode.Mul ? rhs.Value : new LlvmAPInt(rhs.Value.BitWidth, 1).Shl(rhs.Value);
                if (binary.Opcode == LlvmOpcode.Shl) preserveNSW = rhs.Value.Ult(new LlvmAPInt(rhs.Value.BitWidth, rhs.Value.BitWidth - 1));
                return true;
            }
            variable = null; return false;
        }
        private static bool MdrMatchShiftConstant(LlvmValue value, ref LlvmValue? variable, out LlvmAPInt constant)
        {
            constant = default;
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt lhs } binary &&
                (variable is null || ReferenceEquals(variable, binary.Right)))
            { variable = binary.Right; constant = lhs.Value; return true; }
            variable = null; return false;
        }

        private LlvmInstruction? VisitURem(LlvmBinaryOperator instruction)
        {
            Count("instcombine.urem.visits");
            if (MdrSimplify(instruction) is { } simplified) return MdrReplace(instruction, simplified, "instcombine.urem.simplify");
            if (CommonIRemTransforms(instruction) is { } common) return common;
            if (NarrowUDivURem(instruction) is { } narrow) return narrow;
            var left = instruction.Left; var right = instruction.Right;
            uint width = instruction.Type.GetIntegerBitWidth();
            if (MdrPower(right, true, instruction))
                return MdrRewrite(MdrBin(LlvmOpcode.And, left, Builder.CreateAdd(right, MdrInt(instruction, LlvmAPInt.GetAllOnes(width)))), "instcombine.urem.power_to_mask");
            if (MdrOne(left))
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, right, MdrInt(instruction, 1)), instruction.Type), "instcombine.urem.one_dividend");
            if (right is LlvmConstantInt { IsNegative: true })
            {
                var frozen = MdrFreeze(left);
                var cmp = Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, frozen, right);
                return MdrReplace(instruction, Builder.CreateSelect(cmp, frozen, Builder.CreateSub(frozen, right)), "instcombine.urem.large_constant_divisor");
            }
            if (right is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext && sext.Operand.Type.IsIntegerTy(1))
            {
                var frozen = MdrFreeze(left);
                var cmp = Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, frozen, MdrInt(instruction, LlvmAPInt.GetAllOnes(width)));
                return MdrReplace(instruction, Builder.CreateSelect(cmp, MdrInt(instruction, 0), frozen), "instcombine.urem.sext_boolean_divisor");
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsOne: true } } add &&
                LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpUlt, add.Left, right, DL, instruction, DT, Metrics, assumptions: AC, allowEphemerals: SQ.AllowEphemerals) is LlvmConstantInt { IsOne: true })
            {
                var frozen = MdrFreeze(left);
                var cmp = Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, frozen, right);
                return MdrReplace(instruction, Builder.CreateSelect(cmp, MdrInt(instruction, 0), frozen), "instcombine.urem.increment_wrap");
            }
            return null;
        }

        private LlvmInstruction? VisitSRem(LlvmBinaryOperator instruction)
        {
            Count("instcombine.srem.visits");
            if (MdrSimplify(instruction) is { } simplified) return MdrReplace(instruction, simplified, "instcombine.srem.simplify");
            if (CommonIRemTransforms(instruction) is { } common) return common;
            var left = instruction.Left; var right = instruction.Right;
            if (right is LlvmConstantInt { IsNegative: true } constant && !constant.Value.IsMinSignedValue)
                return MdrRewrite(ReplaceOperand(instruction, 1, MdrInt(right, -constant.Value)), "instcombine.srem.positive_divisor");
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true, HasOneUse: true, Left: LlvmConstantInt { IsZero: true } } neg)
                return MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(instruction, 0), Builder.CreateBinary(LlvmOpcode.SRem, neg.Right, right), nsw: true), "instcombine.srem.sink_negation");
            var sign = LlvmAPInt.GetSignedMinValue(instruction.Type.GetIntegerBitWidth());
            if (MaskedValueIsZero(right, sign, instruction) && MaskedValueIsZero(left, sign, instruction))
                return MdrRewrite(MdrBin(LlvmOpcode.URem, left, right), "instcombine.srem.nonnegative_to_unsigned");
            return null;
        }
    }
}
