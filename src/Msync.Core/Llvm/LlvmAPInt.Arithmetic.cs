// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADT/APInt.h operators;
// Support/APInt.cpp operator+=/-=/*, tcAdd/tcSubtract/tcMultiply/Part, compare.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    private LlvmAPInt(uint bits, ulong[] ownedWords, byte ownershipTag)
    {
        Debug.Assert(bits > 64 && (ulong)ownedWords.Length == ((ulong)bits + 63) / 64);
        BitWidth = bits;
        single = 0;
        words = ownedWords;
        words[^1] &= Mask((bits - 1) % 64 + 1);
    }
    private static LlvmAPInt FromOwnedWords(uint bits, ulong[] ownedWords) => new(bits, ownedWords, 0);

    public static LlvmAPInt operator +(LlvmAPInt left, LlvmAPInt right)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (left.IsSingleWord) return new(left.BitWidth, unchecked(left.single + right.single), implicitTrunc: true);
        ulong[] result = left.ToWords();
        ulong carry = 0;
        unchecked
        {
            for (int i = 0; i < result.Length; ++i)
            {
                ulong previous = result[i];
                if (carry != 0) { result[i] += right.words![i] + 1; carry = result[i] <= previous ? 1UL : 0; }
                else { result[i] += right.words![i]; carry = result[i] < previous ? 1UL : 0; }
            }
        }
        return FromOwnedWords(left.BitWidth, result);
    }

    public static LlvmAPInt operator -(LlvmAPInt left, LlvmAPInt right)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (left.IsSingleWord) return new(left.BitWidth, unchecked(left.single - right.single), implicitTrunc: true);
        ulong[] result = left.ToWords();
        ulong borrow = 0;
        unchecked
        {
            for (int i = 0; i < result.Length; ++i)
            {
                ulong previous = result[i];
                if (borrow != 0) { result[i] -= right.words![i] + 1; borrow = result[i] >= previous ? 1UL : 0; }
                else { result[i] -= right.words![i]; borrow = result[i] > previous ? 1UL : 0; }
            }
        }
        return FromOwnedWords(left.BitWidth, result);
    }

    public static LlvmAPInt operator *(LlvmAPInt left, LlvmAPInt right)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (left.IsSingleWord) return new(left.BitWidth, unchecked(left.single * right.single), implicitTrunc: true);
        ulong[] result = new ulong[left.NumWords];
        for (int i = 0; i < result.Length; ++i)
            TcMultiplyPart(result.AsSpan(i), left.words!, right.words![i], 0, i != 0);
        return FromOwnedWords(left.BitWidth, result);
    }

    private static bool TcMultiplyPart(Span<ulong> dst, ReadOnlySpan<ulong> src, ulong multiplier, ulong carry, bool add)
    {
        Debug.Assert(dst.Length <= src.Length + 1);
        int n = Math.Min(dst.Length, src.Length);
        unchecked
        {
            for (int i = 0; i < n; ++i)
            {
                ulong part = src[i], low, high, mid;
                if (multiplier == 0 || part == 0) { low = carry; high = 0; }
                else
                {
                    low = (part & uint.MaxValue) * (multiplier & uint.MaxValue);
                    high = (part >> 32) * (multiplier >> 32);
                    mid = (part & uint.MaxValue) * (multiplier >> 32);
                    high += mid >> 32;
                    mid <<= 32;
                    if (low + mid < low) ++high;
                    low += mid;
                    mid = (part >> 32) * (multiplier & uint.MaxValue);
                    high += mid >> 32;
                    mid <<= 32;
                    if (low + mid < low) ++high;
                    low += mid;
                    if (low + carry < low) ++high;
                    low += carry;
                }
                if (add)
                {
                    if (low + dst[i] < low) ++high;
                    dst[i] += low;
                }
                else dst[i] = low;
                carry = high;
            }
        }
        if (src.Length < dst.Length) { dst[src.Length] = carry; return false; }
        if (carry != 0) return true;
        if (multiplier != 0)
            for (int i = dst.Length; i < src.Length; ++i)
                if (src[i] != 0) return true;
        return false;
    }

    public static LlvmAPInt operator -(LlvmAPInt value) => ~value + new LlvmAPInt(value.BitWidth, 1, implicitTrunc: true);
    public static LlvmAPInt operator ~(LlvmAPInt value)
    {
        if (value.IsSingleWord) return new(value.BitWidth, ~value.single, implicitTrunc: true);
        ulong[] result = value.ToWords();
        for (int i = 0; i < result.Length; ++i) result[i] = ~result[i];
        return FromOwnedWords(value.BitWidth, result);
    }
    public static LlvmAPInt operator &(LlvmAPInt left, LlvmAPInt right)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (left.IsSingleWord) return new(left.BitWidth, left.single & right.single);
        ulong[] result = left.ToWords();
        for (int i = 0; i < result.Length; ++i) result[i] &= right.words![i];
        return FromOwnedWords(left.BitWidth, result);
    }
    public static LlvmAPInt operator |(LlvmAPInt left, LlvmAPInt right)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (left.IsSingleWord) return new(left.BitWidth, left.single | right.single);
        ulong[] result = left.ToWords();
        for (int i = 0; i < result.Length; ++i) result[i] |= right.words![i];
        return FromOwnedWords(left.BitWidth, result);
    }
    public static LlvmAPInt operator ^(LlvmAPInt left, LlvmAPInt right)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (left.IsSingleWord) return new(left.BitWidth, left.single ^ right.single);
        ulong[] result = left.ToWords();
        for (int i = 0; i < result.Length; ++i) result[i] ^= right.words![i];
        return FromOwnedWords(left.BitWidth, result);
    }

    public int Compare(LlvmAPInt right)
    {
        Debug.Assert(BitWidth == right.BitWidth);
        if (IsSingleWord) return single < right.single ? -1 : single > right.single ? 1 : 0;
        for (int i = words!.Length - 1; i >= 0; --i)
            if (words[i] != right.words![i]) return words[i] > right.words[i] ? 1 : -1;
        return 0;
    }
    public int CompareSigned(LlvmAPInt right)
    {
        Debug.Assert(BitWidth == right.BitWidth);
        if (IsSingleWord)
        {
            long leftSigned = GetSExtValue(), rightSigned = right.GetSExtValue();
            return leftSigned < rightSigned ? -1 : leftSigned > rightSigned ? 1 : 0;
        }
        if (IsNegative != right.IsNegative) return IsNegative ? -1 : 1;
        return Compare(right);
    }
    public bool Ult(LlvmAPInt right) => Compare(right) < 0;
    public bool Ule(LlvmAPInt right) => Compare(right) <= 0;
    public bool Ugt(LlvmAPInt right) => Compare(right) > 0;
    public bool Uge(LlvmAPInt right) => Compare(right) >= 0;
    public bool Slt(LlvmAPInt right) => CompareSigned(right) < 0;
    public bool Sle(LlvmAPInt right) => CompareSigned(right) <= 0;
    public bool Sgt(LlvmAPInt right) => CompareSigned(right) > 0;
    public bool Sge(LlvmAPInt right) => CompareSigned(right) >= 0;
}
