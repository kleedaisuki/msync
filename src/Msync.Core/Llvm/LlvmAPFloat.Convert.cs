// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp IEEEFloat::convert,
// IEEEFloat::next, scalbn and APFloat::getExactInverse (IEEE formats).
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    public LlvmAPFloatConvertResult Convert(LlvmAPFloatSemantics semantics,
        LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(this);
        var format = GetFormat(semantics);
        bool signaling = value.Signaling;
        LostFraction fraction = LostFraction.ExactlyZero;
        int shift = format.Precision - value.Precision;
        if (shift < 0 && value.Normal)
        {
            int omsb = (int)value.MSB() + 1;
            int exponentChange = omsb - value.Precision;
            if (value.Exponent + exponentChange < format.MinExponent)
                exponentChange = format.MinExponent - value.Exponent;
            exponentChange = Math.Max(exponentChange, shift);
            if (exponentChange < 0)
            { shift -= exponentChange; value.Exponent += exponentChange; }
            else if (omsb <= -shift)
            {
                exponentChange = omsb + shift - 1;
                shift -= exponentChange; value.Exponent += exponentChange;
            }
        }
        if (shift < 0 && value.Category is LlvmFloatCategory.Normal or LlvmFloatCategory.NaN)
        {
            fraction = TruncatedFraction(value.Significand, (uint)-shift);
            value.Significand >>= -shift;
        }
        value.Semantics = semantics;
        value.Precision = format.Precision;
        value.MinExponent = format.MinExponent;
        value.MaxExponent = format.MaxExponent;
        if (shift > 0 && value.Category is LlvmFloatCategory.Normal or LlvmFloatCategory.NaN)
            value.Significand <<= shift;
        LlvmFloatStatus status;
        bool losesInfo;
        if (value.Normal)
        {
            status = value.Normalize(mode, fraction);
            losesInfo = status != LlvmFloatStatus.OK;
        }
        else if (value.Category == LlvmFloatCategory.NaN)
        {
            losesInfo = fraction != LostFraction.ExactlyZero;
            if (signaling) { value.MakeQuiet(); status = LlvmFloatStatus.InvalidOp; }
            else status = LlvmFloatStatus.OK;
        }
        else { losesInfo = false; status = LlvmFloatStatus.OK; }
        return new(value.Pack(), status, losesInfo);
    }

    public LlvmAPFloatResult Next(bool down)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.Next(down);
        return new(value.Pack(), status);
    }

    public LlvmAPFloatResult Scalbn(int exponent, LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(this);
        int maxIncrement = value.MaxExponent - (value.MinExponent - (value.Precision - 1)) + 1;
        value.Exponent += Math.Clamp(exponent, -maxIncrement - 1, maxIncrement);
        LlvmFloatStatus status = value.Normalize(mode, LostFraction.ExactlyZero);
        if (value.Category == LlvmFloatCategory.NaN) value.MakeQuiet();
        return new(value.Pack(), status);
    }

    public int GetExactLog2Abs()
    {
        if (!IsFiniteNonZero) return int.MinValue;
        IeeeFloat value = new(this);
        return BitOperations.IsPow2(value.Significand) ? value.ILogB() : int.MinValue;
    }

    public bool GetExactInverse(out LlvmAPFloat inverse)
    {
        inverse = default;
        if (!IsFiniteNonZero || IsDenormal) return false;
        int exponent = GetExactLog2Abs();
        if (exponent == int.MinValue) return false;
        LlvmAPFloat reciprocal = GetOne(Semantics, IsNegative).Scalbn(-exponent, LlvmRoundingMode.TowardZero).Value;
        if (reciprocal.GetExactLog2Abs() != -exponent || reciprocal.IsDenormal) return false;
        Debug.Assert(reciprocal.IsFiniteNonZero);
        inverse = reciprocal;
        return true;
    }

    private partial struct IeeeFloat
    {
        internal LlvmFloatStatus Next(bool down)
        {
            if (down) Sign = !Sign;
            LlvmFloatStatus status = LlvmFloatStatus.OK;
            switch (Category)
            {
                case LlvmFloatCategory.Infinity:
                    if (Sign) this = new IeeeFloat(GetLargest(Semantics, true));
                    break;
                case LlvmFloatCategory.NaN:
                    if (Signaling)
                    {
                        bool sign = Sign;
                        MakeNaN(); Sign = sign;
                        status = LlvmFloatStatus.InvalidOp;
                    }
                    break;
                case LlvmFloatCategory.Zero:
                    this = new IeeeFloat(GetSmallest(Semantics));
                    break;
                case LlvmFloatCategory.Normal:
                    if (Significand == 1 && Exponent == MinExponent && Sign)
                    { Significand = 0; Category = LlvmFloatCategory.Zero; Exponent = 0; break; }
                    ulong integralBit = 1UL << (Precision - 1);
                    ulong allBits = (integralBit << 1) - 1;
                    if (Significand == allBits && Exponent == MaxExponent && !Sign)
                    { Significand = 0; Category = LlvmFloatCategory.Infinity; Exponent = MaxExponent + 1; break; }
                    if (Sign)
                    {
                        bool crossing = Exponent != MinExponent && (Significand & (integralBit - 1)) == 0;
                        --Significand;
                        if (crossing) { Significand |= integralBit; --Exponent; }
                    }
                    else if (Significand >= integralBit && Significand == allBits)
                    { Significand = integralBit; ++Exponent; }
                    else ++Significand;
                    break;
            }
            if (down) Sign = !Sign;
            return status;
        }
    }
}
