// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/TypeSize.h and Alignment.h.
// Managed value carriers replace CRTP and optional Align; arithmetic/order rules are upstream.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmAlign : IComparable<LlvmAlign>
{
    private readonly byte shift;
    public LlvmAlign(ulong value)
    {
        Debug.Assert(BitOperations.IsPow2(value));
        shift = (byte)BitOperations.Log2(value);
    }
    public ulong Value => 1UL << shift;
    public int CompareTo(LlvmAlign other) => shift.CompareTo(other.shift);
    public static bool operator <(LlvmAlign left, LlvmAlign right) => left.shift < right.shift;
    public static bool operator >(LlvmAlign left, LlvmAlign right) => left.shift > right.shift;
    public static bool operator <=(LlvmAlign left, LlvmAlign right) => left.shift <= right.shift;
    public static bool operator >=(LlvmAlign left, LlvmAlign right) => left.shift >= right.shift;
    public static LlvmAlign Max(LlvmAlign left, LlvmAlign right) => left > right ? left : right;
    public ulong AlignTo(ulong value) => unchecked((value + Value - 1) & ~(Value - 1));
    public bool IsAligned(ulong value) => (value & (Value - 1)) == 0;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct LlvmTypeSize(ulong KnownMinValue, bool IsScalable)
{
    public LlvmTypeSize(LlvmElementCount count) : this(count.KnownMinValue, count.IsScalable) { }
    public static LlvmTypeSize Fixed(ulong value) => new(value, false);
    public static LlvmTypeSize Scalable(ulong value) => new(value, true);
    public static LlvmTypeSize Get(ulong value, bool scalable) => new(value, scalable);
    public static LlvmTypeSize GetFixed(ulong value) => Fixed(value);
    public static LlvmTypeSize GetScalable(ulong value) => Scalable(value);
    public static LlvmTypeSize GetZero() => default;
    public bool IsFixed => !IsScalable;
    public bool IsZero => KnownMinValue == 0;
    public bool IsNonZero => KnownMinValue != 0;
    public bool IsKnownEven => (KnownMinValue & 1) == 0;
    public ulong GetKnownMinValue() => KnownMinValue;
    public ulong GetKnownMinSize() => KnownMinValue;
    public ulong GetFixedValue() { Debug.Assert(!IsScalable || IsZero); return KnownMinValue; }
    public LlvmTypeSize GetWithIncrement(ulong value) => new(unchecked(KnownMinValue + value), IsScalable);
    public bool IsKnownMultipleOf(ulong value) => value != 0 && KnownMinValue % value == 0;
    public bool IsKnownMultipleOf(LlvmTypeSize value) => (IsScalable || !value.IsScalable) && IsKnownMultipleOf(value.KnownMinValue);
    public bool HasKnownScalarFactor(LlvmTypeSize value) => IsScalable == value.IsScalable && KnownMinValue % value.KnownMinValue == 0;
    public ulong GetKnownScalarFactor(LlvmTypeSize value) { Debug.Assert(HasKnownScalarFactor(value)); return KnownMinValue / value.KnownMinValue; }
    public LlvmTypeSize DivideCoefficientBy(ulong value) => new(KnownMinValue / value, IsScalable);
    public LlvmTypeSize CoefficientNextPowerOf2() => new(NextPowerOf2(KnownMinValue), IsScalable);
    public LlvmTypeSize AlignTo(ulong align)
    {
        Debug.Assert(align != 0);
        return new(unchecked((KnownMinValue + align - 1) / align * align), IsScalable);
    }
    public static bool IsKnownLT(LlvmTypeSize left, LlvmTypeSize right) => (!left.IsScalable || right.IsScalable) && left.KnownMinValue < right.KnownMinValue;
    public static bool IsKnownGT(LlvmTypeSize left, LlvmTypeSize right) => (left.IsScalable || !right.IsScalable) && left.KnownMinValue > right.KnownMinValue;
    public static bool IsKnownLE(LlvmTypeSize left, LlvmTypeSize right) => (!left.IsScalable || right.IsScalable) && left.KnownMinValue <= right.KnownMinValue;
    public static bool IsKnownGE(LlvmTypeSize left, LlvmTypeSize right) => (left.IsScalable || !right.IsScalable) && left.KnownMinValue >= right.KnownMinValue;
    public static LlvmTypeSize operator +(LlvmTypeSize left, LlvmTypeSize right)
    {
        Debug.Assert(left.IsZero || right.IsZero || left.IsScalable == right.IsScalable);
        return new(unchecked(left.KnownMinValue + right.KnownMinValue), right.IsZero ? left.IsScalable : right.IsScalable);
    }
    public static LlvmTypeSize operator -(LlvmTypeSize left, LlvmTypeSize right)
    {
        Debug.Assert(left.IsZero || right.IsZero || left.IsScalable == right.IsScalable);
        return new(unchecked(left.KnownMinValue - right.KnownMinValue), right.IsZero ? left.IsScalable : right.IsScalable);
    }
    public static LlvmTypeSize operator *(LlvmTypeSize left, ulong right) => new(unchecked(left.KnownMinValue * right), left.IsScalable);
    public static LlvmTypeSize operator *(ulong left, LlvmTypeSize right) => right * left;
    internal static ulong NextPowerOf2(ulong value)
    {
        value |= value >> 1; value |= value >> 2; value |= value >> 4;
        value |= value >> 8; value |= value >> 16; value |= value >> 32;
        return unchecked(value + 1);
    }
    public override string ToString() => (IsScalable ? "vscale x " : "") + KnownMinValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct LlvmElementCount(uint KnownMinValue, bool IsScalable)
{
    public static LlvmElementCount Fixed(uint value) => new(value, false);
    public static LlvmElementCount Scalable(uint value) => new(value, true);
    public static LlvmElementCount Get(uint value, bool scalable) => new(value, scalable);
    public static LlvmElementCount GetFixed(uint value) => Fixed(value);
    public static LlvmElementCount GetScalable(uint value) => Scalable(value);
    public bool IsFixed => !IsScalable;
    public bool IsZero => KnownMinValue == 0;
    public bool IsNonZero => KnownMinValue != 0;
    public bool IsScalar => !IsScalable && KnownMinValue == 1;
    public bool IsVector => IsScalable && KnownMinValue != 0 || KnownMinValue > 1;
    public bool IsPowerOf2 => BitOperations.IsPow2(KnownMinValue);
    public bool IsKnownEven => (KnownMinValue & 1) == 0;
    public uint GetKnownMinValue() => KnownMinValue;
    public uint GetFixedValue() { Debug.Assert(!IsScalable || IsZero); return KnownMinValue; }
    public LlvmElementCount GetWithIncrement(uint value) => new(unchecked(KnownMinValue + value), IsScalable);
    public bool IsKnownMultipleOf(uint value) => value != 0 && KnownMinValue % value == 0;
    public bool IsKnownMultipleOf(LlvmElementCount value) => (IsScalable || !value.IsScalable) && IsKnownMultipleOf(value.KnownMinValue);
    public LlvmElementCount DivideCoefficientBy(uint value) => new(KnownMinValue / value, IsScalable);
    public LlvmElementCount CoefficientNextPowerOf2() => new(unchecked((uint)LlvmTypeSize.NextPowerOf2(KnownMinValue)), IsScalable);
    public bool HasKnownScalarFactor(LlvmElementCount value) => IsScalable == value.IsScalable && KnownMinValue % value.KnownMinValue == 0;
    public uint GetKnownScalarFactor(LlvmElementCount value) { Debug.Assert(HasKnownScalarFactor(value)); return KnownMinValue / value.KnownMinValue; }
    public static bool IsKnownLT(LlvmElementCount left, LlvmElementCount right) => (!left.IsScalable || right.IsScalable) && left.KnownMinValue < right.KnownMinValue;
    public static bool IsKnownGT(LlvmElementCount left, LlvmElementCount right) => (left.IsScalable || !right.IsScalable) && left.KnownMinValue > right.KnownMinValue;
    public static bool IsKnownLE(LlvmElementCount left, LlvmElementCount right) => (!left.IsScalable || right.IsScalable) && left.KnownMinValue <= right.KnownMinValue;
    public static bool IsKnownGE(LlvmElementCount left, LlvmElementCount right) => (left.IsScalable || !right.IsScalable) && left.KnownMinValue >= right.KnownMinValue;
    public static LlvmElementCount operator +(LlvmElementCount left, LlvmElementCount right)
    {
        Debug.Assert(left.IsZero || right.IsZero || left.IsScalable == right.IsScalable);
        return new(unchecked(left.KnownMinValue + right.KnownMinValue), right.IsZero ? left.IsScalable : right.IsScalable);
    }
    public static LlvmElementCount operator -(LlvmElementCount left, LlvmElementCount right)
    {
        Debug.Assert(left.IsZero || right.IsZero || left.IsScalable == right.IsScalable);
        return new(unchecked(left.KnownMinValue - right.KnownMinValue), right.IsZero ? left.IsScalable : right.IsScalable);
    }
    public static LlvmElementCount operator *(LlvmElementCount left, uint right) => new(unchecked(left.KnownMinValue * right), left.IsScalable);
    public static LlvmElementCount operator *(uint left, LlvmElementCount right) => right * left;
    public override string ToString() => (IsScalable ? "vscale x " : "") + KnownMinValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
