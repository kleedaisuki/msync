// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// MatchMul/MatchRem/MatchDiv/MulWillOverflow/SimplifyAddWithRemainder.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MatchAddSubMul(LlvmValue value, out LlvmValue operand, out LlvmAPInt coefficient)
        {
            operand = null!; coefficient = default;
            if (value is not LlvmBinaryOperator { Right: LlvmConstantInt constant } binary) return false;
            if (binary.Opcode == LlvmOpcode.Mul)
            { operand = binary.Left; coefficient = constant.Value; return true; }
            if (binary.Opcode != LlvmOpcode.Shl) return false;
            operand = binary.Left;
            coefficient = new LlvmAPInt(constant.Value.BitWidth, 1).Shl(constant.Value);
            return true;
        }
        private static bool MatchAddSubRem(LlvmValue value, out LlvmValue operand, out LlvmAPInt divisor, out bool signed)
        {
            operand = null!; divisor = default; signed = false;
            if (value is not LlvmBinaryOperator { Right: LlvmConstantInt constant } binary) return false;
            signed = binary.Opcode == LlvmOpcode.SRem;
            if (signed || binary.Opcode == LlvmOpcode.URem)
            { operand = binary.Left; divisor = constant.Value; return true; }
            if (binary.Opcode != LlvmOpcode.And) return false;
            divisor = constant.Value + new LlvmAPInt(constant.Value.BitWidth, 1);
            if (!divisor.IsPowerOf2) return false;
            operand = binary.Left;
            return true;
        }
        private static bool MatchAddSubDiv(LlvmValue value, out LlvmValue operand, out LlvmAPInt divisor, bool signed)
        {
            operand = null!; divisor = default;
            if (value is not LlvmBinaryOperator { Right: LlvmConstantInt constant } binary) return false;
            if (binary.Opcode == (signed ? LlvmOpcode.SDiv : LlvmOpcode.UDiv))
            { operand = binary.Left; divisor = constant.Value; return true; }
            if (signed || binary.Opcode != LlvmOpcode.LShr) return false;
            operand = binary.Left;
            divisor = new LlvmAPInt(constant.Value.BitWidth, 1).Shl(constant.Value);
            return true;
        }
        private static bool AddSubMulWillOverflow(LlvmAPInt lhs, LlvmAPInt rhs, bool signed)
        {
            bool overflow;
            if (signed) lhs.SMulOverflow(rhs, out overflow);
            else lhs.UMulOverflow(rhs, out overflow);
            return overflow;
        }
        private LlvmValue? SimplifyAddWithRemainder(LlvmBinaryOperator instruction)
        {
            Count("instcombine.addsub.remainder.attempt");
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (MatchRemainderProduct(left, right, out var dividend, out var divisor, out var product, out bool signed) ||
                MatchRemainderProduct(right, left, out dividend, out divisor, out product, out signed))
            {
                if (MatchAddSubRem(product, out var quotient, out var secondDivisor, out bool secondSigned) &&
                    signed == secondSigned && MatchAddSubDiv(quotient, out var original, out var firstDivisor, signed) &&
                    ReferenceEquals(dividend, original) && divisor == firstDivisor &&
                    !AddSubMulWillOverflow(divisor, secondDivisor, signed))
                {
                    Count("instcombine.addsub.remainder.nested_divisor");
                    return Builder.CreateBinary(signed ? LlvmOpcode.SRem : LlvmOpcode.URem, dividend,
                        LlvmConstantInt.Get(dividend.Context, divisor * secondDivisor));
                }
            }
            return FoldDivRem(left, right, instruction) ?? FoldDivRem(right, left, instruction);
        }
        private static bool MatchRemainderProduct(LlvmValue remainder, LlvmValue multiply,
            out LlvmValue dividend, out LlvmAPInt divisor, out LlvmValue product, out bool signed)
        {
            product = null!;
            return MatchAddSubRem(remainder, out dividend, out divisor, out signed) &&
                MatchAddSubMul(multiply, out product, out var multiplier) && divisor == multiplier;
        }
        private LlvmValue? FoldDivRem(LlvmValue divSide, LlvmValue remSide, LlvmBinaryOperator instruction)
        {
            var one = new LlvmAPInt(instruction.Type.GetScalarSizeInBits(), 1);
            if (!divSide.HasOneUse || !MatchAddSubMul(divSide, out var quotient, out var divCoefficient))
            { quotient = divSide; divCoefficient = one; }
            if (!remSide.HasOneUse || !MatchAddSubMul(remSide, out var remainder, out var remCoefficient))
            { remainder = remSide; remCoefficient = one; }
            if (!MatchAddSubRem(remainder, out var dividend, out var divisor, out bool signed) ||
                !MatchAddSubDiv(quotient, out var original, out var quotientDivisor, signed) ||
                !ReferenceEquals(dividend, original) || divisor != quotientDivisor ||
                (divCoefficient.IsOne && !signed && divisor.IsPowerOf2 && divisor != new LlvmAPInt(divisor.BitWidth, 2))) return null;
            LlvmAPInt coefficient = divCoefficient - remCoefficient * divisor;
            if (!coefficient.IsZero && !remainder.HasOneUse) return null;
            if (!LlvmValueTracking.IsGuaranteedNotToBeUndef(dividend, DT, instruction, Metrics, assumptions: AC))
            { Count("instcombine.addsub.remainder.undef"); return null; }
            LlvmValue multiplied = Builder.CreateMul(dividend, LlvmConstantInt.Get(dividend.Context, remCoefficient));
            Count("instcombine.addsub.remainder.divrem_combined");
            if (coefficient.IsZero) return multiplied;
            return Builder.CreateAdd(Builder.CreateMul(quotient, LlvmConstantInt.Get(dividend.Context, coefficient)), multiplied);
        }
    }
}
