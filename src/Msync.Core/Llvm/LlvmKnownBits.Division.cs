// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: lib/Support/KnownBits.cpp.
namespace Msync.Core.Llvm;

public readonly partial struct LlvmKnownBits
{
    private static LlvmKnownBits DivLowBit(LlvmKnownBits known, LlvmKnownBits left, LlvmKnownBits right, bool exact)
    {
        if (!exact) return known;
        uint width = known.BitWidth;
        LlvmAPInt zero = known.Zero, one = known.One;
        if (!(left.One & new LlvmAPInt(width, 1)).IsZero) one |= new LlvmAPInt(width, 1);
        int min = (int)left.CountMinTrailingZeros() - (int)right.CountMaxTrailingZeros();
        int max = (int)left.CountMaxTrailingZeros() - (int)right.CountMinTrailingZeros();
        if (min >= 0)
        {
            zero |= LlvmAPInt.GetLowBitsSet(width, (uint)min);
            if (min == max) one |= LlvmAPInt.GetOneBitSet(width, (uint)min);
        }
        else if (max < 0) return MakeConstant(LlvmAPInt.GetZero(width));
        return new LlvmKnownBits(zero, one).ResolveConflict();
    }
    public static LlvmKnownBits UDiv(LlvmKnownBits left, LlvmKnownBits right, bool exact = false)
    {
        uint width = left.BitWidth;
        if (left.IsZero || right.IsZero) return MakeConstant(LlvmAPInt.GetZero(width));
        LlvmAPInt denominator = right.One;
        if (denominator.IsZero) denominator |= LlvmAPInt.GetOneBitSet(width, right.CountMinTrailingZeros());
        LlvmAPInt max = left.GetMaxValue().UDiv(denominator);
        LlvmAPInt.UDivRem(left.One, right.GetMaxValue(), out LlvmAPInt min, out LlvmAPInt rem);
        if (exact && !rem.IsZero) min += new LlvmAPInt(width, 1);
        LlvmAPInt mask = ~LlvmAPInt.GetLowBitsSet(width, (min ^ max).GetActiveBits());
        return DivLowBit(new(~min & mask, min & mask), left, right, exact);
    }
    public static LlvmKnownBits SDiv(LlvmKnownBits left, LlvmKnownBits right, bool exact = false)
    {
        if (left.IsNonNegative && right.IsNonNegative) return UDiv(left, right, exact);
        uint width = left.BitWidth;
        if (left.IsZero || right.IsZero) return MakeConstant(LlvmAPInt.GetZero(width));
        LlvmAPInt? bound = null;
        if (left.IsNegative && right.IsNegative)
        {
            LlvmAPInt num = left.GetSignedMinValue(), denom = right.GetSignedMaxValue();
            bound = num == LlvmAPInt.GetSignedMinValue(width) && denom.IsAllOnes ? LlvmAPInt.GetSignedMaxValue(width) : num.SDiv(denom);
        }
        else if (left.IsNegative && right.IsNonNegative && (exact || (-left.GetSignedMaxValue()).Uge(right.GetSignedMaxValue())))
        {
            LlvmAPInt denom = right.GetSignedMinValue();
            if (denom.IsZero) denom |= LlvmAPInt.GetOneBitSet(width, right.CountMinTrailingZeros());
            bound = left.GetSignedMinValue().SDiv(denom);
        }
        else if (left.IsStrictlyPositive && right.IsNegative && (exact || left.GetSignedMinValue().Uge(-right.GetSignedMinValue())))
            bound = left.GetSignedMaxValue().SDiv(right.GetSignedMaxValue());
        LlvmKnownBits known = new(width);
        if (bound is LlvmAPInt value)
            known = value.IsNegative ? new(known.Zero, LlvmAPInt.GetHighBitsSet(width, value.CountLeadingOnes())) : new(LlvmAPInt.GetHighBitsSet(width, value.CountLeadingZeros()), known.One);
        return DivLowBit(known, left, right, exact);
    }
    private static LlvmKnownBits RemLowBits(LlvmKnownBits left, LlvmKnownBits right)
    {
        if (right.IsZero || (right.Zero & new LlvmAPInt(right.BitWidth, 1)).IsZero) return new(left.BitWidth);
        LlvmAPInt mask = LlvmAPInt.GetLowBitsSet(left.BitWidth, right.CountMinTrailingZeros());
        return new(left.Zero & mask, left.One & mask);
    }
    public static LlvmKnownBits URem(LlvmKnownBits left, LlvmKnownBits right)
    {
        if (left.GetMaxValue().Ult(right.One)) return left;
        LlvmKnownBits known = RemLowBits(left, right);
        LlvmAPInt max = right.GetMaxValue() - new LlvmAPInt(left.BitWidth, 1);
        if (left.GetMaxValue().Ult(max)) max = left.GetMaxValue();
        return new(known.Zero | LlvmAPInt.GetHighBitsSet(left.BitWidth, max.CountLeadingZeros()), known.One);
    }
    public static LlvmKnownBits SRem(LlvmKnownBits left, LlvmKnownBits right)
    {
        LlvmKnownBits known = RemLowBits(left, right);
        uint width = left.BitWidth;
        if (right.IsConstant && right.One.IsPowerOf2)
        {
            LlvmAPInt mask = right.One - new LlvmAPInt(width, 1);
            if (left.IsNonNegative || (mask & left.Zero) == mask) known = new(known.Zero | ~mask, known.One);
            if (left.IsNegative && !(mask & left.One).IsZero) known = new(known.Zero, known.One | ~mask);
            return known;
        }
        if (left.IsNegative && known.IsNonZero) return new(known.Zero, known.One | LlvmAPInt.GetHighBitsSet(width, Math.Max(left.CountMinLeadingOnes(), right.CountMinSignBits())));
        if (left.IsNonNegative) return new(known.Zero | LlvmAPInt.GetHighBitsSet(width, Math.Max(left.CountMinLeadingZeros(), right.CountMinSignBits())), known.One);
        return known;
    }
    public static LlvmKnownBits Shl(LlvmKnownBits left, LlvmKnownBits right, bool nuw = false, bool nsw = false, bool nonZero = false) => Shift(LlvmOpcode.Shl, left, right, nuw, nsw, nonZero: nonZero);
}
