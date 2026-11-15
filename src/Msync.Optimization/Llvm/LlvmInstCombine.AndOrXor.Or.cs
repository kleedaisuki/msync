// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp visitOr.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitOr(LlvmBinaryOperator i)
        {
            Count("instcombine.logic.or.visited");
            if (LlvmInstructionSimplify.SimplifyInstruction(i, Query(i)) is { } simplified) return ReplaceInstUsesWith(i, simplified);
            if (SimplifyAssociativeOrCommutative(i)) return i;
            if (FoldBinopWithPhiOperands(i) is { } phi) return phi;
            if (SimplifyDemandedInstructionBits(i)) return i;
            if (FoldOrToXor(i) is { } xor) return xor;
            if (FoldComplexAndOrPatterns(i) is { } complex) return complex;
            if (FoldIntegerPackFromVector(i) is { } pack) return pack;
            if (FoldOrOfInversions(i) is { } inverse) return ReplaceInstUsesWith(i, inverse);
            if (FoldUsingDistributiveLaws(i) is { } distributed) return ReplaceInstUsesWith(i, distributed);
            var op0 = i.Left; var op1 = i.Right; var type = i.Type;
            if (type.IsIntegerTy(1))
            {
                if (op0 is LlvmSelectInstruction s0 && FoldAndOrOfSelectUsingImpliedCond(op1, s0, false) is { } selected0) return selected0;
                if (op1 is LlvmSelectInstruction s1 && FoldAndOrOfSelectUsingImpliedCond(op0, s1, false) is { } selected1) return selected1;
            }
            if (FoldBinOpIntoSelectOrPhi(i) is { } selectPhi) return selectPhi;
            if (FoldBinOpSelectBinOp(i) is { } selectBin) return selectBin;
            if (MatchBSwapOrBitReverse(i, true, true) is { } swap) return swap;
            if (MatchFunnelShift(i) is { } funnel) return funnel;
            if (MatchOrConcat(i) is { } concat) return ReplaceInstUsesWith(i, concat);
            if (FoldBinOpShiftWithShift(i) is { } shifted) return shifted;
            if (TryFoldInstWithCtpopWithNot(i) is { } ctpop) return ctpop;
            if (i.IsDisjoint)
            {
                if (FoldAddLikeCommutative(op0, op1, true, true) is { } add0) return add0;
                if (FoldAddLikeCommutative(op1, op0, true, true) is { } add1) return add1;
                if (FoldDisjointOr(op0, op1) is { } disjoint) return ReplaceInstUsesWith(i, disjoint);
                if (ReassociateDisjointOr(op0, op1) is { } reassociated) return ReplaceInstUsesWith(i, reassociated);
            }
            if (FoldOrInitialPatterns(i) is { } initial) return initial;
            if (type.IsIntegerTy(1) && (op0.HasOneUse || op1.HasOneUse) && FoldOrOfLogicalAnds(op0, op1) is { } logical) return logical;
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and0 && op1 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and1)
            {
                if (and0.Right is LlvmConstantInt c0 && and1.Right is LlvmConstantInt c1 && FoldOrMaskedConstants(i, and0.Left, c0.Value, and1.Left, c1.Value) is { } masks) return masks;
                if (op0.HasOneUse || op1.HasOneUse)
                {
                    for (int orientation = 0; orientation < 2; ++orientation)
                    {
                        var left = orientation == 0 ? and0 : and1; var right = orientation == 0 ? and1 : and0;
                        if (TryAndOrSelectPermutation(left.Left, left.Right, right.Left, right.Right, out var select)) return ReplaceInstUsesWith(i, select);
                    }
                }
            }
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && LogicNot(op1, out var not) && not is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or
                && (op0.HasOneUse || op1.HasOneUse))
            {
                for (int order = 0; order < 4; ++order)
                {
                    var a = (order & 2) == 0 ? and.Left : and.Right; var c = (order & 2) == 0 ? and.Right : and.Left;
                    var b = (order & 1) == 0 ? or.Left : or.Right; var d = (order & 1) == 0 ? or.Right : or.Left;
                    if (MatchSelectFromAndOr(a, c, b, d, true) is { } select) return ReplaceInstUsesWith(i, select);
                }
            }
            if (FoldOrXorChains(i) is { } chain) return chain;
            if (MatchDeMorgansLaws(i) is { } deMorgan) return deMorgan;
            if (FoldOrXorIdentities(i) is { } identities) return identities;
            if (FoldBooleanAndOr(op0, op1, i, false, false) is { } boolean) return ReplaceInstUsesWith(i, boolean);
            if (op1.HasOneUse && LogicLogical(op1, false, out var x1, out var y1) && ReassociateBooleanAndOr(op0, x1, y1, i, false, op1 is LlvmSelectInstruction) is { } r1) return ReplaceInstUsesWith(i, r1);
            if (op0.HasOneUse && LogicLogical(op0, false, out var x0, out var y0) && ReassociateBooleanAndOr(op1, x0, y0, i, false, op0 is LlvmSelectInstruction) is { } r0) return ReplaceInstUsesWith(i, r0);
            if (ReassociateFCmps(i) is { } floating) return floating;
            if (FoldCastedBitwiseLogic(i) is { } casts) return casts;
            if (FoldBinopOfSextBoolToSelect(i) is { } sextSelect) return sextSelect;
            if (FoldOrMiddlePatterns(i) is { } middle) return middle;
            if (CanonicalizeCondSignextOfHighBitExtractToSignextHighBitExtract(i) is { } sign) return sign;
            if (FoldOrOverflowPatterns(i) is { } overflow) return overflow;
            if (FoldOrUnsignedUMulOverflowICmp(i) is { } unsignedOverflow) return ReplaceInstUsesWith(i, unsignedOverflow);
            if (SinkNotIntoOtherHandOfLogicalOp(i)) return i;
            if (FoldOrLowMask(i) is { } lowMask) return lowMask;
            if (LogicRecurrence(i, out var recurrence, out var start, out var step) && DT.Dominates(step, recurrence))
                return LogicReplace(i, Builder.CreateOr(start, step), "or.recurrence_invariant_step");
            if (FoldOrSharedAndTree(i) is { } commonAnd) return commonAnd;
            if (ReassociateForUses(i) is { } uses) return uses;
            if (CanonicalizeLogicFirst(i) is { } canonical) return canonical;
            if (FoldLogicOfIsFPClass(i, op0, op1) is { } fpClass) return fpClass;
            if (FoldBinOpOfDisplacedShifts(i) is { } displaced) return displaced;
            if (FoldOrFloatingSign(i) is { } fpSign) return fpSign;
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Right: LlvmConstantInt c1mask } inner && op1 is LlvmConstantInt c2mask
                && (LogicKnown(inner.Left, i).One & c2mask.Value) == c2mask.Value)
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, inner.Left, LogicC(type, c1mask.Value | c2mask.Value)), "or.known_bits_and_mask");
            if (FoldBitwiseLogicWithIntrinsics(i) is { } intrinsic) return intrinsic;
            if (SimplifyAndOrWithOpReplaced(op0, op1, LogicZero(type), false) is { } replaced0) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, replaced0, op1), "or.replaced_left");
            if (SimplifyAndOrWithOpReplaced(op1, op0, LogicZero(type), false) is { } replaced1) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, op0, replaced1), "or.replaced_right");
            if (i.IsDisjoint && SimplifyAddWithRemainder(i) is { } remainder) return ReplaceInstUsesWith(i, remainder);
            if (FoldOrOfSelectSmaxToAbs(i) is { } abs) return ReplaceInstUsesWith(i, abs);
            return FoldOrSignum(i);
        }
        private LlvmValue? FoldOrOfInversions(LlvmBinaryOperator i)
        {
            if (i.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } a || i.Right is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } b) return null;
            if (LlvmValueTracking.IsKnownInversion(a.Left, b.Left) && LlvmValueTracking.IsKnownInversion(a.Right, b.Right)) return Builder.CreateXor(a.Left, b.Right);
            if (LlvmValueTracking.IsKnownInversion(a.Left, b.Right) && LlvmValueTracking.IsKnownInversion(a.Right, b.Left)) return Builder.CreateXor(a.Left, b.Left);
            return null;
        }
        private bool TryAndOrSelectPermutation(LlvmValue a, LlvmValue c, LlvmValue b, LlvmValue d, out LlvmValue result)
        {
            for (int order = 0; order < 4; ++order)
            {
                var l0 = (order & 2) == 0 ? a : c; var l1 = (order & 2) == 0 ? c : a;
                var r0 = (order & 1) == 0 ? b : d; var r1 = (order & 1) == 0 ? d : b;
                if (MatchSelectFromAndOr(l0, l1, r0, r1) is not { } select) continue;
                result = select; return true;
            }
            result = null!; return false;
        }
        private LlvmInstruction? FoldOrInitialPatterns(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true, Right: LlvmConstantInt c } xor && !c.IsMinusOne && LogicMaskedZero(rhs, c.Value, i))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateOr(xor.Left, rhs), c), "or.xor_constant_masked");
            }
            if (i.IsDisjoint)
                for (int orientation = 0; orientation < 2; ++orientation)
                {
                    var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                    if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true } mul && ReferenceEquals(rhs, mul.Left))
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Mul, mul.Left, Builder.CreateAdd(mul.Right, LogicC(i.Type, 1))), "or.disjoint_mul_increment");
                }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs.HasOneUse && rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt { IsMinusOne: true } } add && LogicNot(lhs, out var x) && ReferenceEquals(add.Left, x))
                    return LogicResult(LogicNotInstruction(Builder.CreateAnd(x, Builder.CreateNeg(x))), "or.lowest_set_bit");
            }
            return null;
        }
    }
}
