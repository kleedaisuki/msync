// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldAndOrOfICmps and foldBooleanAndOr controller, preserving source order.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldAndOrOfICmps(LlvmValue left, LlvmValue right, LlvmInstruction instruction, bool isAnd, bool isLogical)
        {
            if (!BoolCmpLike(left, out var predLeft, out var left0, out var left1) ||
                !BoolCmpLike(right, out var predRight, out var right0, out var right1)) return null;
            Count("boolean_compare.icmp_pair.queries");
            bool leftOneUse = left.HasOneUse, rightOneUse = right.HasOneUse;
            bool leftSameSign = left is LlvmICmpInstruction { SameSign: true }, rightSameSign = right is LlvmICmpInstruction { SameSign: true };
            LlvmAPInt? lc = (left1 as LlvmConstantInt)?.Value, rc = (right1 as LlvmConstantInt)?.Value;
            if (LlvmCmpInstAnalysis.PredicatesFoldable(predLeft, predRight))
            {
                if (left0 == right1 && left1 == right0)
                { predLeft = LlvmCmpInstruction.GetSwappedPredicate(predLeft); (left0, left1) = (left1, left0); }
                if (left0 == right0 && left1 == right1)
                {
                    uint code = isAnd ? LlvmCmpInstAnalysis.GetICmpCode(predLeft) & LlvmCmpInstAnalysis.GetICmpCode(predRight) :
                        LlvmCmpInstAnalysis.GetICmpCode(predLeft) | LlvmCmpInstAnalysis.GetICmpCode(predRight);
                    return BoolCmpRewrite(GetNewICmpValue(code, BoolCmpSigned(predLeft) || BoolCmpSigned(predRight), left0, left1), "icmp_same_operands");
                }
            }
            if (FoldAndOrOfICmpEqConstantAndICmp(predLeft, left0, left1, leftOneUse, predRight, right0, right1, rightOneUse, isAnd, isLogical) is { } eqCmp) return eqCmp;
            if (FoldAndOrOfICmpEqConstantAndICmp(predRight, right0, right1, rightOneUse, predLeft, left0, left1, leftOneUse, isAnd, false) is { } eqCmpSwap) return eqCmpSwap;
            if (FoldAndOrOfICmpsWithConstEq(predLeft, left0, left1, left, predRight, right0, right1, rightOneUse, isAnd, isLogical, instruction, rightSameSign) is { } substituted) return substituted;
            if (right is LlvmICmpInstruction rightComparison &&
                FoldAndOrOfICmpsWithConstEq(predRight, right0, right1, right, predLeft, left0, left1, leftOneUse, isAnd, false, instruction, leftSameSign) is { } substituteSwap)
            {
                if (isLogical && rightSameSign && !right.UseEmpty)
                { rightComparison.SameSign = false; Worklist.Push(rightComparison); }
                return substituteSwap;
            }
            if (FoldIsPowerOf2OrZero(predLeft, left0, left1, predRight, right0, right1, isAnd) is { } pow0) return pow0;
            if (FoldIsPowerOf2OrZero(predRight, right0, right1, predLeft, left0, left1, isAnd) is { } pow0Swap) return pow0Swap;
            if (!isLogical)
            {
                if (SimplifyRangeCheck(predLeft, left0, left1, predRight, right0, right1, instruction, !isAnd) is { } range) return range;
                if (SimplifyRangeCheck(predRight, right0, right1, predLeft, left0, left1, instruction, !isAnd) is { } rangeSwap) return rangeSwap;
            }
            if (isAnd && !isLogical && FoldSignedTruncationCheck(predLeft, left0, left1, predRight, right0, right1, instruction) is { } signedTrunc) return signedTrunc;
            if (FoldIsPowerOf2(predLeft, left0, left1, predRight, right0, right1, isAnd) is { } pow) return pow;
            if (FoldPowerOf2AndShiftedMask(left, right, isAnd) is { } powMask) return powMask;
            if (!isLogical)
            {
                if (FoldUnsignedUnderflowCheck(predLeft, left0, left1, leftOneUse, predRight, right0, right1, rightOneUse, isAnd, instruction) is { } underflow) return underflow;
                if (FoldUnsignedUnderflowCheck(predRight, right0, right1, rightOneUse, predLeft, left0, left1, leftOneUse, isAnd, instruction) is { } underflowSwap) return underflowSwap;
            }
            if (predLeft == (isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe) && predLeft == predRight &&
                BoolCmpZero(left1) && BoolCmpZero(right1) && left0.Type == right0.Type && (!isLogical || BoolCmpNonPoison(right0)))
            {
                LlvmValue union = Builder.CreateOr(left0, right0);
                return BoolCmpRewrite(Builder.CreateICmp(predLeft, union, LlvmConstant.GetNullValue(union.Type), sameSign: leftSameSign), "zero_comparison_pair");
            }
            if (predLeft == (isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe) && predLeft == predRight &&
                BoolCmpOnes(left1) && BoolCmpOnes(right1) && left0.Type == right0.Type && (!isLogical || BoolCmpNonPoison(right0)))
            {
                LlvmValue intersection = Builder.CreateAnd(left0, right0);
                return BoolCmpRewrite(Builder.CreateICmp(predLeft, intersection, LogicOnes(left0.Type), sameSign: leftSameSign), "ones_comparison_pair");
            }
            if (!isLogical && FoldAndOrOfICmpsWithPow2AndWithZero(predLeft, left0, left1, leftOneUse,
                predRight, right0, right1, rightOneUse, isAnd, instruction) is { } powerZero) return powerZero;
            if (lc is not { } constantLeft || rc is not { } constantRight) return null;
            if (LlvmValueTracking.IsSignBitCheck(predLeft, constantLeft, out bool negativeLeft) &&
                LlvmValueTracking.IsSignBitCheck(predRight, constantRight, out bool negativeRight) && (leftOneUse || rightOneUse))
            {
                if (left0 is LlvmBinaryOperator first && first.Opcode is LlvmOpcode.And or LlvmOpcode.Or)
                {
                    LlvmOpcode required = isAnd ? negativeLeft ? LlvmOpcode.Or : LlvmOpcode.And : negativeLeft ? LlvmOpcode.And : LlvmOpcode.Or;
                    LlvmOpcode other = required == LlvmOpcode.And ? LlvmOpcode.Or : LlvmOpcode.And;
                    if (negativeLeft != negativeRight && first.Opcode == required && LogicPair(right0, other, first.Left, first.Right))
                    {
                        LlvmValue xor = Builder.CreateXor(first.Left, first.Right);
                        return BoolCmpRewrite(Builder.CreateICmp(isAnd ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge,
                            xor, BoolCmpC(xor, 0)), "same_sign_bitwise_pair");
                    }
                }
            }
            if (left0 == right0 && predLeft == predRight && predLeft == (isAnd ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq) &&
                !F.NoImplicitFloat && leftOneUse && rightOneUse &&
                left0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Left: LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast, Right: LlvmConstantInt mask } &&
                cast.Operand.Type.IsDoubleTy() && mask.Value == new LlvmAPInt(64, 0x7ff0000000000000UL) &&
                (constantLeft.IsZero && constantRight == mask.Value || constantRight.IsZero && constantLeft == mask.Value))
                return BoolCmpRewrite(Builder.CreateIsFPClass(cast.Operand, isAnd ? LlvmFPClassTest.Normal : LlvmFPClassTest.All & ~LlvmFPClassTest.Normal), "ieee_exponent_normal_test");
            return FoldAndOrOfICmpsUsingRanges(predLeft, left0, left1, leftOneUse, predRight, right0, right1, rightOneUse, isAnd);
        }

        private LlvmValue? FoldBooleanAndOr(LlvmValue left, LlvmValue right, LlvmInstruction instruction, bool isAnd, bool isLogical = false)
        {
            if (!left.Type.IsIntOrIntVectorTy(1)) return null;
            if (FoldLogOpOfMaskedICmps(left, right, isAnd, isLogical, instruction) is { } masked) return masked;
            if (FoldAndOrOfICmps(left, right, instruction, isAnd, isLogical) is { } comparison) return comparison;
            if (left is LlvmFCmpInstruction leftFloat && right is LlvmFCmpInstruction rightFloat &&
                FoldLogicOfFCmps(leftFloat, rightFloat, isAnd, isLogical) is { } floating) return floating;
            return FoldEqOfParts(left, right, isAnd);
        }
    }
}
