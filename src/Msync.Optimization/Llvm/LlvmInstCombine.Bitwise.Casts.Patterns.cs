// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// matchDeMorgansLaws / foldComplexAndOrPatterns.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? MatchDeMorgansLaws(LlvmBinaryOperator instruction)
        {
            LlvmOpcode opcode = instruction.Opcode, flipped = opcode == LlvmOpcode.And ? LlvmOpcode.Or : LlvmOpcode.And;
            if (instruction.Left.HasOneUse && instruction.Right.HasOneUse && LogicNot(instruction.Left, out var a) && LogicNot(instruction.Right, out var b) &&
                !IsFreeToInvert(a, a.HasOneUse) && !IsFreeToInvert(b, b.HasOneUse))
                return LogicResult(LogicNotInstruction(Builder.CreateBinOp(flipped, a, b, instruction.Name + ".demorgan")), "demorgan_both_not");
            if (instruction.Left is LlvmBinaryOperator inner && inner.Opcode == opcode && inner.HasOneUse && LogicNot(instruction.Right, out var c))
            {
                LlvmValue x = inner.Left, not = inner.Right;
                if (!LogicNot(not, out var y)) { (x, not) = (not, x); if (!LogicNot(not, out y)) return null; }
                return LogicResult(new LlvmBinaryOperator(opcode, x, Builder.CreateNot(Builder.CreateBinOp(flipped, y, c))), "demorgan_reassociate_not");
            }
            return null;
        }
        private static bool MatchLogicNotPair(LlvmValue value, LlvmOpcode opcode, LlvmValue a, LlvmValue b, bool innerOneUse)
        {
            return value.HasOneUse && LogicNot(value, out var inner) && (!innerOneUse || inner.HasOneUse) && LogicPair(inner, opcode, a, b);
        }
        private static bool MatchLogicNotOrAnd(LlvmValue value, LlvmOpcode opcode, LlvmOpcode flipped,
            out LlvmValue a, out LlvmValue b, out LlvmValue c, out LlvmValue not, bool oneUse = false)
        {
            a = b = c = not = null!;
            if ((oneUse && !value.HasOneUse) || LogicBin(value, flipped) is not { } outer) return false;
            not = outer.Left; c = outer.Right;
            if (!LogicNot(not, out var inner) || LogicBin(inner, opcode) is null)
            { not = outer.Right; c = outer.Left; if (!LogicNot(not, out inner) || LogicBin(inner, opcode) is null) return false; }
            if (oneUse && !not.HasOneUse) return false;
            var binary = (LlvmBinaryOperator)inner;
            a = binary.Left; b = binary.Right; return true;
        }
        private static bool MatchSpecificLogicNotOrAnd(LlvmValue value, LlvmOpcode opcode, LlvmOpcode flipped, LlvmValue a, LlvmValue b, LlvmValue c)
        {
            if (!value.HasOneUse || LogicBin(value, flipped) is not { } outer) return false;
            return ReferenceEquals(outer.Left, c) && outer.Right.HasOneUse && LogicNot(outer.Right, out var x) && LogicPair(x, opcode, a, b) ||
                ReferenceEquals(outer.Right, c) && outer.Left.HasOneUse && LogicNot(outer.Left, out var y) && LogicPair(y, opcode, a, b);
        }
        private static bool MatchLogicNotTriple(LlvmValue value, LlvmOpcode opcode, LlvmValue a, LlvmValue b, LlvmValue c)
        {
            if (!value.HasOneUse || !LogicNot(value, out var inner)) return false;
            return LogicOther(inner, opcode, c, out var ab) && LogicPair(ab, opcode, a, b) ||
                LogicOther(inner, opcode, a, out var bc) && LogicPair(bc, opcode, b, c) ||
                LogicOther(inner, opcode, b, out var ac) && LogicPair(ac, opcode, a, c);
        }
        private static bool MatchLogicTripleWithNot(LlvmValue value, LlvmOpcode opcode, out LlvmValue a, out LlvmValue b, out LlvmValue c, out LlvmValue not)
        {
            a = b = c = not = null!;
            if (!value.HasOneUse || LogicBin(value, opcode) is not { } outer) return false;
            for (int reverse = 0; reverse < 2; ++reverse)
            {
                LlvmValue candidate = reverse != 0 ? outer.Right : outer.Left, other = reverse != 0 ? outer.Left : outer.Right;
                if (LogicBin(candidate, opcode) is { } pair && LogicNot(other, out var inverted))
                { a = inverted; b = pair.Left; c = pair.Right; not = other; return true; }
            }
            for (int reverse = 0; reverse < 2; ++reverse)
            {
                LlvmValue candidate = reverse != 0 ? outer.Right : outer.Left, other = reverse != 0 ? outer.Left : outer.Right;
                if (LogicBin(candidate, opcode) is not { } pair) continue;
                if (LogicNot(pair.Right, out var inverted)) { a = inverted; b = other; c = pair.Left; not = pair.Right; return true; }
                if (LogicNot(pair.Left, out inverted)) { a = inverted; b = other; c = pair.Right; not = pair.Left; return true; }
            }
            return false;
        }
        private LlvmInstruction? FoldComplexAndOrPatterns(LlvmBinaryOperator instruction)
        {
            LlvmOpcode opcode = instruction.Opcode, flipped = opcode == LlvmOpcode.And ? LlvmOpcode.Or : LlvmOpcode.And;
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (MatchLogicNotOrAnd(left, opcode, flipped, out var a, out var b, out var c, out var not))
            {
                if (MatchSpecificLogicNotOrAnd(right, opcode, flipped, a, c, b))
                {
                    LlvmValue xor = Builder.CreateXor(b, c);
                    return LogicResult(opcode == LlvmOpcode.Or ? new LlvmBinaryOperator(LlvmOpcode.And, xor, Builder.CreateNot(a)) : LogicNotInstruction(Builder.CreateAnd(xor, a)), "complex_not_pair_ac");
                }
                if (MatchSpecificLogicNotOrAnd(right, opcode, flipped, b, c, a))
                {
                    LlvmValue xor = Builder.CreateXor(a, c);
                    return LogicResult(opcode == LlvmOpcode.Or ? new LlvmBinaryOperator(LlvmOpcode.And, xor, Builder.CreateNot(b)) : LogicNotInstruction(Builder.CreateAnd(xor, b)), "complex_not_pair_bc");
                }
                if (MatchLogicNotPair(right, opcode, a, c, true))
                    return LogicResult(LogicNotInstruction(Builder.CreateBinOp(opcode, Builder.CreateBinOp(flipped, b, c), a)), "complex_not_absorb_ac");
                if (MatchLogicNotPair(right, opcode, b, c, true))
                    return LogicResult(LogicNotInstruction(Builder.CreateBinOp(opcode, Builder.CreateBinOp(flipped, a, c), b)), "complex_not_absorb_bc");
                if (opcode == LlvmOpcode.Or && left.HasOneUse && right.HasOneUse && LogicNot(right, out var y) &&
                    LogicOther(y, opcode, c, out var xorValue) && LogicPair(xorValue, LlvmOpcode.Xor, a, b))
                {
                    LogicNot(not, out var inner);
                    return LogicResult(LogicNotInstruction(Builder.CreateAnd(inner, y)), "complex_or_xor_complement");
                }
            }
            if (MatchLogicTripleWithNot(left, flipped, out a, out b, out c, out not))
            {
                if (MatchLogicNotTriple(right, opcode, a, b, c))
                {
                    LlvmValue xor = Builder.CreateXor(b, c);
                    return LogicResult(opcode == LlvmOpcode.Or ? LogicNotInstruction(Builder.CreateOr(xor, a)) : new LlvmBinaryOperator(LlvmOpcode.Or, xor, not), "complex_triple_complement");
                }
                if (MatchLogicNotPair(right, opcode, a, b, true))
                    return LogicResult(new LlvmBinaryOperator(flipped, Builder.CreateBinOp(opcode, c, Builder.CreateNot(b)), not), "complex_triple_absorb_ab");
                if (MatchLogicNotPair(right, opcode, a, c, true))
                    return LogicResult(new LlvmBinaryOperator(flipped, Builder.CreateBinOp(opcode, b, Builder.CreateNot(c)), not), "complex_triple_absorb_ac");
            }
            return null;
        }
    }
}
