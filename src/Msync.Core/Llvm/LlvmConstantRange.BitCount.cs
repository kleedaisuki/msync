// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// ConstantRange.cpp cttz/ctpop and unsigned helper ranges.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public LlvmConstantRange CTTZ(bool zeroIsPoison)
    {
        if (IsEmptySet) return GetEmpty();
        var zero = LlvmAPInt.GetZero(BitWidth);
        var one = new LlvmAPInt(BitWidth, 1);
        if (zeroIsPoison && Contains(zero))
        {
            if (Lower.IsZero)
                return Upper.IsOne ? GetEmpty() : UnsignedCountTrailingZerosRange(one, Upper);
            if (Upper.IsOne) return UnsignedCountTrailingZerosRange(Lower, zero);
            return UnsignedCountTrailingZerosRange(Lower, zero).UnionWith(UnsignedCountTrailingZerosRange(one, Upper));
        }
        if (IsFullSet) return GetNonEmpty(zero, Inc(new LlvmAPInt(BitWidth, BitWidth)));
        if (!IsWrappedSet) return UnsignedCountTrailingZerosRange(Lower, Upper);
        return UnsignedCountTrailingZerosRange(Lower, zero).UnionWith(UnsignedCountTrailingZerosRange(zero, Upper));
    }

    private static LlvmConstantRange UnsignedCountTrailingZerosRange(LlvmAPInt lower, LlvmAPInt upper)
    {
        Debug.Assert(!new LlvmConstantRange(lower, upper).IsWrappedSet && lower != upper);
        uint width = lower.BitWidth;
        if (Inc(lower) == upper) return new(new LlvmAPInt(width, lower.CountTrailingZeros()));
        if (lower.IsZero) return new(LlvmAPInt.GetZero(width), new LlvmAPInt(width, width + 1, implicitTrunc: true));
        uint commonPrefix = (lower ^ Dec(upper)).CountLeadingZeros();
        return new(LlvmAPInt.GetZero(width), new LlvmAPInt(width, Math.Max(width - commonPrefix - 1, lower.CountTrailingZeros()) + 1, implicitTrunc: true));
    }

    public LlvmConstantRange CTPop()
    {
        if (IsEmptySet) return GetEmpty();
        var zero = LlvmAPInt.GetZero(BitWidth);
        if (IsFullSet) return GetNonEmpty(zero, Inc(new LlvmAPInt(BitWidth, BitWidth)));
        if (!IsWrappedSet) return UnsignedPopCountRange(Lower, Upper);
        var high = new LlvmConstantRange(new LlvmAPInt(BitWidth, Lower.CountLeadingOnes()), new LlvmAPInt(BitWidth, BitWidth + 1, implicitTrunc: true));
        return high.UnionWith(UnsignedPopCountRange(zero, Upper));
    }

    private static LlvmConstantRange UnsignedPopCountRange(LlvmAPInt lower, LlvmAPInt upper)
    {
        Debug.Assert(!new LlvmConstantRange(lower, upper).IsWrappedSet && lower != upper);
        uint width = lower.BitWidth;
        if (Inc(lower) == upper) return new(new LlvmAPInt(width, lower.PopCount()));
        LlvmAPInt maximum = Dec(upper);
        uint commonPrefix = (lower ^ maximum).CountLeadingZeros();
        uint prefixPopulation = lower.LShr(width - commonPrefix).PopCount();
        uint minimumBits = prefixPopulation + (lower.CountTrailingZeros() < width - commonPrefix ? 1U : 0);
        uint maximumBits = prefixPopulation + width - commonPrefix - (maximum.CountTrailingOnes() < width - commonPrefix ? 1U : 0);
        return new(new LlvmAPInt(width, minimumBits), new LlvmAPInt(width, maximumBits + 1, implicitTrunc: true));
    }
}
