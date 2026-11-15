// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldAndToXor/foldOrToXor, simplifyAndOrWithOpReplaced,
// reassociateBooleanAndOr, foldRoundUpToPow2Alignment.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldAndToXor(LlvmBinaryOperator i)
        {
            if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or && LogicNot(i.Right, out var not) && LogicPair(not, LlvmOpcode.And, or.Left, or.Right))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, or.Left, or.Right), "and.to_xor");
            if (!i.Left.HasOneUse && !i.Right.HasOneUse) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
                if (LogicNotPairOperands(i.Left, LlvmOpcode.Or, orientation != 0, out var a, out var b) && LogicNotPair(i.Right, LlvmOpcode.Or, b, a))
                    return LogicResult(LogicNotInstruction(Builder.CreateXor(a, b)), "and.to_not_xor");
            return null;
        }
        private LlvmInstruction? FoldOrToXor(LlvmBinaryOperator i)
        {
            if ((i.Left.HasOneUse || i.Right.HasOneUse) && LogicNot(i.Right, out var not))
            {
                if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.And or LlvmOpcode.Xor } binary && LogicPair(not, LlvmOpcode.Or, binary.Left, binary.Right))
                    return LogicResult(LogicNotInstruction(binary.Opcode == LlvmOpcode.And ? Builder.CreateXor(binary.Left, binary.Right) : Builder.CreateAnd(binary.Left, binary.Right)), "or.to_inverted_xor_and");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
                if (LogicNotPairOperands(i.Left, LlvmOpcode.And, orientation != 0, out var a, out var b) && LogicNotPair(i.Right, LlvmOpcode.And, b, a))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, a, b), "or.to_xor");
            return null;
        }
        // PatternMatch m_c_BinOp(m_Value(A),m_Not(m_Value(B))).
        private static bool LogicNotPairOperands(LlvmValue value, LlvmOpcode opcode, bool reverse, out LlvmValue a, out LlvmValue b)
        {
            if (LogicBin(value, opcode) is { } binary && LogicNot(reverse ? binary.Left : binary.Right, out b))
            { a = reverse ? binary.Right : binary.Left; return true; }
            a = b = null!; return false;
        }
        private static bool LogicNotPair(LlvmValue value, LlvmOpcode opcode, LlvmValue a, LlvmValue b)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
                if (LogicNotPairOperands(value, opcode, orientation != 0, out var left, out var inverted) && ReferenceEquals(left, a) && ReferenceEquals(inverted, b)) return true;
            return false;
        }
        private LlvmValue? SimplifyAndOrWithOpReplaced(LlvmValue value, LlvmValue operand, LlvmValue replacement,
            bool simplifyOnly, uint depth = 0)
        {
            if (ReferenceEquals(operand, replacement)) return null;
            if (ReferenceEquals(value, operand)) return replacement;
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor } binary || depth >= 3) return null;
            if (!binary.HasOneUse) simplifyOnly = true;
            var left = SimplifyAndOrWithOpReplaced(binary.Left, operand, replacement, simplifyOnly, depth + 1);
            var right = SimplifyAndOrWithOpReplaced(binary.Right, operand, replacement, simplifyOnly, depth + 1);
            if (left is null && right is null) return null;
            left ??= binary.Left; right ??= binary.Right;
            if (LlvmInstructionSimplify.SimplifyBinOp(binary.Opcode, left, right, Query(binary)) is { } folded) return folded;
            return simplifyOnly ? null : Builder.CreateBinary(binary.Opcode, left, right);
        }
        private LlvmValue? ReassociateBooleanAndOr(LlvmValue lhs, LlvmValue x, LlvmValue y,
            LlvmInstruction i, bool isAnd, bool rhsIsLogical)
        {
            var opcode = isAnd ? LlvmOpcode.And : LlvmOpcode.Or;
            if (FoldBooleanAndOr(lhs, x, i, isAnd, false) is { } left)
                return rhsIsLogical ? (isAnd ? Builder.CreateLogicalAnd(left, y) : Builder.CreateLogicalOr(left, y)) : Builder.CreateBinary(opcode, left, y);
            if (FoldBooleanAndOr(lhs, y, i, isAnd, false) is { } right)
                return rhsIsLogical ? (isAnd ? Builder.CreateLogicalAnd(x, right) : Builder.CreateLogicalOr(x, right)) : Builder.CreateBinary(opcode, x, right);
            return null;
        }
        private LlvmInstruction? FoldRoundUpToPow2Alignment(LlvmBinaryOperator i)
        {
            if (i.Right is not LlvmConstantInt negative || !(-negative.Value).IsPowerOf2 || i.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true } add) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var x = orientation == 0 ? add.Left : add.Right;
                var choice = orientation == 0 ? add.Right : add.Left;
                if (choice is not LlvmSelectInstruction { TrueValue: LlvmConstantInt { IsZero: true }, FalseValue: LlvmConstantInt amount,
                    Condition: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq, Right: LlvmConstantInt { IsZero: true } } cmp } || amount.Value != -negative.Value) continue;
                var mask = ~negative.Value;
                if (cmp.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt c } and || !ReferenceEquals(and.Left, x) || c.Value != mask) continue;
                var newAdd = Builder.CreateAdd(x, LogicC(i.Type, mask));
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, newAdd, negative), "and.round_up_pow2_alignment");
            }
            return null;
        }
        private static bool LogicRecurrence(LlvmBinaryOperator i, out LlvmPhiInstruction phi, out LlvmValue start, out LlvmValue step)
        {
            phi = i.Left as LlvmPhiInstruction ?? i.Right as LlvmPhiInstruction ?? null!;
            start = step = null!;
            if (phi is null || phi.IncomingCount != 2) return false;
            for (int index = 0; index < 2; ++index)
            {
                if (phi.GetIncomingValue(index) is not LlvmBinaryOperator recurrence || (!ReferenceEquals(recurrence.Left, phi) && !ReferenceEquals(recurrence.Right, phi))) continue;
                if (!ReferenceEquals(recurrence, i)) return false;
                start = phi.GetIncomingValue(1 - index); step = ReferenceEquals(i.Left, phi) ? i.Right : i.Left; return true;
            }
            return false;
        }
    }
}
