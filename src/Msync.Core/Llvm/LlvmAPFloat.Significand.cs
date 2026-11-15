// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat addOrSubtractSignificand, multiplySignificand (zero addend),
// divideSignificand. A binary64/temporary-55-bit significand occupies one word.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    private partial struct IeeeFloat
    {
        private ulong SubtractSignificand(IeeeFloat rhs, bool borrow)
        {
            Debug.Assert(Exponent == rhs.Exponent);
            ulong previous = Significand;
            if (borrow)
            {
                Significand = unchecked(Significand - (rhs.Significand + 1));
                return Significand >= previous ? 1UL : 0;
            }
            Significand = unchecked(Significand - rhs.Significand);
            return Significand > previous ? 1UL : 0;
        }
        private ulong AddSignificand(IeeeFloat rhs)
        {
            Debug.Assert(Exponent == rhs.Exponent);
            ulong previous = Significand;
            Significand = unchecked(Significand + rhs.Significand);
            return Significand < previous ? 1UL : 0;
        }
        private LostFraction AddOrSubtractSignificand(IeeeFloat rhs, bool subtract)
        {
            ulong carry = 0;
            LostFraction fraction;
            subtract ^= Sign ^ rhs.Sign;
            int bits = Exponent - rhs.Exponent;
            if (subtract)
            {
                IeeeFloat temporary = rhs;
                bool fractionFromRhs = false;
                if (bits == 0) fraction = LostFraction.ExactlyZero;
                else if (bits > 0)
                {
                    fraction = temporary.ShiftSignificandRight((uint)(bits - 1));
                    fractionFromRhs = true;
                    ShiftSignificandLeft(1);
                }
                else
                {
                    fraction = ShiftSignificandRight((uint)(-bits - 1));
                    temporary.ShiftSignificandLeft(1);
                }
                LlvmFloatCompare comparison = CompareAbsolute(temporary);
                if (comparison == LlvmFloatCompare.LessThan)
                {
                    bool borrow = fraction != LostFraction.ExactlyZero && !fractionFromRhs;
                    if (borrow) fraction = InvertFraction(fraction);
                    carry = temporary.SubtractSignificand(this, borrow);
                    Significand = temporary.Significand;
                    Sign = !Sign;
                }
                else if (comparison == LlvmFloatCompare.GreaterThan)
                {
                    bool borrow = fraction != LostFraction.ExactlyZero && fractionFromRhs;
                    if (borrow) fraction = InvertFraction(fraction);
                    carry = SubtractSignificand(temporary, borrow);
                }
                else
                {
                    Significand = 0;
                    if (fraction != LostFraction.ExactlyZero && fractionFromRhs) Sign = !Sign;
                }
            }
            else
            {
                if (bits > 0)
                {
                    IeeeFloat temporary = rhs;
                    fraction = temporary.ShiftSignificandRight((uint)bits);
                    carry = AddSignificand(temporary);
                }
                else
                {
                    fraction = ShiftSignificandRight((uint)-bits);
                    carry = AddSignificand(rhs);
                }
            }
            Debug.Assert(carry == 0);
            return fraction;
        }
        internal static LostFraction InvertFraction(LostFraction fraction) => fraction switch
        {
            LostFraction.LessThanHalf => LostFraction.MoreThanHalf,
            LostFraction.MoreThanHalf => LostFraction.LessThanHalf,
            _ => fraction
        };
        private LostFraction MultiplySignificand(IeeeFloat rhs)
        {
            Debug.Assert(Precision == rhs.Precision);
            Span<ulong> full = stackalloc ulong[2];
            ReadOnlySpan<ulong> left = [Significand], right = [rhs.Significand];
            LlvmAPInt.FullMultiplyWords(full, left, right);
            LostFraction fraction = LostFraction.ExactlyZero;
            uint omsb = unchecked(LlvmAPInt.WordsMSB(full) + 1);
            Exponent += rhs.Exponent;
            Exponent += 2;
            // The upstream fused-addend block is not entered for multiply:
            // IEEEdouble has zero and its implicit addend is +0.
            Exponent -= Precision + 1;
            if (omsb > Precision)
            {
                uint bits = omsb - (uint)Precision;
                int significantParts = (int)((omsb + 63) / 64);
                LostFraction shifted = TruncatedFraction(full[..significantParts], bits);
                LlvmAPInt.ShiftWordsRight(full[..significantParts], bits);
                fraction = Combine(shifted, fraction);
                Exponent += (int)bits;
            }
            Significand = full[0];
            return fraction;
        }
        private LostFraction DivideSignificand(IeeeFloat rhs)
        {
            ulong dividend = Significand, divisor = rhs.Significand;
            Significand = 0;
            Exponent -= rhs.Exponent;
            uint bit = (uint)Precision - (uint)BitOperations.Log2(divisor) - 1;
            if (bit != 0) { Exponent += (int)bit; divisor <<= (int)bit; }
            bit = (uint)Precision - (uint)BitOperations.Log2(dividend) - 1;
            if (bit != 0) { Exponent -= (int)bit; dividend <<= (int)bit; }
            if (dividend < divisor) { --Exponent; dividend <<= 1; Debug.Assert(dividend >= divisor); }
            for (bit = (uint)Precision; bit != 0; --bit)
            {
                if (dividend >= divisor) { dividend -= divisor; Significand |= 1UL << (int)(bit - 1); }
                dividend <<= 1;
            }
            if (dividend > divisor) return LostFraction.MoreThanHalf;
            if (dividend == divisor) return LostFraction.ExactlyHalf;
            return dividend == 0 ? LostFraction.ExactlyZero : LostFraction.LessThanHalf;
        }
    }
}
