// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantRange.cpp
// unsignedAddMayOverflow, signedAddMayOverflow, unsignedSubMayOverflow,
// signedSubMayOverflow; Analysis/ValueTracking.h OverflowResult.
namespace Msync.Core.Llvm;

public enum LlvmOverflowResult : byte { AlwaysOverflowsLow, AlwaysOverflowsHigh, MayOverflow, NeverOverflows }

public sealed partial record LlvmConstantRange
{
    public LlvmOverflowResult UnsignedAddMayOverflow(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return LlvmOverflowResult.MayOverflow;
        var min = GetUnsignedMin(); var max = GetUnsignedMax();
        var otherMin = other.GetUnsignedMin(); var otherMax = other.GetUnsignedMax();
        if (min.Ugt(~otherMin)) return LlvmOverflowResult.AlwaysOverflowsHigh;
        return max.Ugt(~otherMax) ? LlvmOverflowResult.MayOverflow : LlvmOverflowResult.NeverOverflows;
    }
    public LlvmOverflowResult SignedAddMayOverflow(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return LlvmOverflowResult.MayOverflow;
        var min = GetSignedMin(); var max = GetSignedMax();
        var otherMin = other.GetSignedMin(); var otherMax = other.GetSignedMax();
        var signedMin = LlvmAPInt.GetSignedMinValue(BitWidth); var signedMax = LlvmAPInt.GetSignedMaxValue(BitWidth);
        if (!min.IsNegative && !otherMin.IsNegative && min.Sgt(signedMax - otherMin)) return LlvmOverflowResult.AlwaysOverflowsHigh;
        if (max.IsNegative && otherMax.IsNegative && max.Slt(signedMin - otherMax)) return LlvmOverflowResult.AlwaysOverflowsLow;
        if (!max.IsNegative && !otherMax.IsNegative && max.Sgt(signedMax - otherMax)) return LlvmOverflowResult.MayOverflow;
        if (min.IsNegative && otherMin.IsNegative && min.Slt(signedMin - otherMin)) return LlvmOverflowResult.MayOverflow;
        return LlvmOverflowResult.NeverOverflows;
    }
    public LlvmOverflowResult UnsignedSubMayOverflow(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return LlvmOverflowResult.MayOverflow;
        var min = GetUnsignedMin(); var max = GetUnsignedMax();
        var otherMin = other.GetUnsignedMin(); var otherMax = other.GetUnsignedMax();
        if (max.Ult(otherMin)) return LlvmOverflowResult.AlwaysOverflowsLow;
        return min.Ult(otherMax) ? LlvmOverflowResult.MayOverflow : LlvmOverflowResult.NeverOverflows;
    }
    public LlvmOverflowResult SignedSubMayOverflow(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return LlvmOverflowResult.MayOverflow;
        var min = GetSignedMin(); var max = GetSignedMax();
        var otherMin = other.GetSignedMin(); var otherMax = other.GetSignedMax();
        var signedMin = LlvmAPInt.GetSignedMinValue(BitWidth); var signedMax = LlvmAPInt.GetSignedMaxValue(BitWidth);
        if (!min.IsNegative && otherMax.IsNegative && min.Sgt(signedMax + otherMax)) return LlvmOverflowResult.AlwaysOverflowsHigh;
        if (max.IsNegative && !otherMin.IsNegative && max.Slt(signedMin + otherMin)) return LlvmOverflowResult.AlwaysOverflowsLow;
        if (!max.IsNegative && otherMin.IsNegative && max.Sgt(signedMax + otherMin)) return LlvmOverflowResult.MayOverflow;
        if (min.IsNegative && !otherMax.IsNegative && min.Slt(signedMin + otherMax)) return LlvmOverflowResult.MayOverflow;
        return LlvmOverflowResult.NeverOverflows;
    }
}
