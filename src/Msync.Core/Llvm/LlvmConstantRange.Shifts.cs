// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantRange.cpp shifts.
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public LlvmConstantRange Shl(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var minimum = GetUnsignedMin();
        var maximum = GetUnsignedMax();
        if (other.GetSingleElement() is { } shift)
        {
            if (shift.GetLimitedValue() >= BitWidth) return GetEmpty();
            uint leading = (minimum ^ maximum).CountLeadingZeros();
            if (shift.GetLimitedValue() <= leading) return GetNonEmpty(minimum.Shl(shift), Inc(maximum.Shl(shift)));
            return GetNonEmpty(LlvmAPInt.GetZero(BitWidth), Inc(LlvmAPInt.GetBitsSetFrom(BitWidth, (uint)shift.GetZExtValue())));
        }
        var otherMax = other.GetUnsignedMax();
        if (IsAllNegative && otherMax.GetLimitedValue() <= minimum.CountLeadingOnes())
            return GetNonEmpty(minimum.Shl(otherMax), Inc(maximum.Shl(other.GetUnsignedMin())));
        if (otherMax.GetLimitedValue() > maximum.CountLeadingZeros()) return GetFull();
        return GetNonEmpty(minimum.Shl(other.GetUnsignedMin()), Inc(maximum.Shl(otherMax)));
    }
    public LlvmConstantRange LShr(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var maximum = Inc(GetUnsignedMax().LShr(other.GetUnsignedMin()));
        var minimum = GetUnsignedMin().LShr(other.GetUnsignedMax());
        return GetNonEmpty(minimum, maximum);
    }
    public LlvmConstantRange AShr(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var positiveMax = Inc(GetSignedMax().AShr(other.GetUnsignedMin()));
        var positiveMin = GetSignedMin().AShr(other.GetUnsignedMax());
        var negativeMax = Inc(GetSignedMax().AShr(other.GetUnsignedMax()));
        var negativeMin = GetSignedMin().AShr(other.GetUnsignedMin());
        if (!GetSignedMin().IsNegative) return GetNonEmpty(positiveMin, positiveMax);
        if (GetSignedMax().IsNegative) return GetNonEmpty(negativeMin, negativeMax);
        return GetNonEmpty(negativeMin, positiveMax);
    }
    private static LlvmConstantRange ComputeShlNUW(LlvmConstantRange left, LlvmConstantRange right)
    {
        uint width = left.BitWidth;
        var leftMin = left.GetUnsignedMin();
        uint rightMin = (uint)right.GetUnsignedMin().GetLimitedValue(width);
        var minShift = leftMin.UShlOverflow(rightMin, out bool overflow);
        if (overflow) return Empty(width);
        var leftMax = left.GetUnsignedMax();
        uint rightMax = (uint)right.GetUnsignedMax().GetLimitedValue(width);
        var maxShift = minShift;
        uint maxAmount = leftMax.CountLeadingZeros();
        if (rightMin <= maxAmount) maxShift = leftMax.Shl(Math.Min(rightMax, maxAmount));
        rightMin = Math.Max(rightMin, maxAmount + 1);
        rightMax = Math.Min(rightMax, leftMin.CountLeadingZeros());
        if (rightMin <= rightMax) maxShift = UMax(maxShift, LlvmAPInt.GetHighBitsSet(width, width - rightMin));
        return GetNonEmpty(minShift, Inc(maxShift));
    }
    private static LlvmConstantRange ComputeShlNSWNonNegative(LlvmAPInt leftMin, LlvmAPInt leftMax, uint rightMin, uint rightMax)
    {
        uint width = leftMin.BitWidth;
        var minShift = leftMin.SShlOverflow(rightMin, out bool overflow);
        if (overflow) return Empty(width);
        var maxShift = minShift;
        uint maxAmount = leftMax.CountLeadingZeros() - 1;
        if (rightMin <= maxAmount) maxShift = leftMax.Shl(Math.Min(rightMax, maxAmount));
        rightMin = Math.Max(rightMin, maxAmount + 1);
        rightMax = Math.Min(rightMax, leftMin.CountLeadingZeros() - 1);
        if (rightMin <= rightMax) maxShift = UMax(maxShift, LlvmAPInt.GetBitsSet(width, rightMin, width - 1));
        return GetNonEmpty(minShift, Inc(maxShift));
    }
    private static LlvmConstantRange ComputeShlNSWNegative(LlvmAPInt leftMin, LlvmAPInt leftMax, uint rightMin, uint rightMax)
    {
        uint width = leftMin.BitWidth;
        var maxShift = leftMax.SShlOverflow(rightMin, out bool overflow);
        if (overflow) return Empty(width);
        var minShift = maxShift;
        uint maxAmount = leftMin.CountLeadingOnes() - 1;
        if (rightMin <= maxAmount) minShift = leftMin.Shl(Math.Min(rightMax, maxAmount));
        rightMin = Math.Max(rightMin, maxAmount + 1);
        rightMax = Math.Min(rightMax, leftMax.CountLeadingOnes() - 1);
        if (rightMin <= rightMax) minShift = LlvmAPInt.GetSignedMinValue(width);
        return GetNonEmpty(minShift, Inc(maxShift));
    }
    private static LlvmConstantRange ComputeShlNSW(LlvmConstantRange left, LlvmConstantRange right)
    {
        uint width = left.BitWidth;
        uint rightMin = (uint)right.GetUnsignedMin().GetLimitedValue(width);
        uint rightMax = (uint)right.GetUnsignedMax().GetLimitedValue(width);
        var leftMin = left.GetSignedMin();
        var leftMax = left.GetSignedMax();
        if (!leftMin.IsNegative) return ComputeShlNSWNonNegative(leftMin, leftMax, rightMin, rightMax);
        if (leftMax.IsNegative) return ComputeShlNSWNegative(leftMin, leftMax, rightMin, rightMax);
        return ComputeShlNSWNonNegative(LlvmAPInt.GetZero(width), leftMax, rightMin, rightMax)
            .UnionWith(ComputeShlNSWNegative(leftMin, LlvmAPInt.GetAllOnes(width), rightMin, rightMax), LlvmPreferredRangeType.Signed);
    }
    public LlvmConstantRange ShlWithNoWrap(LlvmConstantRange other, uint flags, LlvmPreferredRangeType type = LlvmPreferredRangeType.Smallest)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        return flags switch
        {
            0 => Shl(other), NoSignedWrap => ComputeShlNSW(this, other), NoUnsignedWrap => ComputeShlNUW(this, other),
            BothNoWrap => ComputeShlNSW(this, other).IntersectWith(ComputeShlNUW(this, other), type),
            _ => throw new InvalidOperationException("Invalid NoWrapKind")
        };
    }
}
