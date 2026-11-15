// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from llvm/ADT/APInt.h and llvm/lib/Support/APInt.cpp,
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt : IEquatable<LlvmAPInt>
{
    private readonly ulong single;
    private readonly ulong[]? words;
    public uint BitWidth { get; }
    public bool IsSingleWord => BitWidth <= 64;
    public uint NumWords => (uint)(((ulong)BitWidth + 63) / 64);

    public LlvmAPInt() : this(1, 0) { }

    public LlvmAPInt(uint numBits, ulong value, bool isSigned = false, bool implicitTrunc = false)
    {
        BitWidth = numBits;
        words = null;
        single = 0;
        Debug.Assert(implicitTrunc || Fits(numBits, value, isSigned), "Value does not fit the APInt width");
        if (IsSingleWord)
        {
            single = implicitTrunc || isSigned ? value & Mask(numBits) : value;
            return;
        }
        words = new ulong[NumWords];
        if (isSigned && unchecked((long)value) < 0) Array.Fill(words, ulong.MaxValue);
        words[0] = value;
        words[^1] &= Mask((numBits - 1) % 64 + 1);
    }

    public LlvmAPInt(uint numBits, ReadOnlySpan<ulong> value)
    {
        Debug.Assert(!value.IsEmpty, "APInt array input must have data");
        BitWidth = numBits;
        single = 0;
        words = null;
        if (IsSingleWord)
        {
            single = value[0] & Mask(numBits);
            return;
        }
        words = new ulong[NumWords];
        value[..Math.Min(value.Length, words.Length)].CopyTo(words);
        words[^1] &= Mask((numBits - 1) % 64 + 1);
    }

    public static LlvmAPInt GetZero(uint bits) => new(bits, 0);
    public static LlvmAPInt GetAllOnes(uint bits) => new(bits, ulong.MaxValue, isSigned: true);
    public static LlvmAPInt GetMaxValue(uint bits) => GetAllOnes(bits);
    public static LlvmAPInt GetMinValue(uint bits) => GetZero(bits);
    public static LlvmAPInt GetSignedMinValue(uint bits)
    {
        Debug.Assert(bits != 0);
        ulong[] data = new ulong[(uint)(((ulong)bits + 63) / 64)];
        data[(bits - 1) / 64] = 1UL << (int)((bits - 1) % 64);
        return new(bits, data);
    }
    public static LlvmAPInt GetSignedMaxValue(uint bits)
    {
        Debug.Assert(bits != 0);
        ulong[] data = GetAllOnes(bits).ToWords();
        data[(bits - 1) / 64] &= ~(1UL << (int)((bits - 1) % 64));
        return new(bits, data);
    }

    public ulong GetWord(uint index)
    {
        Debug.Assert(index < NumWords);
        return IsSingleWord ? single : words![index];
    }
    public ulong[] ToWords()
    {
        if (IsSingleWord) return BitWidth == 0 ? [] : [single];
        return (ulong[])words!.Clone();
    }
    public void CopyRawData(Span<ulong> destination)
    {
        Debug.Assert(destination.Length >= NumWords);
        if (IsSingleWord) { if (BitWidth != 0) destination[0] = single; return; }
        words.AsSpan().CopyTo(destination);
    }

    public bool IsZero => IsSingleWord ? single == 0 : CountLeadingZeros() == BitWidth;
    public bool IsOne => IsSingleWord ? single == 1 : CountLeadingZeros() == BitWidth - 1;
    public bool IsAllOnes => BitWidth == 0 || (IsSingleWord ? single == Mask(BitWidth) : CountTrailingOnes() == BitWidth);
    public bool IsNegative
    {
        get { Debug.Assert(BitWidth != 0); return (GetWord((BitWidth - 1) / 64) & (1UL << (int)((BitWidth - 1) % 64))) != 0; }
    }
    public uint GetActiveBits() => BitWidth - CountLeadingZeros();
    public uint GetSignificantBits() => BitWidth - (IsNegative ? CountLeadingOnes() : CountLeadingZeros()) + 1;
    public ulong GetZExtValue()
    {
        Debug.Assert(GetActiveBits() <= 64, "Too many bits for ulong");
        return IsSingleWord ? single : words![0];
    }
    public long GetSExtValue()
    {
        Debug.Assert(BitWidth != 0);
        if (!IsSingleWord)
        {
            Debug.Assert(GetSignificantBits() <= 64, "Too many bits for long");
            return unchecked((long)words![0]);
        }
        int shift = (int)(64 - BitWidth);
        return unchecked((long)(single << shift)) >> shift;
    }
    public ulong? TryZExtValue() => GetActiveBits() <= 64 ? GetZExtValue() : null;
    public long? TrySExtValue() => GetSignificantBits() <= 64 ? GetSExtValue() : null;
    public ulong GetLimitedValue(ulong limit = ulong.MaxValue) =>
        GetActiveBits() > 64 || GetZExtValue() > limit ? limit : GetZExtValue();

    public uint CountLeadingZeros()
    {
        if (IsSingleWord) return (uint)BitOperations.LeadingZeroCount(single) - (64 - BitWidth);
        uint count = 0;
        for (int index = words!.Length - 1; index >= 0; index--)
        {
            ulong value = words[index];
            if (value == 0) count += 64;
            else { count += (uint)BitOperations.LeadingZeroCount(value); break; }
        }
        uint remainder = BitWidth % 64;
        return count - (remainder > 0 ? 64 - remainder : 0);
    }
    public uint CountLeadingOnes()
    {
        if (IsSingleWord)
            return BitWidth == 0 ? 0 : (uint)BitOperations.LeadingZeroCount(~(single << (int)(64 - BitWidth)));
        uint highBits = BitWidth % 64;
        if (highBits == 0) highBits = 64;
        int index = words!.Length - 1;
        uint count = (uint)BitOperations.LeadingZeroCount(~(words[index] << (int)(64 - highBits)));
        if (count != highBits) return count;
        for (index--; index >= 0; index--)
        {
            if (words[index] == ulong.MaxValue) count += 64;
            else { count += (uint)BitOperations.LeadingZeroCount(~words[index]); break; }
        }
        return count;
    }
    public uint CountTrailingZeros()
    {
        if (IsSingleWord) return Math.Min(BitWidth, (uint)BitOperations.TrailingZeroCount(single));
        uint count = 0;
        foreach (ulong word in words!)
        {
            if (word == 0) { count += 64; continue; }
            count += (uint)BitOperations.TrailingZeroCount(word);
            break;
        }
        return Math.Min(count, BitWidth);
    }
    public uint CountTrailingOnes()
    {
        if (IsSingleWord) return Math.Min(BitWidth, (uint)BitOperations.TrailingZeroCount(~single));
        uint count = 0;
        int index = 0;
        for (; index < words!.Length && words[index] == ulong.MaxValue; index++) count += 64;
        if (index < words.Length) count += (uint)BitOperations.TrailingZeroCount(~words[index]);
        return count;
    }

    public bool Equals(LlvmAPInt other)
    {
        if (BitWidth != other.BitWidth) return false;
        return IsSingleWord ? single == other.single : words.AsSpan().SequenceEqual(other.words);
    }
    public override bool Equals(object? value) => value is LlvmAPInt other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(BitWidth);
        if (IsSingleWord) hash.Add(single);
        else foreach (ulong word in words!) hash.Add(word);
        return hash.ToHashCode();
    }
    public static bool operator ==(LlvmAPInt first, LlvmAPInt second) => first.Equals(second);
    public static bool operator !=(LlvmAPInt first, LlvmAPInt second) => !first.Equals(second);

    private static ulong Mask(uint bits) => bits == 0 ? 0 : ulong.MaxValue >> (int)(64 - bits);
    private static bool Fits(uint bits, ulong value, bool signed)
    {
        if (bits >= 64) return true;
        if (bits == 0) return value == 0 || signed && value == ulong.MaxValue;
        int shift = (int)(64 - bits);
        return signed ? (unchecked((long)(value << shift)) >> shift) == unchecked((long)value) : (value >> (int)bits) == 0;
    }
}
