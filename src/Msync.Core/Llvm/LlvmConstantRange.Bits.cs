// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantRange.cpp KnownBits bridge/bitwise.
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public static LlvmConstantRange FromKnownBits(LlvmKnownBits known, bool isSigned)
    {
        if (known.HasConflict) return Empty(known.BitWidth);
        if (known.IsUnknown) return Full(known.BitWidth);
        if (!isSigned || known.IsNegative || known.IsNonNegative)
            return new(known.GetMinValue(), Inc(known.GetMaxValue()));
        var sign = LlvmAPInt.GetSignedMinValue(known.BitWidth);
        return new(known.GetMinValue() | sign, Inc(known.GetMaxValue() & ~sign));
    }
    public LlvmKnownBits ToKnownBits()
    {
        if (IsEmptySet) return new(BitWidth);
        var minimum = GetUnsignedMin();
        var maximum = GetUnsignedMax();
        var known = LlvmKnownBits.MakeConstant(minimum);
        uint differingWidth = (minimum ^ maximum).GetActiveBits();
        return differingWidth == 0 ? known : new(known.Zero.ClearBits(0, differingWidth), known.One.ClearBits(0, differingWidth));
    }
    private static LlvmAPInt EstimateBitMaskedAndLowerBound(LlvmConstantRange left, LlvmConstantRange right)
    {
        uint width = left.BitWidth;
        if (left.IsFullSet || right.IsFullSet || left.IsWrappedSet || right.IsWrappedSet) return LlvmAPInt.GetZero(width);
        var leftLow = left.Lower;
        var leftHigh = Dec(left.Upper);
        var rightLow = right.Lower;
        var rightHigh = Dec(right.Upper);
        var mask = ~((leftLow ^ leftHigh) | (rightLow ^ rightHigh) | (leftLow ^ rightLow));
        mask = mask.ClearBits(0, width - mask.CountLeadingOnes());
        var byLeft = leftLow.ClearBits(0, width - ((rightLow & rightHigh) | mask).CountLeadingOnes());
        var byRight = rightLow.ClearBits(0, width - ((leftLow & leftHigh) | mask).CountLeadingOnes());
        return UMax(byLeft, byRight);
    }
    public LlvmConstantRange BinaryNot() => new LlvmConstantRange(LlvmAPInt.GetAllOnes(BitWidth)).Sub(this);
    public LlvmConstantRange BinaryAnd(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var known = FromKnownBits(ToKnownBits() & other.ToKnownBits(), false);
        var bounds = GetNonEmpty(EstimateBitMaskedAndLowerBound(this, other), Inc(UMin(GetUnsignedMax(), other.GetUnsignedMax())));
        return known.IntersectWith(bounds);
    }
    public LlvmConstantRange BinaryOr(LlvmConstantRange other, bool isDisjoint = false)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var known = FromKnownBits(ToKnownBits() | other.ToKnownBits(), false);
        var upper = -EstimateBitMaskedAndLowerBound(BinaryNot(), other.BinaryNot());
        var bounds = GetNonEmpty(UMax(GetUnsignedMin(), other.GetUnsignedMin()), upper);
        var result = known.IntersectWith(bounds);
        if (isDisjoint) result = AddWithNoWrap(other, NoUnsignedWrap | NoSignedWrap).IntersectWith(result);
        return result;
    }
    public LlvmConstantRange BinaryXor(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (GetSingleElement() is { } left && other.GetSingleElement() is { } right) return new(left ^ right);
        if (other.GetSingleElement() is { IsAllOnes: true }) return BinaryNot();
        if (GetSingleElement() is { IsAllOnes: true }) return other.BinaryNot();
        var leftKnown = ToKnownBits();
        var rightKnown = other.ToKnownBits();
        var result = FromKnownBits(leftKnown ^ rightKnown, false);
        if (BitWidth == 1) return result;
        if (((~leftKnown.Zero) & ~rightKnown.One).IsZero)
            result = result.IntersectWith(other.Sub(this), LlvmPreferredRangeType.Unsigned);
        else if (((~rightKnown.Zero) & ~leftKnown.One).IsZero)
            result = result.IntersectWith(Sub(other), LlvmPreferredRangeType.Unsigned);
        return result;
    }
}
