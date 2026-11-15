// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat handleOverflow, roundAwayFromZero, normalize (IEEE754 semantics).
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    private partial struct IeeeFloat
    {
        private LlvmFloatStatus HandleOverflow(LlvmRoundingMode mode)
        {
            if (mode is LlvmRoundingMode.NearestTiesToEven or LlvmRoundingMode.NearestTiesToAway ||
                (mode == LlvmRoundingMode.TowardPositive && !Sign) ||
                (mode == LlvmRoundingMode.TowardNegative && Sign))
            {
                Category = LlvmFloatCategory.Infinity;
                return LlvmFloatStatus.Overflow | LlvmFloatStatus.Inexact;
            }
            Category = LlvmFloatCategory.Normal;
            Exponent = MaxExponent;
            Significand = (1UL << Precision) - 1;
            return LlvmFloatStatus.Inexact;
        }
        private readonly bool RoundAwayFromZero(LlvmRoundingMode mode, LostFraction fraction, uint bit)
        {
            Debug.Assert(Normal || Category == LlvmFloatCategory.Zero);
            Debug.Assert(fraction != LostFraction.ExactlyZero);
            return mode switch
            {
                LlvmRoundingMode.NearestTiesToAway => fraction is LostFraction.ExactlyHalf or LostFraction.MoreThanHalf,
                LlvmRoundingMode.NearestTiesToEven => fraction == LostFraction.MoreThanHalf ||
                    (fraction == LostFraction.ExactlyHalf && Category != LlvmFloatCategory.Zero &&
                     (Significand & (1UL << (int)bit)) != 0),
                LlvmRoundingMode.TowardZero => false,
                LlvmRoundingMode.TowardPositive => !Sign,
                LlvmRoundingMode.TowardNegative => Sign,
                _ => throw new UnreachableException("Invalid rounding mode found")
            };
        }
        internal LlvmFloatStatus Normalize(LlvmRoundingMode mode, LostFraction fraction)
        {
            if (!Normal) return LlvmFloatStatus.OK;
            uint omsb = unchecked(MSB() + 1);
            if (omsb != 0 || fraction != LostFraction.ExactlyZero)
            {
                int change = (int)omsb - Precision;
                if (Exponent + change > MaxExponent) return HandleOverflow(mode);
                if (Exponent + change < MinExponent) change = MinExponent - Exponent;
                if (change < 0)
                {
                    Debug.Assert(fraction == LostFraction.ExactlyZero);
                    ShiftSignificandLeft((uint)-change);
                    return LlvmFloatStatus.OK;
                }
                if (change > 0)
                {
                    LostFraction shifted = ShiftSignificandRight((uint)change);
                    fraction = Combine(shifted, fraction);
                    omsb = omsb > (uint)change ? omsb - (uint)change : 0;
                }
            }
            if (fraction == LostFraction.ExactlyZero)
            {
                if (omsb == 0) Category = LlvmFloatCategory.Zero;
                return LlvmFloatStatus.OK;
            }
            if (RoundAwayFromZero(mode, fraction, 0))
            {
                if (omsb == 0) Exponent = MinExponent;
                Significand = unchecked(Significand + 1);
                omsb = unchecked(MSB() + 1);
                if (omsb == Precision + 1)
                {
                    if (Exponent == MaxExponent)
                        return HandleOverflow(Sign ? LlvmRoundingMode.TowardNegative : LlvmRoundingMode.TowardPositive);
                    ShiftSignificandRight(1);
                    return LlvmFloatStatus.Inexact;
                }
            }
            if (omsb == Precision) return LlvmFloatStatus.Inexact;
            Debug.Assert(omsb < Precision);
            if (omsb == 0) Category = LlvmFloatCategory.Zero;
            return LlvmFloatStatus.Underflow | LlvmFloatStatus.Inexact;
        }
    }
}
