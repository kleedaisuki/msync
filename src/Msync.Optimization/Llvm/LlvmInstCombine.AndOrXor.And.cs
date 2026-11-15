// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp visitAnd.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitAnd(LlvmBinaryOperator i)
        {
            Count("instcombine.logic.and.visited");
            if (LlvmInstructionSimplify.SimplifyInstruction(i, Query(i)) is { } simplified) return ReplaceInstUsesWith(i, simplified);
            if (SimplifyAssociativeOrCommutative(i)) return i;
            if (FoldBinopWithPhiOperands(i) is { } phi) return phi;
            if (SimplifyDemandedInstructionBits(i)) return i;
            if (FoldAndToXor(i) is { } xor) return xor;
            if (FoldComplexAndOrPatterns(i) is { } complex) return complex;
            if (FoldUsingDistributiveLaws(i) is { } distributed) return ReplaceInstUsesWith(i, distributed);
            if (FoldBinOpShiftWithShift(i) is { } shifted) return shifted;
            var op0 = i.Left; var op1 = i.Right; var type = i.Type;
            if (FoldAndInitialPatterns(i) is { } initial) return initial;
            if (op1 is LlvmConstantInt mask && FoldAndConstantMask(i, mask.Value) is { } constant) return constant;
            if (FoldAndFloatingAbs(i) is { } absolute) return absolute;
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true, Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext,
                Right: LlvmConstantInt amount } && op1 is LlvmConstantInt sign && sign.Value == LlvmAPInt.GetSignedMinValue(type.GetScalarSizeInBits())
                && amount.Value == new LlvmAPInt(amount.Value.BitWidth, type.GetScalarSizeInBits() - ext.Operand.Type.GetScalarSizeInBits()))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateSExt(ext.Operand, type, ext.Operand.Name + ".signext"), op1), "and.sign_extract_sext");
            if (NarrowMaskedBinOp(i) is { } narrow) return narrow;
            if (type.IsIntegerTy(1))
            {
                if (op0 is LlvmSelectInstruction s0 && FoldAndOrOfSelectUsingImpliedCond(op1, s0, true) is { } selected0) return selected0;
                if (op1 is LlvmSelectInstruction s1 && FoldAndOrOfSelectUsingImpliedCond(op0, s1, true) is { } selected1) return selected1;
            }
            if (FoldBinOpIntoSelectOrPhi(i) is { } selectPhi) return selectPhi;
            if (MatchDeMorgansLaws(i) is { } deMorgan) return deMorgan;
            if (FoldAndXorOrIdentities(i) is { } identities) return identities;
            if (FoldBooleanAndOr(op0, op1, i, true, false) is { } boolean) return ReplaceInstUsesWith(i, boolean);
            if (op1.HasOneUse && LogicLogical(op1, true, out var x1, out var y1) && ReassociateBooleanAndOr(op0, x1, y1, i, true, op1 is LlvmSelectInstruction) is { } reassociated1) return ReplaceInstUsesWith(i, reassociated1);
            if (op0.HasOneUse && LogicLogical(op0, true, out var x0, out var y0) && ReassociateBooleanAndOr(op1, x0, y0, i, true, op0 is LlvmSelectInstruction) is { } reassociated0) return ReplaceInstUsesWith(i, reassociated0);
            if (ReassociateFCmps(i) is { } floating) return floating;
            if (FoldCastedBitwiseLogic(i) is { } casts) return casts;
            if (FoldBinopOfSextBoolToSelect(i) is { } sextSelect) return sextSelect;
            if (FoldAndBooleanMasks(i) is { } boolMask) return boolMask;
            if (SinkNotIntoOtherHandOfLogicalOp(i)) return i;
            if (LogicRecurrence(i, out var recurrence, out var start, out var step) && DT.Dominates(step, recurrence))
                return LogicReplace(i, Builder.CreateAnd(start, step), "and.recurrence_invariant_step");
            if (ReassociateForUses(i) is { } uses) return uses;
            if (CanonicalizeLogicFirst(i) is { } canonical) return canonical;
            if (FoldLogicOfIsFPClass(i, op0, op1) is { } fpClass) return fpClass;
            if (FoldBinOpOfDisplacedShifts(i) is { } displaced) return displaced;
            if (FoldBitwiseLogicWithIntrinsics(i) is { } intrinsic) return intrinsic;
            if (SimplifyAndOrWithOpReplaced(op0, op1, LogicOnes(type), false) is { } replaced0) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, replaced0, op1), "and.replaced_left");
            if (SimplifyAndOrWithOpReplaced(op1, op0, LogicOnes(type), false) is { } replaced1) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, op0, replaced1), "and.replaced_right");
            return FoldRoundUpToPow2Alignment(i);
        }

        private LlvmInstruction? FoldAndInitialPatterns(LlvmBinaryOperator i)
        {
            var type = i.Type;
            if (i.Right is LlvmConstantInt { IsOne: true } && i.Left is LlvmBinaryOperator { HasOneUse: true, Left: LlvmConstantInt c } shift
                && ((shift.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr && c.IsOne) || (shift.Opcode == LlvmOpcode.Shl && !(c.Value & new LlvmAPInt(c.BitWidth, 1)).IsZero)))
            {
                var isZero = Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, shift.Right, LogicZero(type));
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.ZExt, isZero, type), "and.shift_one_bit_test");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var neg = orientation == 0 ? i.Left : i.Right; var y = orientation == 0 ? i.Right : i.Left;
                if (neg.HasOneUse && LogicNeg(neg, out var masked) && masked is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt { IsOne: true } })
                    return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, neg, LogicZero(type)), LogicZero(type), y, "and.neg_lowbit_to_select");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var y = orientation == 0 ? i.Left : i.Right; var expression = orientation == 0 ? i.Right : i.Left;
                if (!expression.HasOneUse || expression is not LlvmBinaryOperator bin) continue;
                LlvmValue? x = null;
                if (LogicOther(bin, LlvmOpcode.Add, y, out var addend)) x = addend;
                else if (bin.Opcode == LlvmOpcode.Sub && ReferenceEquals(bin.Right, y)) x = bin.Left;
                if (x is not null && LogicPower(y, true, i))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNot(x), y), "and.power2_add_sub");
            }
            return null;
        }

        private LlvmInstruction? FoldAndConstantMask(LlvmBinaryOperator i, LlvmAPInt mask)
        {
            var type = i.Type; var op0 = i.Left; var op1 = i.Right;
            uint width = type.GetScalarSizeInBits();
            if (op0 is LlvmBinaryOperator { HasOneUse: true, Right: LlvmConstantInt c } inner)
            {
                if (inner.Opcode == LlvmOpcode.Xor)
                {
                    var and = Builder.CreateAnd(inner.Left, op1); LogicTakeName(and, op0);
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, and, LogicC(type, mask & c.Value)), "and.xor_constant");
                }
                if (inner.Opcode == LlvmOpcode.Or)
                {
                    var together = mask & c.Value;
                    var and = Builder.CreateAnd(inner.Left, LogicC(type, together ^ mask)); LogicTakeName(and, op0);
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, and, LogicC(type, together)), "and.or_constant");
                }
            }
            if (op0 is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true,
                Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt shiftAmount } ashr }
                && shiftAmount.Value.Ult(new(width, width)) && LogicMask(mask, width - (uint)shiftAmount.GetZExtValue()))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.LShr, Builder.CreateSExt(ashr.Left, type), LogicC(type, shiftAmount.Value.ZExt(width))), "and.sext_ashr_to_lshr");
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt shift } rightShift
                && shift.Value.Ult(new(width, width)) && LogicMask(mask, width - (uint)shift.GetZExtValue()))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.LShr, rightShift.Left, shift), "and.ashr_to_lshr");
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt addC } add
                && mask.IsPowerOf2 && (addC.Value & (mask - new LlvmAPInt(width, 1))).IsZero)
            {
                Debug.Assert(!(mask & addC.Value).IsZero, "Demanded bits must remove the non-flipping add");
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateAnd(add.Left, op1), op1), "and.add_one_bit_to_xor");
            }
            if (FoldAndNarrowArithmetic(i, mask) is { } narrow) return narrow;
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor or LlvmOpcode.Or, HasOneUse: true } logic)
            {
                if (LogicMaskedZero(logic.Left, ~mask, i))
                    return LogicResult(new LlvmBinaryOperator(logic.Opcode, logic.Left, Builder.CreateAnd(logic.Right, op1, logic.Right.Name + ".masked")), "and.push_mask_rhs");
                if (logic.Right is not LlvmConstant && LogicMaskedZero(logic.Right, ~mask, i))
                    return LogicResult(new LlvmBinaryOperator(logic.Opcode, Builder.CreateAnd(logic.Left, op1, logic.Left.Name + ".masked"), logic.Right), "and.push_mask_lhs");
            }
            if (FoldAndShiftedPower(i, mask) is { } power) return power;
            return FoldAndUnsignedClamp(i, mask);
        }

        private LlvmInstruction? FoldAndNarrowArithmetic(LlvmBinaryOperator i, LlvmAPInt mask)
        {
            if (i.Left is not LlvmBinaryOperator { HasOneUse: true, Opcode: LlvmOpcode.Xor or LlvmOpcode.Or or LlvmOpcode.Mul or LlvmOpcode.Add or LlvmOpcode.Sub } bin) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                bool reverse = orientation != 0; var extension = reverse ? bin.Right : bin.Left; var other = reverse ? bin.Left : bin.Right;
                if (extension is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } zext) continue;
                var x = zext.Operand; uint width = x.Type.GetScalarSizeInBits();
                if (other is LlvmConstantInt c && mask.GetActiveBits() <= width)
                {
                    var narrowC = LogicC(x.Type, c.Value.Trunc(width));
                    var operation = reverse ? Builder.CreateBinary(bin.Opcode, narrowC, x) : Builder.CreateBinary(bin.Opcode, x, narrowC);
                    var and = Builder.CreateAnd(operation, LogicC(x.Type, mask.Trunc(width)));
                    return LogicResult(new LlvmCastInstruction(LlvmOpcode.ZExt, and, i.Type), "and.narrow_constant_arithmetic");
                }
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                bool reverse = orientation != 0; var extension = reverse ? bin.Right : bin.Left; var other = reverse ? bin.Left : bin.Right;
                if (extension is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } zext || !LogicMask(mask, zext.Operand.Type.GetScalarSizeInBits())) continue;
                var narrowOther = Builder.CreateTrunc(other, zext.Operand.Type, other.Name + ".tr");
                var operation = reverse ? Builder.CreateBinary(bin.Opcode, narrowOther, zext.Operand, bin.Name + ".narrow") : Builder.CreateBinary(bin.Opcode, zext.Operand, narrowOther, bin.Name + ".narrow");
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.ZExt, operation, i.Type), "and.narrow_variable_arithmetic");
            }
            return null;
        }
    }
}
