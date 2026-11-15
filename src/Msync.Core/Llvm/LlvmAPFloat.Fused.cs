// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat::multiplySignificand(addend) and fusedMultiplyAdd.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    public LlvmAPFloatResult FusedMultiplyAdd(LlvmAPFloat multiplicand, LlvmAPFloat addend,
        LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        Debug.Assert(Semantics == multiplicand.Semantics && Semantics == addend.Semantics);
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.FusedMultiplyAdd(new(multiplicand), new(addend), mode);
        return new(value.Pack(), status);
    }

    private partial struct IeeeFloat
    {
        internal LlvmFloatStatus FusedMultiplyAdd(IeeeFloat multiplicand, IeeeFloat addend, LlvmRoundingMode mode)
        {
            Sign ^= multiplicand.Sign;
            if (Normal && multiplicand.Normal && addend.Category is LlvmFloatCategory.Normal or LlvmFloatCategory.Zero)
            {
                LostFraction fraction = MultiplySignificandWithAddend(multiplicand, addend);
                LlvmFloatStatus status = Normalize(mode, fraction);
                if (fraction != LostFraction.ExactlyZero) status |= LlvmFloatStatus.Inexact;
                if (Category == LlvmFloatCategory.Zero && (status & LlvmFloatStatus.Underflow) == 0 && Sign != addend.Sign)
                    Sign = mode == LlvmRoundingMode.TowardNegative;
                return status;
            }
            LlvmFloatStatus special = MultiplySpecials(multiplicand);
            return special == LlvmFloatStatus.OK ? AddOrSubtract(addend, mode, false) : special;
        }

        private LostFraction MultiplySignificandWithAddend(IeeeFloat rhs, IeeeFloat addend)
        {
            int precision = Precision;
            uint extendedPrecision = (uint)(precision * 2 + 1);
            uint width = ((extendedPrecision + 63) / 64) * 64;
            LlvmAPInt full = new LlvmAPInt(width, Significand) * new LlvmAPInt(width, rhs.Significand);
            uint omsb = full.GetActiveBits();
            Exponent += rhs.Exponent + 2;
            LostFraction fraction = LostFraction.ExactlyZero;
            if (addend.Normal)
            {
                uint shift = extendedPrecision - 1 - omsb;
                full = full.Shl(shift);
                Exponent -= (int)shift;
                WideSignificand product = new(full, Exponent, Sign);
                WideSignificand extra = new(new LlvmAPInt(width, addend.Significand).Shl((uint)precision + 1),
                    addend.Exponent, addend.Sign);
                LostFraction shifted = extra.ShiftRight(1);
                Debug.Assert(shifted == LostFraction.ExactlyZero);
                fraction = product.AddOrSubtract(extra);
                Exponent = product.Exponent; Sign = product.Sign; full = product.Significand;
                omsb = full.GetActiveBits();
            }
            Exponent -= precision + 1;
            if (omsb > precision)
            {
                uint shift = omsb - (uint)precision;
                fraction = Combine(WideSignificand.Truncated(full, shift), fraction);
                full = full.LShr(shift);
                Exponent += (int)shift;
            }
            Significand = full.GetWord(0);
            return fraction;
        }
    }

    // Source tc* operations on the temporary 2*precision+1 significand. Public
    // arithmetic remains on its native one-word IEEE representation.
    private struct WideSignificand(LlvmAPInt significand, int exponent, bool sign)
    {
        internal LlvmAPInt Significand = significand;
        internal int Exponent = exponent;
        internal bool Sign = sign;

        internal LostFraction ShiftRight(uint amount)
        {
            Exponent += (int)amount;
            LostFraction fraction = Truncated(Significand, amount);
            Significand = Significand.LShr(Math.Min(amount, Significand.BitWidth));
            return fraction;
        }
        private void ShiftLeft(uint amount)
        {
            Significand = Significand.Shl(amount);
            Exponent -= (int)amount;
        }
        private readonly int CompareAbsolute(WideSignificand rhs) => Exponent == rhs.Exponent
            ? Significand.Compare(rhs.Significand) : Exponent.CompareTo(rhs.Exponent);

        internal LostFraction AddOrSubtract(WideSignificand rhs)
        {
            int bits = Exponent - rhs.Exponent;
            LostFraction fraction;
            if (Sign != rhs.Sign)
            {
                bool fromRhs = false;
                if (bits == 0) fraction = LostFraction.ExactlyZero;
                else if (bits > 0)
                { fraction = rhs.ShiftRight((uint)(bits - 1)); fromRhs = true; ShiftLeft(1); }
                else { fraction = ShiftRight((uint)(-bits - 1)); rhs.ShiftLeft(1); }
                int comparison = CompareAbsolute(rhs);
                if (comparison < 0)
                {
                    bool borrow = fraction != LostFraction.ExactlyZero && !fromRhs;
                    if (borrow) fraction = IeeeFloat.InvertFraction(fraction);
                    Significand = rhs.Significand - Significand - new LlvmAPInt(Significand.BitWidth, borrow ? 1UL : 0);
                    Sign = !Sign;
                }
                else if (comparison > 0)
                {
                    bool borrow = fraction != LostFraction.ExactlyZero && fromRhs;
                    if (borrow) fraction = IeeeFloat.InvertFraction(fraction);
                    Significand -= rhs.Significand + new LlvmAPInt(Significand.BitWidth, borrow ? 1UL : 0);
                }
                else
                {
                    Significand = LlvmAPInt.GetZero(Significand.BitWidth);
                    if (fraction != LostFraction.ExactlyZero && fromRhs) Sign = !Sign;
                }
            }
            else
            {
                if (bits > 0) fraction = rhs.ShiftRight((uint)bits);
                else fraction = ShiftRight((uint)-bits);
                Significand += rhs.Significand;
            }
            return fraction;
        }
        internal static LostFraction Truncated(LlvmAPInt value, uint amount)
        {
            uint lsb = value.IsZero ? uint.MaxValue : value.CountTrailingZeros();
            if (amount <= lsb) return LostFraction.ExactlyZero;
            if (amount == lsb + 1) return LostFraction.ExactlyHalf;
            if (amount <= value.BitWidth && value.ExtractBits(1, amount - 1).IsOne) return LostFraction.MoreThanHalf;
            return LostFraction.LessThanHalf;
        }
    }
}
