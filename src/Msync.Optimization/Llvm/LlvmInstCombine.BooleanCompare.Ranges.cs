// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// simplifyRangeCheck, foldAndOrOfICmpsUsingRanges, constant-equality substitution.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyRangeCheck(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, LlvmInstruction context, bool inverted)
        {
            if (left1 is not LlvmConstantInt start) return null;
            if (inverted)
            { predLeft = LlvmCmpInstruction.GetInversePredicate(predLeft); predRight = LlvmCmpInstruction.GetInversePredicate(predRight); }
            if (!(predLeft == LlvmCmpPredicate.ICmpSgt && start.IsMinusOne || predLeft == LlvmCmpPredicate.ICmpSge && start.IsZero)) return null;
            LlvmValue input = left0, end;
            if ((right0 is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sx0 ? sx0.Operand : right0) == input)
            { input = right0; end = right1; }
            else if ((right1 is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sx1 ? sx1.Operand : right1) == input)
            { input = right1; end = right0; predRight = LlvmCmpInstruction.GetSwappedPredicate(predRight); }
            else return null;
            LlvmCmpPredicate predicate;
            if (predRight == LlvmCmpPredicate.ICmpSlt) predicate = LlvmCmpPredicate.ICmpUlt;
            else if (predRight == LlvmCmpPredicate.ICmpSle) predicate = LlvmCmpPredicate.ICmpUle;
            else return null;
            if (!LogicKnown(end, context).IsNonNegative) return null;
            if (inverted) predicate = LlvmCmpInstruction.GetInversePredicate(predicate);
            return BoolCmpRewrite(Builder.CreateICmp(predicate, input, end), "signed_zero_range");
        }

        private static (LlvmValue Value, LlvmConstantRange Range)? BoolCmpMatchExactRange(
            LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
        {
            if (right is not LlvmConstantInt constant) return null;
            LlvmAPInt bits = constant.Value;
            if (BoolCmpEq(predicate) && left is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Right: LlvmConstantInt mask } maskedValue &&
                (-mask.Value).IsPowerOf2 && bits.CountTrailingZeros() >= mask.Value.CountTrailingZeros())
            {
                LlvmConstantRange masked = new(bits, bits - mask.Value);
                return (maskedValue.Left, predicate == LlvmCmpPredicate.ICmpNe ? masked.Inverse() : masked);
            }
            LlvmConstantRange range = LlvmConstantRange.MakeExactICmpRegion(predicate, bits);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt add } addition)
                return (addition.Left, range.Subtract(add.Value));
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true, Right: LlvmConstantInt orMask } disjoint)
                return (disjoint.Left, range.Subtract(orMask.Value));
            return (left, range);
        }

        private LlvmValue? FoldAndOrOfICmpsUsingRanges(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            bool leftOneUse, LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool rightOneUse, bool isAnd)
        {
            var first = BoolCmpMatchExactRange(predLeft, left0, left1);
            var second = BoolCmpMatchExactRange(predRight, right0, right1);
            if (first is not { } a || second is not { } b || a.Value != b.Value) return null;
            LlvmConstantRange rangeA = isAnd ? a.Range.Inverse() : a.Range;
            LlvmConstantRange rangeB = isAnd ? b.Range.Inverse() : b.Range;
            LlvmValue value = a.Value;
            LlvmConstantRange? merged = rangeA.ExactUnionWith(rangeB);
            if (merged is null)
            {
                if (!leftOneUse || !rightOneUse || rangeA.IsWrappedSet || rangeB.IsWrappedSet) return null;
                LlvmAPInt one = new(rangeA.BitWidth, 1);
                LlvmAPInt lowerDiff = rangeA.Lower ^ rangeB.Lower;
                LlvmAPInt upperDiff = (rangeA.Upper - one) ^ (rangeB.Upper - one);
                if (!lowerDiff.IsPowerOf2 || lowerDiff != upperDiff || rangeA.Upper - rangeA.Lower != rangeB.Upper - rangeB.Lower) return null;
                merged = rangeA.Lower.Ult(rangeB.Lower) ? rangeA : rangeB;
                value = Builder.CreateAnd(value, BoolCmpC(value, ~lowerDiff));
            }
            if (isAnd) merged = merged.Inverse();
            merged.GetEquivalentICmp(out var predicate, out var rhs, out var offset);
            if (!offset.IsZero) value = Builder.CreateAdd(value, BoolCmpC(value, offset));
            return BoolCmpRewrite(Builder.CreateICmp(predicate, value, BoolCmpC(value, rhs)), "interval_union");
        }

        private LlvmValue? FoldAndOrOfICmpsWithConstEq(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            LlvmValue left, LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool rightOneUse,
            bool isAnd, bool isLogical, LlvmInstruction context, bool rightSameSign = false)
        {
            if (left1 is not LlvmConstant || !BoolCmpDefined(left1) || left0 is LlvmConstant ||
                predLeft != (isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe)) return null;
            LlvmValue other;
            if (left0 == right0) { other = right1; predRight = LlvmCmpInstruction.GetSwappedPredicate(predRight); }
            else if (left0 == right1) other = right0;
            else return null;
            LlvmValue? substitute = LlvmInstructionSimplify.SimplifyICmp(predRight, other, left1, Query(context));
            if (substitute is null)
            {
                if (!rightOneUse) return null;
                substitute = Builder.CreateICmp(predRight, other, left1, sameSign: rightSameSign);
            }
            LlvmValue result = isLogical ?
                (isAnd ? Builder.CreateSelect(left, substitute, LlvmConstantInt.GetFalse(left.Context)) :
                         Builder.CreateSelect(left, LlvmConstantInt.GetTrue(left.Context), substitute)) :
                Builder.CreateBinOp(isAnd ? LlvmOpcode.And : LlvmOpcode.Or, left, substitute);
            return BoolCmpRewrite(result, "constant_equivalence_substitution");
        }

        private LlvmValue? FoldAndOrOfICmpEqConstantAndICmp(LlvmCmpPredicate predLeft, LlvmValue left0, LlvmValue left1,
            bool leftOneUse, LlvmCmpPredicate predRight, LlvmValue right0, LlvmValue right1, bool rightOneUse, bool isAnd, bool isLogical)
        {
            if (isAnd)
            { predLeft = LlvmCmpInstruction.GetInversePredicate(predLeft); predRight = LlvmCmpInstruction.GetInversePredicate(predRight); }
            if (predLeft != LlvmCmpPredicate.ICmpEq || left1 is not LlvmConstantInt constant ||
                !left0.Type.IsIntOrIntVectorTy() || !(leftOneUse || rightOneUse)) return null;
            bool MatchOther(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt add } addition &&
                addition.Left == left0 && add.Value == -constant.Value || constant.IsZero && value == left0;
            LlvmValue other;
            if (predRight == LlvmCmpPredicate.ICmpUlt && MatchOther(right1)) other = right0;
            else if (predRight == LlvmCmpPredicate.ICmpUgt && MatchOther(right0)) other = right1;
            else return null;
            if (isLogical) other = Builder.CreateFreeze(other);
            LlvmValue shifted = Builder.CreateSub(left0, BoolCmpC(left0, constant.Value + new LlvmAPInt(constant.BitWidth, 1)));
            return BoolCmpRewrite(Builder.CreateICmp(isAnd ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge, shifted, other), "eq_constant_unsigned_compare");
        }
    }
}
