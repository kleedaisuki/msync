// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// IntPart, matchIntPart, extractIntPart and foldEqOfParts.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private readonly record struct BoolCmpIntPart(LlvmValue From, uint StartBit, uint NumBits);
        private static BoolCmpIntPart? MatchIntPart(LlvmValue value)
        {
            if (value is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true } trunc) return null;
            LlvmValue original = trunc.Operand;
            uint originalBits = original.Type.GetScalarSizeInBits(), extractedBits = value.Type.GetScalarSizeInBits();
            if (original is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt shift } binary &&
                shift.Value.Ule(new LlvmAPInt(shift.BitWidth, originalBits - extractedBits)))
                return new(binary.Left, (uint)shift.GetZExtValue(), extractedBits);
            return new(original, 0, extractedBits);
        }
        private LlvmValue ExtractIntPart(BoolCmpIntPart part)
        {
            LlvmValue value = part.From;
            if (part.StartBit != 0) value = Builder.CreateLShr(value, BoolCmpC(value, part.StartBit));
            LlvmType type = LlvmIntegerType.Get(value.Context, part.NumBits);
            return type != value.Type ? Builder.CreateTrunc(value, type) : value;
        }
        private static BoolCmpIntPart? BoolCmpMatchPart(LlvmValue comparison, int operand, LlvmCmpPredicate predicate)
        {
            LlvmValue candidate = comparison;
            if (predicate == LlvmCmpPredicate.ICmpEq)
            {
                if (candidate is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } not)
                {
                    if (BoolCmpOnes(not.Left)) candidate = not.Right;
                    else if (BoolCmpOnes(not.Right)) candidate = not.Left;
                    else candidate = comparison;
                }
            }
            if ((predicate == LlvmCmpPredicate.ICmpNe || candidate != comparison) &&
                candidate is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor })
                return new(operand == 0 ? xor.Left : xor.Right, 0, 1);
            if (comparison is not LlvmICmpInstruction compare) return null;
            if (predicate == compare.Predicate) return MatchIntPart(compare.GetOperand(operand)!);
            if (compare.Right is not LlvmConstantInt constant || compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } bits) return null;
            uint start;
            if (predicate == LlvmCmpPredicate.ICmpEq && compare.Predicate == LlvmCmpPredicate.ICmpUlt && constant.Value.IsPowerOf2)
                start = constant.Value.CountTrailingZeros();
            else if (predicate == LlvmCmpPredicate.ICmpNe && compare.Predicate == LlvmCmpPredicate.ICmpUgt && LogicMask(constant.Value))
                start = constant.Value.PopCount();
            else return null;
            return new(operand == 0 ? bits.Left : bits.Right, start, constant.BitWidth - start);
        }
        private LlvmValue? FoldEqOfParts(LlvmValue first, LlvmValue second, bool isAnd)
        {
            if (!first.HasOneUse || !second.HasOneUse) return null;
            LlvmCmpPredicate predicate = isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe;
            if (BoolCmpMatchPart(first, 0, predicate) is not { } left0 || BoolCmpMatchPart(first, 1, predicate) is not { } right0 ||
                BoolCmpMatchPart(second, 0, predicate) is not { } left1 || BoolCmpMatchPart(second, 1, predicate) is not { } right1) return null;
            if (left0.From != left1.From || right0.From != right1.From)
            {
                if (left0.From != right1.From || right0.From != left1.From) return null;
                (left1, right1) = (right1, left1);
            }
            if (left0.StartBit + left0.NumBits != left1.StartBit || right0.StartBit + right0.NumBits != right1.StartBit)
            {
                if (left1.StartBit + left1.NumBits != left0.StartBit || right1.StartBit + right1.NumBits != right0.StartBit) return null;
                (left0, left1) = (left1, left0); (right0, right1) = (right1, right0);
            }
            LlvmValue left = ExtractIntPart(new(left0.From, left0.StartBit, left0.NumBits + left1.NumBits));
            LlvmValue right = ExtractIntPart(new(right0.From, right0.StartBit, right0.NumBits + right1.NumBits));
            return BoolCmpRewrite(Builder.CreateICmp(predicate, left, right), "adjacent_integer_parts");
        }
    }
}
