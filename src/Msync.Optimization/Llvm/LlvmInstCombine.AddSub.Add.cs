// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp visitAdd.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitAdd(LlvmBinaryOperator instruction)
        {
            Count("instcombine.add.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simple) return ReplaceInstUsesWith(instruction, simple);
            if (SimplifyAssociativeOrCommutative(instruction)) return instruction;
            if (FoldVectorBinop(instruction) is { } vector) return RecordAddSubRewrite(vector, "vector");
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return RecordAddSubRewrite(phi, "phi");
            if (FoldUsingDistributiveLaws(instruction) is { } distributed) return ReplaceInstUsesWith(instruction, distributed);
            if (FoldBoxMultiply(instruction) is { } box) return RecordAddSubRewrite(box, "box");
            if (FactorizeMathWithShlOps(instruction) is { } factor) return RecordAddSubRewrite(factor, "factor");
            if (FoldAddWithConstant(instruction) is { } constant) return RecordAddSubRewrite(constant, "constant");
            if (FoldNoWrapAdd(instruction) is { } noWrap) return RecordAddSubRewrite(noWrap, "noWrap");
            if (FoldBinOpShiftWithShift(instruction) is { } shifts) return RecordAddSubRewrite(shifts, "shifts");
            if (CombineAddSubWithShlAddSub(instruction) is { } shiftedNegation) return RecordAddSubRewrite(shiftedNegation, "shiftedNegation");
            LlvmValue left = instruction.Left, right = instruction.Right;
            bool nsw = instruction.HasNoSignedWrap, nuw = instruction.HasNoUnsignedWrap;
            if (FoldAddLikeCommutative(left, right, nsw, nuw) is { } commuted) return RecordAddSubRewrite(commuted, "commuted");
            if (FoldAddLikeCommutative(right, left, nsw, nuw) is { } commuted2) return RecordAddSubRewrite(commuted2, "commuted2");
            if (instruction.Type.IsIntOrIntVectorTy(1)) return new LlvmBinaryOperator(LlvmOpcode.Xor, left, right);
            if (ReferenceEquals(left, right)) return new LlvmBinaryOperator(LlvmOpcode.Shl, left, AddSubInt(instruction, 1)) { HasNoSignedWrap = nsw, HasNoUnsignedWrap = nuw };
            if (AddSubNegated(left) is { } a)
            {
                if (AddSubNegated(right) is { } b) return new LlvmBinaryOperator(LlvmOpcode.Sub, AddSubInt(instruction, 0), Builder.CreateAdd(a, b));
                return new LlvmBinaryOperator(LlvmOpcode.Sub, right, a) { HasNoSignedWrap = nsw && ((LlvmBinaryOperator)left).HasNoSignedWrap };
            }
            if (AddSubNegated(right) is { } negative) return new LlvmBinaryOperator(LlvmOpcode.Sub, left, negative) { HasNoSignedWrap = nsw && ((LlvmBinaryOperator)right).HasNoSignedWrap };
            if (CheckForNegativeOperand(instruction) is { } disguise) return ReplaceInstUsesWith(instruction, disguise);
            if (FoldAddSubNotOffset(left, right, instruction, true) is { } notOne) return RecordAddSubRewrite(notOne, "notOne");
            if (FoldAddSubNotOffset(left, right, instruction, false) is { } notConstant) return RecordAddSubRewrite(notConstant, "notConstant");
            if (left.HasOneUse && MatchAddSubCommuted(left, LlvmOpcode.Add, right, out var repeatedLeft))
                return new LlvmBinaryOperator(LlvmOpcode.Add, repeatedLeft, Builder.CreateBinary(LlvmOpcode.Shl, right, AddSubInt(instruction, 1)));
            if (right.HasOneUse && MatchAddSubCommuted(right, LlvmOpcode.Add, left, out var repeatedRight))
                return new LlvmBinaryOperator(LlvmOpcode.Add, repeatedRight, Builder.CreateBinary(LlvmOpcode.Shl, left, AddSubInt(instruction, 1)));
            if (FoldAddSubSeparatedConstants(left, right, instruction) is { } separated) return RecordAddSubRewrite(separated, "separated");
            if (SimplifyAddWithRemainder(instruction) is { } remainder) return ReplaceInstUsesWith(instruction, remainder);
            if (FoldAddSubAndSignBit(left, right) is { } signMask) return RecordAddSubRewrite(signMask, "signMask");
            if (FoldAddSubAndSignBit(right, left) is { } signMask2) return RecordAddSubRewrite(signMask2, "signMask2");
            if (FoldAddSubExtensions(left, right, instruction) is { } extensions) return RecordAddSubRewrite(extensions, "extensions");
            var leftCache = new LlvmValueWithCache(left); var rightCache = new LlvmValueWithCache(right);
            var query = Query(instruction);
            var common = LlvmValueTracking.GetNoCommonBitsSetResult(ref leftCache, ref rightCache, query);
            if (common != LlvmNoCommonBitsSetResult.Unknown) return new LlvmBinaryOperator(LlvmOpcode.Or, left, right) { IsDisjoint = common == LlvmNoCommonBitsSetResult.Known };
            if (NarrowMathIfNoOverflow(instruction) is { } narrowed) return RecordAddSubRewrite(narrowed, "narrowed");
            if (FoldAddSubLogicPair(left, right, instruction) is { } logic) return RecordAddSubRewrite(logic, "logic");
            if (FoldAddSubNegatedOr(left, right, instruction) is { } negatedOr) return RecordAddSubRewrite(negatedOr, "negatedOr");
            if (FoldAddSubNegatedOr(right, left, instruction) is { } negatedOr2) return RecordAddSubRewrite(negatedOr2, "negatedOr2");
            if (FoldAddSubAlignUp(left, right, instruction) is { } aligned) return RecordAddSubRewrite(aligned, "aligned");
            if (FoldAddSubLowBit(instruction) is { } lowBit) return RecordAddSubRewrite(lowBit, "lowBit");
            if (FoldAddSubNotProduct(left, right, instruction) is { } notProduct) return RecordAddSubRewrite(notProduct, "notProduct");
            if (FoldAddSubNotProduct(right, left, instruction) is { } notProduct2) return RecordAddSubRewrite(notProduct2, "notProduct2");
            if (FoldAddSubNegativePowerProduct(left, right, instruction) is { } powerProduct) return RecordAddSubRewrite(powerProduct, "powerProduct");
            if (FoldAddSubNegativePowerProduct(right, left, instruction) is { } powerProduct2) return RecordAddSubRewrite(powerProduct2, "powerProduct2");
            if (FoldAddSubSignum(instruction) is { } signum) return RecordAddSubRewrite(signum, "signum");
            if (FoldAddSubConditionalIncrement(left, right, instruction) is { } conditional) return RecordAddSubRewrite(conditional, "conditional");
            if (FoldAddSubConditionalIncrement(right, left, instruction) is { } conditional2) return RecordAddSubRewrite(conditional2, "conditional2");
            if (FoldAddSubToUMax(instruction) is { } maximum) return RecordAddSubRewrite(maximum, "maximum");
            if (FoldAddToAshr(instruction) is { } arithmeticShift) return RecordAddSubRewrite(arithmeticShift, "arithmeticShift");
            if (FoldAddSubShiftCeiling(left, right, instruction) is { } ceiling) return RecordAddSubRewrite(ceiling, "ceiling");
            if (FoldAddSubShiftCeiling(right, left, instruction) is { } ceiling2) return RecordAddSubRewrite(ceiling2, "ceiling2");
            if (IsFreeToInvert(left, left.HasOneUse, out bool consumesLeft) && consumesLeft && IsFreeToInvert(right, right.HasOneUse, out bool consumesRight) && consumesRight)
                return new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantInt.Get(instruction.Context, new LlvmAPInt(instruction.Type.GetScalarSizeInBits(), unchecked((ulong)-2L), isSigned: true)),
                    Builder.CreateAdd(GetFreelyInverted(left, left.HasOneUse, Builder)!, GetFreelyInverted(right, right.HasOneUse, Builder)!));
            if (TryFoldInstWithCtpopWithNot(instruction) is { } popNot) return RecordAddSubRewrite(popNot, "popNot");
            bool changed = false;
            if (!nsw && WillNotOverflowSignedAdd(left, right, instruction)) { instruction.HasNoSignedWrap = true; changed = true; Count("instcombine.add.infer_nsw"); }
            if (!nuw && WillNotOverflowUnsignedAdd(left, right, instruction)) { instruction.HasNoUnsignedWrap = true; changed = true; Count("instcombine.add.infer_nuw"); }
            if (CanonicalizeLowbitMask(instruction) is { } lowMask) return RecordAddSubRewrite(lowMask, "lowMask");
            if (CanonicalizeCondSignextOfHighBitExtractToSignextHighBitExtract(instruction) is { } signExtract) return RecordAddSubRewrite(signExtract, "signExtract");
            if (FoldToUnsignedSaturatedAdd(instruction) is { } saturated) return RecordAddSubRewrite(saturated, "saturated");
            if (FoldAddSubSaturationAndPopcount(left, right, instruction) is { } count) return RecordAddSubRewrite(count, "count");
            if (FoldAddSubCeilLog2(left, right, instruction) is { } log) return RecordAddSubRewrite(log, "log");
            if (FoldAddSubCeilLog2(right, left, instruction) is { } log2) return RecordAddSubRewrite(log2, "log2");
            if (FoldSquareSumInt(instruction) is { } squares) return RecordAddSubRewrite(squares, "squares");
            if (FoldBinOpOfDisplacedShifts(instruction) is { } displaced) return RecordAddSubRewrite(displaced, "displaced");
            if (FoldBinOpOfSelectAndCastOfSelectCondition(instruction) is { } selectCast) return RecordAddSubRewrite(selectCast, "selectCast");
            if (FoldDivCeil(instruction) is { } divCeil) return RecordAddSubRewrite(divCeil, "divCeil");
            if (FoldAddSubOrNegativeTwo(left, right, instruction) is { } negativeTwo) return RecordAddSubRewrite(negativeTwo, "negativeTwo");
            if (FoldAddSubOrNegativeTwo(right, left, instruction) is { } negativeTwo2) return RecordAddSubRewrite(negativeTwo2, "negativeTwo2");
            if (changed && LlvmValueTracking.MatchSimpleRecurrence(instruction, out var recurrence, out _, out _)) Worklist.PushUsersToWorkList(recurrence);
            return changed ? instruction : null;
        }
    }
}
