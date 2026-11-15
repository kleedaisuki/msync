// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// power-of-two, signed-truncation, shifted-mask and unsigned-underflow folds.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool BoolCmpIntrinsic(LlvmValue value, LlvmIntrinsicKind kind, out LlvmCallInstruction call)
        {
            if (value is LlvmCallInstruction candidate && candidate.CalledFunction?.IntrinsicDescriptor?.Kind == kind)
            { call = candidate; return true; }
            call = null!; return false;
        }
        private LlvmValue? FoldIsPowerOf2OrZero(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool isAnd)
        {
            if (!BoolCmpIntrinsic(left0, LlvmIntrinsicKind.CTPop, out var pop) || !BoolCmpOne(left1) ||
                right0 != pop.GetArgument(0) || !BoolCmpZero(right1)) return null;
            if (isAnd && predLeft == LlvmCmpPredicate.ICmpNe && predRight == LlvmCmpPredicate.ICmpNe)
            {
                pop.DropPoisonGeneratingAnnotations(); Worklist.Push(pop);
                return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpUgt, pop, BoolCmpC(pop, 1)), "ctpop_nonzero_and");
            }
            if (!isAnd && predLeft == LlvmCmpPredicate.ICmpEq && predRight == LlvmCmpPredicate.ICmpEq)
            {
                pop.DropPoisonGeneratingAnnotations(); Worklist.Push(pop);
                return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, pop, BoolCmpC(pop, 2)), "ctpop_zero_or");
            }
            return null;
        }
        private LlvmValue? FoldIsPowerOf2(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool isAnd)
        {
            if (predRight == (isAnd ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq))
            { (predLeft, predRight) = (predRight, predLeft); (left0, right0) = (right0, left0); (left1, right1) = (right1, left1); }
            if (!BoolCmpZero(left1) || !BoolCmpIntrinsic(right0, LlvmIntrinsicKind.CTPop, out var pop) || pop.GetArgument(0) != left0) return null;
            if (isAnd && predLeft == LlvmCmpPredicate.ICmpNe && predRight == LlvmCmpPredicate.ICmpUlt &&
                right1 is LlvmConstantInt { Value: var two } && two.BitWidth > 1 && two == new LlvmAPInt(two.BitWidth, 2))
            {
                pop.DropPoisonGeneratingAnnotations(); Worklist.Push(pop);
                return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, pop, BoolCmpC(pop, 1)), "ctpop_exact_one_and");
            }
            if (!isAnd && predLeft == LlvmCmpPredicate.ICmpEq && predRight == LlvmCmpPredicate.ICmpUgt && BoolCmpOne(right1))
            {
                pop.DropPoisonGeneratingAnnotations(); Worklist.Push(pop);
                return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, pop, BoolCmpC(pop, 1)), "ctpop_exact_one_or");
            }
            return null;
        }
        private LlvmValue? FoldAndOrOfICmpsWithPow2AndWithZero(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            bool leftOneUse, LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool rightOneUse, bool isAnd, LlvmInstruction context)
        {
            var predicate = isAnd ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq;
            if (predLeft != predicate || predRight != predicate) return null;
            if (BoolCmpZero(right1))
            { (predLeft, predRight) = (predRight, predLeft); (left0, right0) = (right0, left0); (left1, right1) = (right1, left1); }
            if (right1 == left0) (right0, right1) = (right1, right0);
            if (!leftOneUse || !rightOneUse || !BoolCmpZero(left1) || right0 != left0 || BoolCmpOne(right1) || !BoolCmpPower(right1, true, context)) return null;
            return BoolCmpRewrite(Builder.CreateICmp(predicate, Builder.CreateAnd(left0, right1), left0), "power_two_or_zero_constant_eq");
        }
        private static bool BoolCmpSignedTruncation(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right,
            out LlvmValue value, out LlvmAPInt sign)
        {
            value = null!; sign = default;
            if (predicate != LlvmCmpPredicate.ICmpUlt || left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt lo } add ||
                right is not LlvmConstantInt hi || !lo.Value.IsPowerOf2 || !hi.Value.IsPowerOf2 || hi.Value.Ule(lo.Value) || lo.Value.Shl(1) != hi.Value) return false;
            value = add.Left; sign = lo.Value; return true;
        }
        private LlvmValue? FoldSignedTruncationCheck(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, LlvmInstruction context)
        {
            LlvmValue x1; LlvmAPInt highest;
            if (BoolCmpSignedTruncation(predRight, right0, right1, out x1, out highest))
            { (predLeft, predRight) = (predRight, predLeft); (left0, right0) = (right0, left0); (left1, right1) = (right1, left1); }
            else if (!BoolCmpSignedTruncation(predLeft, left0, left1, out x1, out highest)) return null;
            if (LlvmCmpInstAnalysis.DecomposeBitTestICmp(right0, right1, predRight, false, false, true, Metrics) is not { Pred: LlvmCmpPredicate.ICmpEq } bitTest) return null;
            LlvmAPInt unset = bitTest.Mask;
            if (bitTest.X != x1)
            {
                if (bitTest.X is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc || trunc.Operand != x1) return null;
                unset = unset.ZExt(x1.Type.GetScalarSizeInBits());
            }
            LlvmAPInt signMask = ~(highest - new LlvmAPInt(highest.BitWidth, 1));
            if ((unset & signMask).IsZero) return null;
            if (!(unset & ~signMask).IsZero)
            {
                LlvmAPInt other = ~unset + new LlvmAPInt(unset.BitWidth, 1);
                if (!other.IsPowerOf2) return null;
                if (other.Ult(highest)) highest = other;
            }
            LlvmValue simplified = Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, x1, BoolCmpC(x1, highest));
            BoolCmpAppendName(simplified, context, ".simplified"u8);
            return BoolCmpRewrite(simplified, "signed_truncation_bit_test");
        }
        private static bool BoolCmpShiftedMask(LlvmAPInt value)
            => value.PopCount() + value.CountLeadingZeros() + value.CountTrailingZeros() == value.BitWidth;
        private LlvmValue? FoldNegativePower2AndShiftedMask(LlvmValue a, LlvmValue b, LlvmValue d, LlvmValue e,
            LlvmCmpPredicate predLeft, LlvmCmpPredicate predRight)
        {
            if (predLeft != LlvmCmpPredicate.ICmpEq || predRight != LlvmCmpPredicate.ICmpNe ||
                b is not LlvmConstantInt bc || d is not LlvmConstantInt dc || e is not LlvmConstantInt ec ||
                !(-bc.Value).IsPowerOf2 || !BoolCmpShiftedMask(dc.Value) || !BoolCmpShiftedMask(ec.Value) ||
                dc.Value != ec.Value || bc.Value.CountLeadingOnes() != dc.Value.CountLeadingZeros()) return null;
            return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, a, d), "negative_power_shifted_mask");
        }
        private LlvmValue? FoldPowerOf2AndShiftedMask(LlvmValue left, LlvmValue right, bool isAnd)
        {
            if (!isAnd || GetMaskedTypeForICmpPair(left, right) is not { } pair) return null;
            const BoolCmpMask compare = BoolCmpMask.BNotMixed | BoolCmpMask.BNotAllOnes;
            if ((pair.LeftMask & BoolCmpMask.AllZeros) != 0 && pair.RightMask == compare)
                return FoldNegativePower2AndShiftedMask(pair.A, pair.B, pair.D, pair.E, pair.PredLeft, pair.PredRight);
            if (pair.LeftMask == compare && (pair.RightMask & BoolCmpMask.AllZeros) != 0)
                return FoldNegativePower2AndShiftedMask(pair.A, pair.D, pair.B, pair.C, pair.PredRight, pair.PredLeft);
            return null;
        }
        private LlvmValue? FoldUnsignedUnderflowCheck(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            bool leftOneUse, LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool rightOneUse, bool isAnd, LlvmInstruction context)
        {
            if (!BoolCmpZero(left1) || !BoolCmpEq(predLeft)) return null;
            LlvmValue a;
            if (right0 == left0) a = right1;
            else if (right1 == left0) { a = right0; predRight = LlvmCmpInstruction.GetSwappedPredicate(predRight); }
            else return null;
            if (!LogicOther(left0, LlvmOpcode.Add, a, out var b) || !(leftOneUse || rightOneUse)) return null;
            bool NonZeroAndOther()
            {
                if (!BoolCmpNonZero(b, context)) (a, b) = (b, a);
                return BoolCmpNonZero(b, context);
            }
            if (predRight == LlvmCmpPredicate.ICmpUlt && predLeft == LlvmCmpPredicate.ICmpNe && isAnd && NonZeroAndOther())
                return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, Builder.CreateNeg(b), a), "unsigned_underflow_and");
            if (predRight == LlvmCmpPredicate.ICmpUge && predLeft == LlvmCmpPredicate.ICmpEq && !isAnd && NonZeroAndOther())
                return BoolCmpRewrite(Builder.CreateICmp(LlvmCmpPredicate.ICmpUge, Builder.CreateNeg(b), a), "unsigned_underflow_or");
            return null;
        }
    }
}
