// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// impliesPoisonOrCond/foldSelectOfBools.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool ImpliesPoisonOrCond(LlvmValue assumedPoison, LlvmValue value, bool expected)
        {
            if (LlvmValueTracking.ImpliesPoison(assumedPoison, value, Metrics)) return true;
            if (assumedPoison is LlvmICmpInstruction { SameSign: true, Right: LlvmConstantInt first } compare &&
                value is LlvmICmpInstruction { Right: LlvmConstantInt second } other && ReferenceEquals(compare.Left, other.Left))
            {
                uint width = first.Value.BitWidth;
                LlvmConstantRange range = first.Value.IsNonNegative ? new(LlvmAPInt.GetSignedMinValue(width), LlvmAPInt.GetZero(width)) :
                    new(LlvmAPInt.GetZero(width), LlvmAPInt.GetSignedMinValue(width));
                return range.ICmp(expected ? other.Predicate : LlvmCmpInstruction.GetInversePredicate(other.Predicate), new(second.Value));
            }
            if (assumedPoison is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true, HasNoSignedWrap: false } trunc &&
                LlvmValueTracking.IsGuaranteedNotToBePoison(trunc.Operand))
            {
                System.Diagnostics.Debug.Assert(assumedPoison.Type.IsIntegerTy(1));
                return LlvmKnownBitsAnalysis.Compute(trunc.Operand, context: trunc, tree: DT, metrics: Metrics,
                    assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: SQ.DC).GetMaxValue().IsOne;
            }
            return false;
        }
        private LlvmInstruction? FoldSelectOfBools(LlvmSelectInstruction select)
        {
            LlvmValue condition = select.Condition, whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            if (!select.Type.IsIntegerTy(1) || condition is LlvmConstant || !ReferenceEquals(whenTrue.Type, condition.Type)) return null;
            LlvmValue one = LlvmConstantInt.Get((LlvmIntegerType)select.Type, 1), zero = LlvmConstant.GetNullValue(select.Type);
            if (SelectIsOne(whenTrue))
            {
                if (ImpliesPoisonOrCond(whenFalse, condition, false)) return new LlvmBinaryOperator(LlvmOpcode.Or, condition, whenFalse);
                if (condition is LlvmSelectInstruction inner && inner.HasOneUse && SelectIsOne(inner.TrueValue) && ImpliesPoisonOrCond(whenFalse, inner.FalseValue, false))
                    return ReplaceInstUsesWith(select, Builder.CreateLogicalOr(inner.Condition, Builder.CreateOr(inner.FalseValue, whenFalse)));
                if (SelectMatchLogical(condition, true, out LlvmValue a, out LlvmValue b) &&
                    SelectMatchLogical(whenFalse, true, out LlvmValue c, out LlvmValue d) && (condition.HasOneUse || whenFalse.HasOneUse))
                {
                    bool conditionLogical = condition is LlvmSelectInstruction, falseLogical = whenFalse is LlvmSelectInstruction;
                    LlvmInstruction Factor(LlvmValue common, LlvmValue innerCondition, LlvmValue innerValue, bool selectFirst = false)
                    {
                        LlvmValue innerSelect = Builder.CreateSelect(innerCondition, one, innerValue);
                        if (selectFirst) (common, innerSelect) = (innerSelect, common);
                        return falseLogical || (conditionLogical && ReferenceEquals(common, a)) ?
                            new LlvmSelectInstruction(common, innerSelect, zero) : new LlvmBinaryOperator(LlvmOpcode.And, common, innerSelect);
                    }
                    if (ReferenceEquals(a, c)) return Factor(a, b, d);
                    if (ReferenceEquals(a, d)) return Factor(a, b, c);
                    if (ReferenceEquals(b, c)) return Factor(b, a, d);
                    if (ReferenceEquals(b, d)) return Factor(b, a, c, conditionLogical && falseLogical);
                }
            }
            if (SelectIsZero(whenFalse))
            {
                if (ImpliesPoisonOrCond(whenTrue, condition, true)) return new LlvmBinaryOperator(LlvmOpcode.And, condition, whenTrue);
                if (condition is LlvmSelectInstruction inner && inner.HasOneUse && SelectIsZero(inner.FalseValue) && ImpliesPoisonOrCond(whenTrue, inner.TrueValue, true))
                    return ReplaceInstUsesWith(select, Builder.CreateLogicalAnd(inner.Condition, Builder.CreateAnd(inner.TrueValue, whenTrue)));
                if (SelectMatchLogical(condition, false, out LlvmValue a, out LlvmValue b) &&
                    SelectMatchLogical(whenTrue, false, out LlvmValue c, out LlvmValue d) && (condition.HasOneUse || whenTrue.HasOneUse))
                {
                    bool conditionLogical = condition is LlvmSelectInstruction, trueLogical = whenTrue is LlvmSelectInstruction;
                    LlvmInstruction Factor(LlvmValue common, LlvmValue innerCondition, LlvmValue innerValue, bool selectFirst = false)
                    {
                        LlvmValue innerSelect = Builder.CreateSelect(innerCondition, innerValue, zero);
                        if (selectFirst) (common, innerSelect) = (innerSelect, common);
                        return trueLogical || (conditionLogical && ReferenceEquals(common, a)) ?
                            new LlvmSelectInstruction(common, one, innerSelect) : new LlvmBinaryOperator(LlvmOpcode.Or, common, innerSelect);
                    }
                    if (ReferenceEquals(a, c)) return Factor(a, b, d);
                    if (ReferenceEquals(a, d)) return Factor(a, b, c);
                    if (ReferenceEquals(b, c)) return Factor(b, a, d);
                    if (ReferenceEquals(b, d)) return Factor(b, a, c, conditionLogical && trueLogical);
                }
            }
            if (ReferenceEquals(whenTrue, zero)) return new LlvmSelectInstruction(Builder.CreateNot(condition, "not." + condition.Name), whenFalse, zero);
            if (ReferenceEquals(whenFalse, one)) return new LlvmSelectInstruction(Builder.CreateNot(condition, "not." + condition.Name), one, whenTrue);
            if (SelectMatchLogical(select, true, out LlvmValue left, out LlvmValue right) &&
                SelectMatchNot(left, out LlvmValue notA) && SelectMatchNot(right, out LlvmValue notB) && (condition.HasOneUse || whenTrue.HasOneUse) && notA is not LlvmConstantExpr && notB is not LlvmConstantExpr)
                return new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateSelect(notA, one, notB), one);
            if (SelectMatchLogical(select, false, out left, out right) && SelectMatchNot(left, out notA) &&
                SelectMatchNot(right, out notB) && (condition.HasOneUse || whenFalse.HasOneUse) && notA is not LlvmConstantExpr && notB is not LlvmConstantExpr)
                return new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateSelect(notA, notB, zero), one);
            if (condition is LlvmSelectInstruction orSelect && SelectIsOne(orSelect.TrueValue) && SelectIsOne(whenTrue) && ReferenceEquals(whenFalse, orSelect.FalseValue))
                return ReplaceOperand(select, 0, orSelect.Condition);
            if (condition is LlvmSelectInstruction andSelect && SelectIsZero(andSelect.FalseValue) && ReferenceEquals(whenTrue, andSelect.TrueValue) && SelectIsZero(whenFalse))
                return ReplaceOperand(select, 0, andSelect.Condition);
            // ~(A & B) & (A | B) --> A ^ B, including commuted logical matches.
            bool MatchXor(LlvmValue invertedAnd, LlvmValue orValue, out LlvmValue a, out LlvmValue b)
            {
                a = b = invertedAnd;
                return SelectMatchNot(invertedAnd, out LlvmValue andValue) && SelectMatchLogical(andValue, true, out a, out b) &&
                    SelectMatchLogical(orValue, false, out LlvmValue c, out LlvmValue d) &&
                    ((ReferenceEquals(a, c) && ReferenceEquals(b, d)) || (ReferenceEquals(a, d) && ReferenceEquals(b, c)));
            }
            if (SelectMatchLogical(select, true, out left, out right) &&
                (MatchXor(left, right, out LlvmValue x, out LlvmValue y) || MatchXor(right, left, out x, out y)))
                return new LlvmBinaryOperator(LlvmOpcode.Xor, x, y);
            if (condition is LlvmBinaryOperator binary && binary.HasOneUse)
            {
                LlvmValue? MatchOther(LlvmOpcode opcode, LlvmValue specific, bool inverted)
                {
                    if (binary.Opcode != opcode) return null;
                    bool Matches(LlvmValue value) => inverted ? SelectMatchNot(value, out LlvmValue operand) && ReferenceEquals(operand, specific) : ReferenceEquals(value, specific);
                    if (Matches(binary.Left)) return binary.Right;
                    return Matches(binary.Right) ? binary.Left : null;
                }
                if (MatchOther(LlvmOpcode.Or, whenTrue, true) is { } c)
                    return new LlvmSelectInstruction(whenTrue, Builder.CreateSelect(c, one, whenFalse), zero);
                if (MatchOther(LlvmOpcode.And, whenFalse, false) is { } other && GetFreelyInverted(other, other.HasOneUse, Builder) is { } inverted)
                    return new LlvmSelectInstruction(whenFalse, Builder.CreateSelect(inverted, one, whenTrue), zero);
                if (MatchOther(LlvmOpcode.Or, whenTrue, false) is { } otherOr && GetFreelyInverted(otherOr, otherOr.HasOneUse, Builder) is { } notOther)
                    return new LlvmSelectInstruction(whenTrue, one, Builder.CreateSelect(notOther, whenFalse, zero));
                if (MatchOther(LlvmOpcode.And, whenFalse, true) is { } cAnd)
                    return new LlvmSelectInstruction(whenFalse, one, Builder.CreateSelect(cAnd, whenTrue, zero));
            }
            if (SelectIsZero(whenFalse) || SelectIsOne(whenTrue))
            {
                bool isAnd = SelectIsZero(whenFalse);
                LlvmValue operand = isAnd ? whenTrue : whenFalse;
                if (IsCheckForZeroAndMulWithOverflow(condition, operand, isAnd, out LlvmUse? use))
                {
                    LlvmFreezeInstruction freeze = new(use!.Value!);
                    freeze.SetName(use.Value!.Name + ".fr");
                    InsertNewInstBefore(freeze, (LlvmInstruction)use.User);
                    ReplaceUse(use, freeze); return ReplaceInstUsesWith(select, operand);
                }
                if (FoldBooleanAndOr(condition, operand, select, isAnd, true) is { } folded) return ReplaceInstUsesWith(select, folded);
            }
            bool? Implied(LlvmValue first, LlvmValue second, bool truth = true) => LlvmInstructionSimplify.IsImpliedCondition(first, second, new(DL, null, Metrics), truth);
            if (SelectMatchLogical(condition, false, out left, out right) && SelectIsZero(whenFalse) && Implied(whenTrue, right) == false)
                return ReplaceOperand(select, 0, left);
            if (SelectMatchLogical(whenTrue, false, out left, out right) && SelectIsZero(whenFalse) && Implied(condition, right) == false)
                return ReplaceOperand(select, 1, left);
            if (SelectIsOne(whenTrue) && SelectMatchLogical(whenFalse, true, out left, out right) && Implied(condition, right, false) == true)
                return ReplaceOperand(select, 2, left);
            if (SelectMatchLogical(condition, true, out left, out right) && SelectIsOne(whenTrue) && Implied(whenFalse, right, false) == true)
                return ReplaceOperand(select, 0, left);
            return SelectIsOne(whenTrue) ? FoldOrOfLogicalAnds(condition, whenFalse) : null;
        }
    }
}
