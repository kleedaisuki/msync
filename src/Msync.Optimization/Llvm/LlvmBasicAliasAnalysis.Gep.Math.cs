// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/APInt.cpp
// APIntOps::GreatestCommonDivisor (unsigned); IR/ConstantRange.cpp smul_fast/smul_sat.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    private static LlvmAPInt GepGcd(LlvmAPInt a, LlvmAPInt b)
    {
        if (a == b) return a;
        if (a.IsZero) return b;
        if (b.IsZero) return a;
        uint aZeros = a.CountTrailingZeros(), bZeros = b.CountTrailingZeros();
        uint common;
        if (aZeros > bZeros) { a = a.LShr(aZeros - bZeros); common = bZeros; }
        else if (bZeros > aZeros) { b = b.LShr(bZeros - aZeros); common = aZeros; }
        else common = aZeros;
        while (a != b)
        {
            if (a.Ugt(b)) { a -= b; a = a.LShr(a.CountTrailingZeros() - common); }
            else { b -= a; b = b.LShr(b.CountTrailingZeros() - common); }
        }
        return a;
    }

    private static LlvmConstantRange GepMulFast(LlvmConstantRange left, LlvmConstantRange right)
    {
        if (left.IsEmptySet || right.IsEmptySet) return LlvmConstantRange.Empty(left.BitWidth);
        var min = left.GetSignedMin(); var max = left.GetSignedMax();
        var otherMin = right.GetSignedMin(); var otherMax = right.GetSignedMax();
        var a = min.SMulOverflow(otherMin, out bool o1);
        var b = min.SMulOverflow(otherMax, out bool o2);
        var c = max.SMulOverflow(otherMin, out bool o3);
        var d = max.SMulOverflow(otherMax, out bool o4);
        if (o1 || o2 || o3 || o4) return LlvmConstantRange.Full(left.BitWidth);
        return GepProductBounds(a, b, c, d);
    }

    private static LlvmConstantRange GepMulSat(LlvmConstantRange left, LlvmConstantRange right)
    {
        if (left.IsEmptySet || right.IsEmptySet) return LlvmConstantRange.Empty(left.BitWidth);
        var min = left.GetSignedMin(); var max = left.GetSignedMax();
        var otherMin = right.GetSignedMin(); var otherMax = right.GetSignedMax();
        return GepProductBounds(min.SMulSat(otherMin), min.SMulSat(otherMax), max.SMulSat(otherMin), max.SMulSat(otherMax));
    }

    private static LlvmConstantRange GepProductBounds(LlvmAPInt a, LlvmAPInt b, LlvmAPInt c, LlvmAPInt d)
    {
        var minimum = a.Slt(b) ? a : b;
        var maximum = a.Sgt(b) ? a : b;
        if (c.Slt(minimum)) minimum = c;
        if (d.Slt(minimum)) minimum = d;
        if (c.Sgt(maximum)) maximum = c;
        if (d.Sgt(maximum)) maximum = d;
        return LlvmConstantRange.GetNonEmpty(minimum, maximum + new LlvmAPInt(maximum.BitWidth, 1));
    }
}
