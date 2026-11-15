// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APInt.cpp
// insertBits/extractBits; mutations return the updated immutable APInt.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    public LlvmAPInt InsertBits(LlvmAPInt bits, uint position)
    {
        uint width = bits.BitWidth;
        Debug.Assert(width + position <= BitWidth);
        if (width == 0) return this;
        if (width == BitWidth) return bits;
        if (IsSingleWord)
        {
            ulong mask = Mask(width) << (int)position;
            return new(BitWidth, (single & ~mask) | (bits.single << (int)position));
        }
        uint lowBit = position % 64, lowWord = position / 64;
        uint highWord = (position + width - 1) / 64;
        var result = (ulong[])words!.Clone();
        if (lowWord == highWord)
        {
            ulong mask = Mask(width) << (int)lowBit;
            result[lowWord] = (result[lowWord] & ~mask) | (bits.single << (int)lowBit);
            return new(BitWidth, result);
        }
        if (lowBit == 0)
        {
            uint wholeWords = width / 64;
            for (uint index = 0; index < wholeWords; ++index) result[lowWord + index] = bits.GetWord(index);
            uint remainder = width % 64;
            if (remainder != 0)
                result[highWord] = (result[highWord] & ~Mask(remainder)) | bits.GetWord((width - 1) / 64);
            return new(BitWidth, result);
        }
        for (uint index = 0; index < width; ++index)
        {
            uint destination = position + index;
            ulong mask = 1UL << (int)(destination % 64);
            if ((bits.GetWord(index / 64) & (1UL << (int)(index % 64))) != 0) result[destination / 64] |= mask;
            else result[destination / 64] &= ~mask;
        }
        return new(BitWidth, result);
    }

    public LlvmAPInt ExtractBits(uint count, uint position)
    {
        Debug.Assert(position < BitWidth && count + position <= BitWidth);
        if (IsSingleWord) return new(count, single >> (int)position, implicitTrunc: true);
        uint lowBit = position % 64, lowWord = position / 64;
        uint highWord = (position + count - 1) / 64;
        if (lowWord == highWord) return new(count, words![lowWord] >> (int)lowBit, implicitTrunc: true);
        if (lowBit == 0) return new(count, words!.AsSpan((int)lowWord, (int)(1 + highWord - lowWord)));
        uint destinationWords = (uint)(((ulong)count + 63) / 64);
        var result = new ulong[destinationWords];
        for (uint index = 0; index < destinationWords; ++index)
        {
            ulong low = words![lowWord + index];
            ulong high = lowWord + index + 1 < NumWords ? words[lowWord + index + 1] : 0;
            result[index] = (low >> (int)lowBit) | (high << (int)(64 - lowBit));
        }
        return new(count, result);
    }
}
