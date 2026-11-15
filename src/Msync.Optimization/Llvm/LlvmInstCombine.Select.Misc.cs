// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectEqualityTest/foldRoundUpIntegerWithPow2Alignment/foldSelectCmpBitcasts.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectEqualityTest(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq } condition ||
                select.TrueValue is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq } trueCmp) return null;
            LlvmValue x = condition.Left, z = condition.Right;
            bool MatchEqualWith(LlvmValue wanted, out LlvmValue other)
            {
                other = trueCmp.Right;
                if (ReferenceEquals(trueCmp.Left, wanted)) return true;
                other = trueCmp.Left; return ReferenceEquals(trueCmp.Right, wanted);
            }
            if (!MatchEqualWith(z, out LlvmValue y)) (x, z) = (z, x);
            if (!MatchEqualWith(z, out y) || !SelectMatchLogical(select.FalseValue, true, out LlvmValue left, out LlvmValue right)) return null;
            LlvmValue equal;
            if (SelectMatchNot(left, out LlvmValue not) && ReferenceEquals(not, trueCmp)) equal = right;
            else if (SelectMatchNot(right, out not) && ReferenceEquals(not, trueCmp)) equal = left;
            else return null;
            if (equal is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq } compare ||
                !((ReferenceEquals(compare.Left, x) && ReferenceEquals(compare.Right, y)) ||
                (ReferenceEquals(compare.Left, y) && ReferenceEquals(compare.Right, x)))) return null;
            compare.SameSign = false;
            return ReplaceInstUsesWith(select, compare);
        }
        private LlvmValue? FoldRoundUpIntegerWithPow2Alignment(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmICmpInstruction compare || !SelectIsZero(compare.Right) ||
                compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)) return null;
            LlvmValue value = select.TrueValue, biased = select.FalseValue;
            if (compare.Predicate == LlvmCmpPredicate.ICmpNe) (value, biased) = (biased, value);
            if (compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and ||
                !ReferenceEquals(and.Left, value) || and.Right is not LlvmConstantInt low) return null;
            LlvmConstantInt bias, high;
            if (biased is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add } masked &&
                ReferenceEquals(add.Left, value) && add.Right is LlvmConstantInt addBias && masked.Right is LlvmConstantInt mask)
            { bias = addBias; high = mask; }
            else if (biased is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And } maskedInput } sum &&
                ReferenceEquals(maskedInput.Left, value) && maskedInput.Right is LlvmConstantInt inputMask && sum.Right is LlvmConstantInt sumBias)
            { bias = sumBias; high = inputMask; }
            else return null;
            uint ones = low.Value.CountTrailingOnes();
            if (ones == 0 || ones + low.Value.CountLeadingZeros() != low.Value.BitWidth || ~low.Value != high.Value) return null;
            LlvmAPInt alignment = low.Value + new LlvmAPInt(low.Value.BitWidth, 1, implicitTrunc: true);
            if (bias.Value != alignment && bias.Value != low.Value) return null;
            if (!biased.HasOneUse)
                return bias.Value == low.Value && LlvmValueTracking.ImpliesPoison(biased, value, Metrics) ? biased : null;
            LlvmValue offset = Builder.CreateAdd(value, low, value.Name + ".biased");
            LlvmValue result = Builder.CreateAnd(offset, high);
            result.TakeName(select); return result;
        }
        private LlvmInstruction? FoldSelectCmpBitcasts(LlvmSelectInstruction select)
        {
            LlvmValue whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            if (select.Condition is not LlvmCmpInstruction compare) return null;
            LlvmValue a = compare.Left, b = compare.Right;
            if (ReferenceEquals(whenTrue, a) || ReferenceEquals(whenTrue, b) || ReferenceEquals(whenFalse, a) || ReferenceEquals(whenFalse, b)) return null;
            if (a is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } castA || b is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } castB ||
                whenTrue is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } trueCast || whenFalse is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } falseCast) return null;
            LlvmValue newSelect;
            if (ReferenceEquals(trueCast.Operand, castA.Operand) && ReferenceEquals(falseCast.Operand, castB.Operand)) newSelect = Builder.CreateSelect(select.Condition, a, b);
            else if (ReferenceEquals(trueCast.Operand, castB.Operand) && ReferenceEquals(falseCast.Operand, castA.Operand)) newSelect = Builder.CreateSelect(select.Condition, b, a);
            else return null;
            return new LlvmCastInstruction(LlvmOpcode.BitCast, newSelect, select.Type);
        }
    }
}
