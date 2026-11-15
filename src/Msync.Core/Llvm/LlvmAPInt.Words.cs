// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APInt.cpp tcFullMultiply,
// tcLSB/tcMSB/tcExtract; internal pointer/span adapters for IEEEFloat.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    internal ReadOnlySpan<ulong> MultiWordData => words;
    internal static void FullMultiplyWords(Span<ulong> dst, ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right)
    {
        if (left.Length > right.Length) { FullMultiplyWords(dst, right, left); return; }
        Debug.Assert(dst.Length >= left.Length + right.Length);
        for (int i = 0; i < left.Length; ++i)
            TcMultiplyPart(dst.Slice(i, right.Length + 1), right, left[i], 0, i != 0);
    }
    internal static void ShiftWordsRight(Span<ulong> dst, uint bits) => TcShiftRight(dst, bits);
    internal static void ShiftWordsLeft(Span<ulong> dst, uint bits) => TcShiftLeft(dst, bits);
    internal static uint WordsLSB(ReadOnlySpan<ulong> parts)
    {
        for (int i = 0; i < parts.Length; ++i)
            if (parts[i] != 0) return (uint)BitOperations.TrailingZeroCount(parts[i]) + (uint)i * 64;
        return uint.MaxValue;
    }
    internal static uint WordsMSB(ReadOnlySpan<ulong> parts)
    {
        for (int i = parts.Length - 1; i >= 0; --i)
            if (parts[i] != 0) return (uint)BitOperations.Log2(parts[i]) + (uint)i * 64;
        return uint.MaxValue;
    }
    internal static void ExtractWords(Span<ulong> dst, ReadOnlySpan<ulong> src, uint srcBits, uint srcLSB)
    {
        int dstParts = (int)(((ulong)srcBits + 63) / 64);
        Debug.Assert(dstParts <= dst.Length);
        int first = (int)(srcLSB / 64);
        src.Slice(first, dstParts).CopyTo(dst);
        uint shift = srcLSB % 64;
        TcShiftRight(dst[..dstParts], shift);
        uint n = (uint)dstParts * 64 - shift;
        if (n < srcBits)
            dst[dstParts - 1] |= (src[first + dstParts] & Mask(srcBits - n)) << (int)(n % 64);
        else if (n > srcBits && srcBits % 64 != 0)
            dst[dstParts - 1] &= Mask(srcBits % 64);
        dst[dstParts..].Clear();
    }
}
