// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/APInt.cpp
// udiv/urem/sdiv/srem and udivrem/sdivrem (APInt operands), divide.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    public LlvmAPInt UDiv(LlvmAPInt right)
    {
        Debug.Assert(BitWidth == right.BitWidth);
        if (IsSingleWord) { Debug.Assert(right.single != 0); return new(BitWidth, single / right.single); }
        int leftWords = (int)((GetActiveBits() + 63UL) / 64);
        uint rightBits = right.GetActiveBits();
        int rightWords = (int)((rightBits + 63UL) / 64);
        Debug.Assert(rightWords != 0);
        if (leftWords == 0) return GetZero(BitWidth);
        if (rightBits == 1) return this;
        if (leftWords < rightWords || Ult(right)) return GetZero(BitWidth);
        if (this == right) return new(BitWidth, 1);
        if (leftWords == 1) return new(BitWidth, words![0] / right.words![0]);
        ulong[] result = new ulong[NumWords];
        DivideWords(words.AsSpan(0, leftWords), right.words.AsSpan(0, rightWords), result, default);
        return FromOwnedWords(BitWidth, result);
    }

    public LlvmAPInt URem(LlvmAPInt right)
    {
        Debug.Assert(BitWidth == right.BitWidth);
        if (IsSingleWord) { Debug.Assert(right.single != 0); return new(BitWidth, single % right.single); }
        int leftWords = (int)((GetActiveBits() + 63UL) / 64);
        uint rightBits = right.GetActiveBits();
        int rightWords = (int)((rightBits + 63UL) / 64);
        Debug.Assert(rightWords != 0);
        if (leftWords == 0 || rightBits == 1) return GetZero(BitWidth);
        if (leftWords < rightWords || Ult(right)) return this;
        if (this == right) return GetZero(BitWidth);
        if (leftWords == 1) return new(BitWidth, words![0] % right.words![0]);
        if (right.IsPowerOf2) return ClearBits(right.GetActiveBits() - 1, BitWidth);
        ulong[] result = new ulong[NumWords];
        DivideWords(words.AsSpan(0, leftWords), right.words.AsSpan(0, rightWords), default, result);
        return FromOwnedWords(BitWidth, result);
    }
    public LlvmAPInt SDiv(LlvmAPInt right)
    {
        if (IsNegative) return right.IsNegative ? (-this).UDiv(-right) : -((-this).UDiv(right));
        return right.IsNegative ? -UDiv(-right) : UDiv(right);
    }
    public LlvmAPInt SRem(LlvmAPInt right)
    {
        if (IsNegative) return right.IsNegative ? -((-this).URem(-right)) : -((-this).URem(right));
        return right.IsNegative ? URem(-right) : URem(right);
    }
    public static void UDivRem(LlvmAPInt left, LlvmAPInt right, out LlvmAPInt quotient, out LlvmAPInt remainder)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        uint bits = left.BitWidth;
        if (left.IsSingleWord)
        {
            Debug.Assert(right.single != 0);
            quotient = new(bits, left.single / right.single);
            remainder = new(bits, left.single % right.single);
            return;
        }
        int leftWords = (int)((left.GetActiveBits() + 63UL) / 64);
        uint rightBits = right.GetActiveBits();
        int rightWords = (int)((rightBits + 63UL) / 64);
        Debug.Assert(rightWords != 0);
        if (leftWords == 0) { quotient = remainder = GetZero(bits); return; }
        if (rightBits == 1) { quotient = left; remainder = GetZero(bits); }
        if (leftWords < rightWords || left.Ult(right)) { quotient = GetZero(bits); remainder = left; return; }
        if (left == right) { quotient = new(bits, 1); remainder = GetZero(bits); return; }
        if (leftWords == 1)
        {
            quotient = new(bits, left.words![0] / right.words![0]);
            remainder = new(bits, left.words[0] % right.words[0]);
            return;
        }
        ulong[] q = new ulong[left.NumWords], r = new ulong[left.NumWords];
        DivideWords(left.words.AsSpan(0, leftWords), right.words.AsSpan(0, rightWords), q, r);
        quotient = FromOwnedWords(bits, q);
        remainder = FromOwnedWords(bits, r);
    }
    public static void SDivRem(LlvmAPInt left, LlvmAPInt right, out LlvmAPInt quotient, out LlvmAPInt remainder)
    {
        if (left.IsNegative)
        {
            if (right.IsNegative) UDivRem(-left, -right, out quotient, out remainder);
            else { UDivRem(-left, right, out quotient, out remainder); quotient = -quotient; }
            remainder = -remainder;
        }
        else if (right.IsNegative) { UDivRem(left, -right, out quotient, out remainder); quotient = -quotient; }
        else UDivRem(left, right, out quotient, out remainder);
    }

    public bool IsPowerOf2
    {
        get
        {
            if (IsSingleWord) return BitOperations.IsPow2(single);
            int count = 0;
            foreach (ulong word in words!) { count += BitOperations.PopCount(word); if (count > 1) return false; }
            return count == 1;
        }
    }
    public LlvmAPInt ClearBits(uint lowBit, uint highBit)
    {
        Debug.Assert(lowBit <= highBit && highBit <= BitWidth);
        if (lowBit == highBit) return this;
        if (IsSingleWord)
        {
            ulong mask = Mask(highBit) & ~Mask(lowBit);
            return new(BitWidth, single & ~mask);
        }
        ulong[] result = ToWords();
        int lowWord = (int)(lowBit / 64), highWord = (int)(highBit / 64);
        ulong lowMask = ~(ulong.MaxValue << (int)(lowBit % 64));
        int highShift = (int)(highBit % 64);
        if (highShift != 0)
        {
            ulong highMask = ~(ulong.MaxValue >> (64 - highShift));
            if (highWord == lowWord) lowMask |= highMask;
            else result[highWord] &= highMask;
        }
        result[lowWord] &= lowMask;
        for (int word = lowWord + 1; word < highWord; ++word) result[word] = 0;
        return FromOwnedWords(BitWidth, result);
    }

    private static void DivideWords(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right,
        Span<ulong> quotient, Span<ulong> remainder)
    {
        Debug.Assert(left.Length >= right.Length);
        int n = right.Length * 2, m = left.Length * 2 - n;
        int spaceSize = (remainder.IsEmpty ? 3 : 4) * n + 2 * m + 1;
        Span<uint> space = spaceSize <= 128 ? stackalloc uint[128] : new uint[spaceSize];
        Span<uint> u = space[..(m + n + 1)], v = space.Slice(m + n + 1, n);
        Span<uint> q = space.Slice(m + 2 * n + 1, m + n);
        Span<uint> r = remainder.IsEmpty ? default : space.Slice(2 * m + 3 * n + 1, n);
        u.Clear(); v.Clear(); q.Clear(); r.Clear();
        for (int i = 0; i < left.Length; ++i) { u[i * 2] = (uint)left[i]; u[i * 2 + 1] = (uint)(left[i] >> 32); }
        for (int i = 0; i < right.Length; ++i) { v[i * 2] = (uint)right[i]; v[i * 2 + 1] = (uint)(right[i] >> 32); }
        for (int i = n; i > 0 && v[i - 1] == 0; --i) { --n; ++m; }
        for (int i = m + n; i > 0 && u[i - 1] == 0; --i) --m;
        Debug.Assert(n != 0);
        if (n == 1)
        {
            uint divisor = v[0], rem = 0;
            for (int i = m; i >= 0; --i)
            {
                ulong partial = ((ulong)rem << 32) | u[i];
                if (partial == 0) { q[i] = 0; rem = 0; }
                else if (partial < divisor) { q[i] = 0; rem = (uint)partial; }
                else if (partial == divisor) { q[i] = 1; rem = 0; }
                else { q[i] = (uint)(partial / divisor); rem = (uint)(partial - (ulong)q[i] * divisor); }
            }
            if (!r.IsEmpty) r[0] = rem;
        }
        else KnuthDiv(u, v, q, r, m, n);
        if (!quotient.IsEmpty)
            for (int i = 0; i < left.Length; ++i) quotient[i] = ((ulong)q[i * 2 + 1] << 32) | q[i * 2];
        if (!remainder.IsEmpty)
            for (int i = 0; i < right.Length; ++i) remainder[i] = ((ulong)r[i * 2 + 1] << 32) | r[i * 2];
    }
}
