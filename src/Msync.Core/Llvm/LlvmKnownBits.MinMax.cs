// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownBits.cpp
// makeGE, umax, umin, smax, smin, flipSignBit.
namespace Msync.Core.Llvm;

public readonly partial struct LlvmKnownBits
{
    public LlvmKnownBits MakeGE(LlvmAPInt value)
    {
        uint leading = (Zero | value).CountLeadingOnes();
        return new(Zero, One | (value & LlvmAPInt.GetHighBitsSet(BitWidth, leading)));
    }
    public static LlvmKnownBits UMax(LlvmKnownBits left, LlvmKnownBits right)
    {
        if (left.GetMinValue().Uge(right.GetMaxValue())) return left;
        if (right.GetMinValue().Uge(left.GetMaxValue())) return right;
        return left.MakeGE(right.GetMinValue()).IntersectWith(right.MakeGE(left.GetMinValue()));
    }
    public static LlvmKnownBits UMin(LlvmKnownBits left, LlvmKnownBits right) => FlipMinMaxBits(UMax(FlipMinMaxBits(left), FlipMinMaxBits(right)));
    public static LlvmKnownBits SMax(LlvmKnownBits left, LlvmKnownBits right) => FlipMinMaxSign(UMax(FlipMinMaxSign(left), FlipMinMaxSign(right)));
    public static LlvmKnownBits SMin(LlvmKnownBits left, LlvmKnownBits right) => FlipMinMaxSign(UMin(FlipMinMaxSign(left), FlipMinMaxSign(right)));
    private static LlvmKnownBits FlipMinMaxBits(LlvmKnownBits value) => new(value.One, value.Zero);
    private static LlvmKnownBits FlipMinMaxSign(LlvmKnownBits value)
    {
        var sign = LlvmAPInt.GetSignedMinValue(value.BitWidth);
        var nonSign = ~sign;
        return new((value.Zero & nonSign) | (value.One & sign), (value.One & nonSign) | (value.Zero & sign));
    }
}
