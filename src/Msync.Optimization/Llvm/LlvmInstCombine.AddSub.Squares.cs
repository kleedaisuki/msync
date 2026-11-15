// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// matchesSquareSum/foldSquareSumInt/foldSquareSumFP.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MatchSquareDouble(LlvmValue value, bool floating, out LlvmValue operand)
        {
            operand = null!;
            if (value is not LlvmBinaryOperator binary || binary.Opcode != (floating ? LlvmOpcode.FMul : LlvmOpcode.Shl)) return false;
            bool matched = floating ? binary.Right is LlvmConstantFP fp && fp.Value.BitwiseIsEqual(LlvmConstantFP.Get(fp.Type, 2).Value) :
                binary.Right is LlvmConstantInt { IsOne: true };
            if (matched) operand = binary.Left;
            return matched;
        }
        private static bool MatchSquare(LlvmValue value, LlvmOpcode multiply, LlvmValue operand) =>
            value is LlvmBinaryOperator binary && binary.Opcode == multiply && ReferenceEquals(binary.Left, operand) && ReferenceEquals(binary.Right, operand);
        private static bool MatchesSquareSum(LlvmBinaryOperator instruction, bool floating, out LlvmValue a, out LlvmValue b)
        {
            a = null!; b = null!;
            var multiply = floating ? LlvmOpcode.FMul : LlvmOpcode.Mul;
            var add = floating ? LlvmOpcode.FAdd : LlvmOpcode.Add;
            for (int outerSide = 0; outerSide != 2; ++outerSide)
            {
                LlvmValue first = outerSide == 0 ? instruction.Left : instruction.Right;
                LlvmValue second = outerSide == 0 ? instruction.Right : instruction.Left;
                if (first is LlvmBinaryOperator square && square.Opcode == multiply && ReferenceEquals(square.Left, square.Right) &&
                    second is LlvmBinaryOperator product && product.Opcode == multiply && product.HasOneUse)
                {
                    a = square.Left;
                    for (int productSide = 0; productSide != 2; ++productSide)
                    {
                        LlvmValue sumValue = productSide == 0 ? product.Left : product.Right;
                        b = productSide == 0 ? product.Right : product.Left;
                        if (MatchAddSubCommuted(sumValue, add, b, out var doubled) &&
                            MatchSquareDouble(doubled, floating, out var twiceOperand) && ReferenceEquals(twiceOperand, a)) return true;
                    }
                }
                if (!first.HasOneUse || !second.HasOneUse) continue;
                if (MatchSquareDouble(first, floating, out var multiplied) && multiplied is LlvmBinaryOperator ab && ab.Opcode == multiply)
                { a = ab.Left; b = ab.Right; }
                else if (first is LlvmBinaryOperator product2 && product2.Opcode == multiply)
                {
                    if (MatchSquareDouble(product2.Left, floating, out a)) b = product2.Right;
                    else if (MatchSquareDouble(product2.Right, floating, out a)) b = product2.Left;
                    else continue;
                }
                else continue;
                if (second is not LlvmBinaryOperator sum || sum.Opcode != add) continue;
                if (MatchSquare(sum.Left, multiply, a) && MatchSquare(sum.Right, multiply, b) ||
                    MatchSquare(sum.Right, multiply, a) && MatchSquare(sum.Left, multiply, b)) return true;
            }
            return false;
        }
        private LlvmInstruction? FoldSquareSumInt(LlvmBinaryOperator instruction)
        {
            if (!MatchesSquareSum(instruction, false, out var a, out var b)) return null;
            var sum = Builder.CreateAdd(a, b);
            Count("instcombine.addsub.square_integer");
            return new LlvmBinaryOperator(LlvmOpcode.Mul, sum, sum);
        }
        private LlvmInstruction? FoldSquareSumFP(LlvmBinaryOperator instruction)
        {
            if (!MatchesSquareSum(instruction, true, out var a, out var b)) return null;
            var sum = BuildAddSubFP(LlvmOpcode.FAdd, a, b, instruction.FastMathFlags);
            Count("instcombine.addsub.square_float");
            return NewAddSubFP(LlvmOpcode.FMul, sum, sum, instruction.FastMathFlags);
        }
    }
}
