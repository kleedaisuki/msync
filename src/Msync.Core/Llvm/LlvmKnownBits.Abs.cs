// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownBits.cpp abs.
namespace Msync.Core.Llvm;

public readonly partial struct LlvmKnownBits
{
    public LlvmKnownBits Abs(bool intMinIsPoison = false)
    {
        if (IsNonNegative) return this;
        if (IsNegative)
        {
            var temporary = this;
            if (intMinIsPoison && Zero.PopCount() + 2 == BitWidth)
                temporary = new(Zero, One | LlvmAPInt.GetOneBitSet(BitWidth, CountMinTrailingZeros()));
            var result = AddSub(false, intMinIsPoison, false, MakeConstant(LlvmAPInt.GetZero(BitWidth)), temporary);
            if (intMinIsPoison && temporary.One.PopCount() == 1 && (~temporary.Zero).PopCount() != 1)
            {
                temporary = new(temporary.Zero | LlvmAPInt.GetSignedMinValue(BitWidth), temporary.One & LlvmAPInt.GetSignedMaxValue(BitWidth));
                var high = LlvmAPInt.GetHighBitsSet(BitWidth, temporary.CountMinLeadingZeros()) & LlvmAPInt.GetSignedMaxValue(BitWidth);
                result = new(result.Zero, result.One | high);
            }
            return result;
        }
        uint min = CountMinTrailingZeros(), max = CountMaxTrailingZeros();
        var known = new LlvmKnownBits(LlvmAPInt.GetLowBitsSet(BitWidth, min), max == min && max < BitWidth ? LlvmAPInt.GetOneBitSet(BitWidth, max) : LlvmAPInt.GetZero(BitWidth));
        return intMinIsPoison || (!One.IsZero && !One.IsMinSignedValue) ? known.WithNonNegative() : known;
    }
}
