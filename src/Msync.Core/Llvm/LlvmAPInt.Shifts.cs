// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADT/APInt.h shift wrappers;
// Support/APInt.cpp shl/lshr/ashrSlowCase, tcShiftLeft/Right, trunc/zext/sext.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    public LlvmAPInt Shl(LlvmAPInt amount) => Shl((uint)amount.GetLimitedValue(BitWidth));
    public LlvmAPInt LShr(LlvmAPInt amount) => LShr((uint)amount.GetLimitedValue(BitWidth));
    public LlvmAPInt AShr(LlvmAPInt amount) => AShr((uint)amount.GetLimitedValue(BitWidth));

    public LlvmAPInt Shl(uint amount)
    {
        Debug.Assert(amount <= BitWidth);
        if (IsSingleWord) return new(BitWidth, amount == BitWidth ? 0 : single << (int)amount, implicitTrunc: true);
        ulong[] result = ToWords();
        TcShiftLeft(result, amount);
        return FromOwnedWords(BitWidth, result);
    }
    public LlvmAPInt LShr(uint amount)
    {
        Debug.Assert(amount <= BitWidth);
        if (IsSingleWord) return new(BitWidth, amount == BitWidth ? 0 : single >> (int)amount);
        ulong[] result = ToWords();
        TcShiftRight(result, amount);
        return FromOwnedWords(BitWidth, result);
    }
    public LlvmAPInt AShr(uint amount)
    {
        Debug.Assert(amount <= BitWidth);
        if (IsSingleWord)
        {
            long extended = GetSExtValue();
            return new(BitWidth, unchecked((ulong)(extended >> (amount == BitWidth ? 63 : (int)amount))), implicitTrunc: true);
        }
        if (amount == 0) return this;
        ulong[] result = ToWords();
        bool negative = IsNegative;
        int wordShift = (int)(amount / 64), bitShift = (int)(amount % 64);
        int wordsToMove = result.Length - wordShift;
        if (wordsToMove != 0)
        {
            int signShift = 64 - (int)((BitWidth - 1) % 64 + 1);
            result[^1] = unchecked((ulong)(unchecked((long)(result[^1] << signShift)) >> signShift));
            if (bitShift == 0) Array.Copy(result, wordShift, result, 0, wordsToMove);
            else
            {
                for (int i = 0; i != wordsToMove - 1; ++i)
                    result[i] = (result[i + wordShift] >> bitShift) | (result[i + wordShift + 1] << (64 - bitShift));
                result[wordsToMove - 1] = unchecked((ulong)(unchecked((long)result[wordShift + wordsToMove - 1]) >> bitShift));
            }
        }
        result.AsSpan(wordsToMove, wordShift).Fill(negative ? ulong.MaxValue : 0);
        return FromOwnedWords(BitWidth, result);
    }

    private static void TcShiftLeft(Span<ulong> dst, uint count)
    {
        if (count == 0) return;
        int wordShift = (int)Math.Min(count / 64, (uint)dst.Length), bitShift = (int)(count % 64);
        if (bitShift == 0) dst[..(dst.Length - wordShift)].CopyTo(dst[wordShift..]);
        else
        {
            for (int i = dst.Length - 1; i >= wordShift; --i)
            {
                dst[i] = dst[i - wordShift] << bitShift;
                if (i > wordShift) dst[i] |= dst[i - wordShift - 1] >> (64 - bitShift);
            }
        }
        dst[..wordShift].Clear();
    }
    private static void TcShiftRight(Span<ulong> dst, uint count)
    {
        if (count == 0) return;
        int wordShift = (int)Math.Min(count / 64, (uint)dst.Length), bitShift = (int)(count % 64);
        int wordsToMove = dst.Length - wordShift;
        if (bitShift == 0) dst[wordShift..].CopyTo(dst);
        else
        {
            for (int i = 0; i != wordsToMove; ++i)
            {
                dst[i] = dst[i + wordShift] >> bitShift;
                if (i + 1 != wordsToMove) dst[i] |= dst[i + wordShift + 1] << (64 - bitShift);
            }
        }
        dst[wordsToMove..].Clear();
    }

    public LlvmAPInt Trunc(uint width)
    {
        Debug.Assert(width <= BitWidth);
        if (width <= 64) return new(width, IsSingleWord ? single : words![0], implicitTrunc: true);
        if (width == BitWidth) return this;
        ulong[] result = new ulong[((ulong)width + 63) / 64];
        words.AsSpan(0, result.Length).CopyTo(result);
        return FromOwnedWords(width, result);
    }
    public LlvmAPInt ZExt(uint width)
    {
        Debug.Assert(width >= BitWidth);
        if (width <= 64) return new(width, single);
        if (width == BitWidth) return this;
        ulong[] result = new ulong[((ulong)width + 63) / 64];
        CopyRawData(result);
        return FromOwnedWords(width, result);
    }
    public LlvmAPInt SExt(uint width)
    {
        Debug.Assert(width >= BitWidth);
        if (width <= 64) return new(width, unchecked((ulong)GetSExtValue()), isSigned: true);
        if (width == BitWidth) return this;
        ulong[] result = new ulong[((ulong)width + 63) / 64];
        CopyRawData(result);
        int last = (int)NumWords - 1;
        int shift = 64 - (int)((BitWidth - 1) % 64 + 1);
        result[last] = unchecked((ulong)(unchecked((long)(result[last] << shift)) >> shift));
        result.AsSpan((int)NumWords).Fill(IsNegative ? ulong.MaxValue : 0);
        return FromOwnedWords(width, result);
    }
    public LlvmAPInt ZExtOrTrunc(uint width) => BitWidth < width ? ZExt(width) : BitWidth > width ? Trunc(width) : this;
    public LlvmAPInt SExtOrTrunc(uint width) => BitWidth < width ? SExt(width) : BitWidth > width ? Trunc(width) : this;
}
