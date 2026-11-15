// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldLogOpOfMaskedICmps, asymmetric and NotAllZeros/BMaskMixed cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldMaskedNotAllZerosMixed(LlvmValue left, LlvmValue right, bool isAnd, bool logical,
            LlvmValue a, LlvmValue b, LlvmValue d, LlvmValue e, LlvmCmpPredicate predLeft, LlvmCmpPredicate predRight)
        {
            if (b is not LlvmConstantInt bc || d is not LlvmConstantInt dc || e is not LlvmConstantInt ec) return null;
            LlvmCmpPredicate predicate = isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe;
            LlvmAPInt expected = predRight != predicate ? ec.Value ^ dc.Value : ec.Value;
            if (bc.IsZero || dc.IsZero) return null;
            if ((bc.Value & dc.Value).IsZero)
            {
                if (dc.Value != expected || a is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast || F.StrictFP || !cast.Operand.Type.IsDoubleTy()) return null;
                LlvmAPInt exponent = new(64, 0x7ff0000000000000UL);
                LlvmAPInt fraction = ~exponent & ~LlvmAPInt.GetOneBitSet(64, 63);
                if (expected != exponent || bc.Value != fraction) return null;
                return Builder.CreateFCmp(isAnd ? LlvmCmpPredicate.FCmpUno : LlvmCmpPredicate.FCmpOrd,
                    cast.Operand, LlvmConstantFP.Get(cast.Operand.Type, 0.0));
            }
            LlvmAPInt lone = bc.Value & (bc.Value ^ dc.Value);
            if (((bc.Value & dc.Value) & expected).IsZero && lone.IsPowerOf2)
                return Builder.CreateICmp(predicate, Builder.CreateAnd(a, BoolCmpC(a, bc.Value | dc.Value)),
                    BoolCmpC(a, lone | expected));
            bool subset = (bc.Value & dc.Value) == bc.Value, superset = (bc.Value & dc.Value) == dc.Value;
            if (!subset && !superset) return null;
            if (expected.IsZero) return subset ? LlvmConstantInt.GetBool(left.Context, !isAnd) : null;
            if (superset || !(bc.Value & expected).IsZero)
                return GetReconstructedMaskedICmp(logical, right, a, d, e, predRight);
            return LlvmConstantInt.GetBool(left.Context, !isAnd);
        }

        private LlvmValue? FoldMaskedAsymmetric(LlvmValue left, LlvmValue right, bool isAnd, bool logical, BoolCmpMaskedPair pair)
        {
            BoolCmpMask leftMask = pair.LeftMask, rightMask = pair.RightMask;
            if (!isAnd) { leftMask = ConjugateICmpMask(leftMask); rightMask = ConjugateICmpMask(rightMask); }
            if ((leftMask & BoolCmpMask.NotAllZeros) != 0 && (rightMask & BoolCmpMask.BMixed) != 0)
                return FoldMaskedNotAllZerosMixed(left, right, isAnd, logical, pair.A, pair.B, pair.D, pair.E, pair.PredLeft, pair.PredRight);
            if ((leftMask & BoolCmpMask.BMixed) != 0 && (rightMask & BoolCmpMask.NotAllZeros) != 0)
                return FoldMaskedNotAllZerosMixed(right, left, isAnd, false, pair.A, pair.D, pair.B, pair.C, pair.PredRight, pair.PredLeft);
            return null;
        }

        private LlvmValue? FoldLogOpOfMaskedICmps(LlvmValue left, LlvmValue right, bool isAnd, bool logical, LlvmInstruction context)
        {
            if (GetMaskedTypeForICmpPair(left, right) is not { } pair) return null;
            LlvmValue a = pair.A, b = pair.B, c = pair.C, d = pair.D, e = pair.E;
            BoolCmpMask mask = pair.LeftMask & pair.RightMask;
            if (mask == 0)
            {
                var asymmetric = FoldMaskedAsymmetric(left, right, isAnd, logical, pair);
                return asymmetric is null ? null : BoolCmpRewrite(asymmetric, "masked_asymmetric");
            }
            LlvmCmpPredicate predicate = isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe;
            if (!isAnd) mask = ConjugateICmpMask(mask);
            if ((mask & BoolCmpMask.AllZeros) != 0)
            {
                if (logical && !BoolCmpDefined(d)) return null;
                return BoolCmpRewrite(Builder.CreateICmp(predicate, Builder.CreateAnd(a, Builder.CreateOr(b, d)), BoolCmpC(a, 0)), "masked_all_zero");
            }
            if ((mask & BoolCmpMask.BAllOnes) != 0)
            {
                if (logical && !BoolCmpDefined(d)) return null;
                LlvmValue union = Builder.CreateOr(b, d);
                return BoolCmpRewrite(Builder.CreateICmp(predicate, Builder.CreateAnd(a, union), union), "masked_all_ones_b");
            }
            if ((mask & BoolCmpMask.AAllOnes) != 0)
            {
                if (logical && !BoolCmpDefined(d)) return null;
                return BoolCmpRewrite(Builder.CreateICmp(predicate, Builder.CreateAnd(a, Builder.CreateAnd(b, d)), a), "masked_all_ones_a");
            }
            if (b is LlvmConstantInt bc && d is LlvmConstantInt dc)
            {
                if ((mask & (BoolCmpMask.NotAllZeros | BoolCmpMask.BNotAllOnes)) != 0)
                {
                    LlvmAPInt intersection = bc.Value & dc.Value;
                    if (intersection == bc.Value) return left;
                    if (intersection == dc.Value) return GetReconstructedMaskedICmp(logical, right, a, d, e, pair.PredRight);
                }
                if ((mask & BoolCmpMask.ANotAllOnes) != 0)
                {
                    LlvmAPInt union = bc.Value | dc.Value;
                    if (union == bc.Value) return left;
                    if (union == dc.Value) return right;
                }
                if ((mask & (BoolCmpMask.BMixed | BoolCmpMask.BNotMixed)) != 0)
                {
                    if (c is not LlvmConstantInt cc || e is not LlvmConstantInt ec) return null;
                    bool inverted = (mask & BoolCmpMask.BMixed) == 0;
                    LlvmCmpPredicate finalPredicate = inverted ? LlvmCmpInstruction.GetInversePredicate(predicate) : predicate;
                    LlvmAPInt constC = pair.PredLeft != finalPredicate ? bc.Value ^ cc.Value : cc.Value;
                    LlvmAPInt constE = pair.PredRight != finalPredicate ? dc.Value ^ ec.Value : ec.Value;
                    if (!((bc.Value & dc.Value) & (constC ^ constE)).IsZero)
                        return inverted ? null : LlvmConstantInt.GetBool(left.Context, !isAnd);
                    if (inverted && (bc.Value & ~dc.Value) != LlvmAPInt.GetZero(bc.BitWidth) &&
                        (dc.Value & ~bc.Value) != LlvmAPInt.GetZero(bc.BitWidth)) return null;
                    LlvmAPInt newMask = inverted ? bc.Value & dc.Value : bc.Value | dc.Value;
                    LlvmAPInt expected = inverted ? constC & constE : constC | constE;
                    return BoolCmpRewrite(Builder.CreateICmp(finalPredicate, Builder.CreateAnd(a, BoolCmpC(a, newMask)), BoolCmpC(a, expected)), "masked_mixed");
                }
            }
            if ((mask & BoolCmpMask.NotAllZeros) != 0 && BoolCmpPower(b, false, context) && BoolCmpPower(d, false, context))
            {
                if (logical) d = Builder.CreateFreeze(d);
                LlvmValue union = Builder.CreateOr(b, d);
                return BoolCmpRewrite(Builder.CreateICmp(predicate, Builder.CreateAnd(a, union), union), "masked_single_bits");
            }
            return null;
        }
    }
}
