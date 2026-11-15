// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APInt.h bit-mask factories
// and predicates; APInt.cpp setBitsSlowCase and sadd/uadd/ssub/usub/smul/umul/
// sshl/ushl_sat. Immutable wrappers retain source field/word operations.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    public uint PopCount()
    {
        if (IsSingleWord) return (uint)BitOperations.PopCount(single);
        uint count = 0;
        foreach (ulong word in words!) count += (uint)BitOperations.PopCount(word);
        return count;
    }
    public bool IsMaxValue => IsAllOnes;
    public bool IsMinValue => IsZero;
    public bool IsNonNegative => !IsNegative;
    public bool IsStrictlyPositive => IsNonNegative && !IsZero;
    public bool IsMaxSignedValue
    {
        get
        {
            if (!IsSingleWord) return !IsNegative && CountTrailingOnes() == BitWidth - 1;
            Debug.Assert(BitWidth != 0);
            return single == ((1UL << (int)(BitWidth - 1)) - 1);
        }
    }
    public static LlvmAPInt GetOneBitSet(uint bits, uint bit)
    {
        Debug.Assert(bit < bits);
        if (bits <= 64) return new(bits, 1UL << (int)bit);
        ulong[] data = new ulong[((ulong)bits + 63) / 64];
        data[bit / 64] = 1UL << (int)(bit % 64);
        return FromOwnedWords(bits, data);
    }
    public static LlvmAPInt GetBitsSet(uint bits, uint low, uint high)
    {
        Debug.Assert(low <= high && high <= bits);
        if (low == high) return GetZero(bits);
        if (bits <= 64) return new(bits, Mask(high) & ~Mask(low));
        ulong[] data = new ulong[((ulong)bits + 63) / 64];
        int lowWord = (int)(low / 64), highWord = (int)(high / 64);
        ulong lowMask = ulong.MaxValue << (int)(low % 64);
        int highShift = (int)(high % 64);
        if (highShift != 0)
        {
            ulong highMask = ulong.MaxValue >> (64 - highShift);
            if (highWord == lowWord) lowMask &= highMask;
            else data[highWord] |= highMask;
        }
        data[lowWord] |= lowMask;
        for (int word = lowWord + 1; word < highWord; ++word) data[word] = ulong.MaxValue;
        return FromOwnedWords(bits, data);
    }
    public static LlvmAPInt GetBitsSetFrom(uint bits, uint low) => GetBitsSet(bits, low, bits);
    public static LlvmAPInt GetHighBitsSet(uint bits, uint count)
    { Debug.Assert(count <= bits); return GetBitsSet(bits, bits - count, bits); }
    public static LlvmAPInt GetLowBitsSet(uint bits, uint count)
    { Debug.Assert(count <= bits); return GetBitsSet(bits, 0, count); }
    public LlvmAPInt SAddSat(LlvmAPInt right)
    { LlvmAPInt result = SAddOverflow(right, out bool overflow); return !overflow ? result : IsNegative ? GetSignedMinValue(BitWidth) : GetSignedMaxValue(BitWidth); }
    public LlvmAPInt UAddSat(LlvmAPInt right)
    { LlvmAPInt result = UAddOverflow(right, out bool overflow); return overflow ? GetMaxValue(BitWidth) : result; }
    public LlvmAPInt SSubSat(LlvmAPInt right)
    { LlvmAPInt result = SSubOverflow(right, out bool overflow); return !overflow ? result : IsNegative ? GetSignedMinValue(BitWidth) : GetSignedMaxValue(BitWidth); }
    public LlvmAPInt USubSat(LlvmAPInt right)
    { LlvmAPInt result = USubOverflow(right, out bool overflow); return overflow ? GetZero(BitWidth) : result; }
    public LlvmAPInt SMulSat(LlvmAPInt right)
    { LlvmAPInt result = SMulOverflow(right, out bool overflow); return !overflow ? result : (IsNegative ^ right.IsNegative) ? GetSignedMinValue(BitWidth) : GetSignedMaxValue(BitWidth); }
    public LlvmAPInt UMulSat(LlvmAPInt right)
    { LlvmAPInt result = UMulOverflow(right, out bool overflow); return overflow ? GetMaxValue(BitWidth) : result; }
    public LlvmAPInt SShlSat(LlvmAPInt right) => SShlSat((uint)right.GetLimitedValue(BitWidth));
    public LlvmAPInt UShlSat(LlvmAPInt right) => UShlSat((uint)right.GetLimitedValue(BitWidth));
    public LlvmAPInt SShlSat(uint right)
    { LlvmAPInt result = SShlOverflow(right, out bool overflow); return !overflow ? result : IsNegative ? GetSignedMinValue(BitWidth) : GetSignedMaxValue(BitWidth); }
    public LlvmAPInt UShlSat(uint right)
    { LlvmAPInt result = UShlOverflow(right, out bool overflow); return overflow ? GetMaxValue(BitWidth) : result; }
}
