// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantRange.h/.cpp.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public enum LlvmPreferredRangeType : byte { Smallest, Unsigned, Signed }

/// <summary>LLVM integer modular half-open interval, not a source language Range.</summary>
public sealed partial record LlvmConstantRange
{
    public LlvmAPInt Lower { get; }
    public LlvmAPInt Upper { get; }
    public uint BitWidth => Lower.BitWidth;
    public LlvmConstantRange(uint bitWidth, bool full)
    {
        Lower = full ? LlvmAPInt.GetMaxValue(bitWidth) : LlvmAPInt.GetMinValue(bitWidth);
        Upper = Lower;
    }
    public LlvmConstantRange(LlvmAPInt value) => (Lower, Upper) = (value, Inc(value));
    public LlvmConstantRange(LlvmAPInt lower, LlvmAPInt upper)
    {
        Debug.Assert(lower.BitWidth == upper.BitWidth);
        Debug.Assert(lower != upper || lower.IsZero || lower.IsAllOnes);
        (Lower, Upper) = (lower, upper);
    }
    public static LlvmConstantRange Full(uint width) => new(width, true);
    public static LlvmConstantRange Empty(uint width) => new(width, false);
    public static LlvmConstantRange GetFull(uint width) => Full(width);
    public static LlvmConstantRange GetEmpty(uint width) => Empty(width);
    public LlvmConstantRange GetFull() => Full(BitWidth);
    public LlvmConstantRange GetEmpty() => Empty(BitWidth);
    public static LlvmConstantRange GetNonEmpty(LlvmAPInt lower, LlvmAPInt upper) => lower == upper ? Full(lower.BitWidth) : new(lower, upper);
    public bool IsFullSet => Lower == Upper && Lower.IsAllOnes;
    public bool IsEmptySet => Lower == Upper && Lower.IsZero;
    public bool IsSingleElement => Upper == Inc(Lower);
    public bool IsWrappedSet => Lower.Ugt(Upper) && !Upper.IsZero;
    public bool IsUpperWrapped => Lower.Ugt(Upper);
    public bool IsSignWrappedSet => Lower.Sgt(Upper) && !Upper.IsMinSignedValue;
    public bool IsUpperSignWrapped => Lower.Sgt(Upper);
    public LlvmAPInt? GetSingleElement() => IsSingleElement ? Lower : null;
    public LlvmAPInt? GetSingleMissingElement() => Lower == Inc(Upper) ? Upper : null;
    internal static LlvmAPInt Inc(LlvmAPInt value) => value + new LlvmAPInt(value.BitWidth, 1);
    internal static LlvmAPInt Dec(LlvmAPInt value) => value - new LlvmAPInt(value.BitWidth, 1);
    internal static LlvmAPInt UMin(LlvmAPInt a, LlvmAPInt b) => a.Ult(b) ? a : b;
    internal static LlvmAPInt UMax(LlvmAPInt a, LlvmAPInt b) => a.Ugt(b) ? a : b;
    internal static LlvmAPInt SMin(LlvmAPInt a, LlvmAPInt b) => a.Slt(b) ? a : b;
    internal static LlvmAPInt SMax(LlvmAPInt a, LlvmAPInt b) => a.Sgt(b) ? a : b;
    public bool IsSizeStrictlySmallerThan(LlvmConstantRange other)
    {
        Debug.Assert(BitWidth == other.BitWidth);
        if (IsFullSet) return false;
        if (other.IsFullSet) return true;
        return (Upper - Lower).Ult(other.Upper - other.Lower);
    }
    public bool IsSizeLargerThan(ulong size) => IsFullSet ? size == 0 || LlvmAPInt.GetMaxValue(BitWidth).GetLimitedValue() > size - 1
        || LlvmAPInt.GetMaxValue(BitWidth).GetActiveBits() > 64 : (Upper - Lower).GetActiveBits() > 64 || (Upper - Lower).GetZExtValue() > size;
    public bool IsAllNegative => IsEmptySet || !IsFullSet && !IsUpperSignWrapped && (Upper.IsNegative || Upper.IsZero);
    public bool IsAllNonNegative => !IsSignWrappedSet && !Lower.IsNegative;
    public bool IsAllPositive => IsEmptySet || !IsFullSet && !IsSignWrappedSet && !Lower.IsNegative && !Lower.IsZero;
    public LlvmAPInt GetUnsignedMax() => IsFullSet || IsUpperWrapped ? LlvmAPInt.GetMaxValue(BitWidth) : Dec(Upper);
    public LlvmAPInt GetUnsignedMin() => IsFullSet || IsWrappedSet ? LlvmAPInt.GetMinValue(BitWidth) : Lower;
    public LlvmAPInt GetSignedMax() => IsFullSet || IsUpperSignWrapped ? LlvmAPInt.GetSignedMaxValue(BitWidth) : Dec(Upper);
    public LlvmAPInt GetSignedMin() => IsFullSet || IsSignWrappedSet ? LlvmAPInt.GetSignedMinValue(BitWidth) : Lower;
    public bool Contains(LlvmAPInt value) => Lower == Upper ? IsFullSet : !IsUpperWrapped
        ? Lower.Ule(value) && value.Ult(Upper) : Lower.Ule(value) || value.Ult(Upper);
    public bool Contains(LlvmConstantRange other)
    {
        if (IsFullSet || other.IsEmptySet) return true;
        if (IsEmptySet || other.IsFullSet) return false;
        if (!IsUpperWrapped) return !other.IsUpperWrapped && Lower.Ule(other.Lower) && other.Upper.Ule(Upper);
        if (!other.IsUpperWrapped) return other.Upper.Ule(Upper) || Lower.Ule(other.Lower);
        return other.Upper.Ule(Upper) && Lower.Ule(other.Lower);
    }
    public LlvmConstantRange Inverse() => IsFullSet ? GetEmpty() : IsEmptySet ? GetFull() : new(Upper, Lower);
    public LlvmConstantRange Subtract(LlvmAPInt value) => Lower == Upper ? this : new(Lower - value, Upper - value);
    public LlvmConstantRange Difference(LlvmConstantRange other) => IntersectWith(other.Inverse());
    public bool ICmp(LlvmCmpPredicate predicate, LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return true;
        return predicate switch
        {
            LlvmCmpPredicate.ICmpEq => GetSingleElement() is { } a && other.GetSingleElement() is { } b && a == b,
            LlvmCmpPredicate.ICmpNe => Inverse().Contains(other),
            LlvmCmpPredicate.ICmpUlt => GetUnsignedMax().Ult(other.GetUnsignedMin()),
            LlvmCmpPredicate.ICmpUle => GetUnsignedMax().Ule(other.GetUnsignedMin()),
            LlvmCmpPredicate.ICmpUgt => GetUnsignedMin().Ugt(other.GetUnsignedMax()),
            LlvmCmpPredicate.ICmpUge => GetUnsignedMin().Uge(other.GetUnsignedMax()),
            LlvmCmpPredicate.ICmpSlt => GetSignedMax().Slt(other.GetSignedMin()),
            LlvmCmpPredicate.ICmpSle => GetSignedMax().Sle(other.GetSignedMin()),
            LlvmCmpPredicate.ICmpSgt => GetSignedMin().Sgt(other.GetSignedMax()),
            LlvmCmpPredicate.ICmpSge => GetSignedMin().Sge(other.GetSignedMax()),
            _ => throw new InvalidOperationException("Invalid ICmp predicate")
        };
    }
    public static LlvmConstantRange MakeAllowedICmpRegion(LlvmCmpPredicate predicate, LlvmConstantRange range)
    {
        if (range.IsEmptySet) return range;
        uint width = range.BitWidth;
        var zero = LlvmAPInt.GetZero(width);
        var signedMin = LlvmAPInt.GetSignedMinValue(width);
        return predicate switch
        {
            LlvmCmpPredicate.ICmpEq => range,
            LlvmCmpPredicate.ICmpNe => range.IsSingleElement ? new(range.Upper, range.Lower) : Full(width),
            LlvmCmpPredicate.ICmpUlt => range.GetUnsignedMax().IsZero ? Empty(width) : new(zero, range.GetUnsignedMax()),
            LlvmCmpPredicate.ICmpSlt => range.GetSignedMax().IsMinSignedValue ? Empty(width) : new(signedMin, range.GetSignedMax()),
            LlvmCmpPredicate.ICmpUle => GetNonEmpty(zero, Inc(range.GetUnsignedMax())),
            LlvmCmpPredicate.ICmpSle => GetNonEmpty(signedMin, Inc(range.GetSignedMax())),
            LlvmCmpPredicate.ICmpUgt => range.GetUnsignedMin().IsAllOnes ? Empty(width) : new(Inc(range.GetUnsignedMin()), zero),
            LlvmCmpPredicate.ICmpSgt => range.GetSignedMin() == LlvmAPInt.GetSignedMaxValue(width) ? Empty(width) : new(Inc(range.GetSignedMin()), signedMin),
            LlvmCmpPredicate.ICmpUge => GetNonEmpty(range.GetUnsignedMin(), zero),
            LlvmCmpPredicate.ICmpSge => GetNonEmpty(range.GetSignedMin(), signedMin),
            _ => throw new InvalidOperationException("Invalid ICmp predicate to makeAllowedICmpRegion()")
        };
    }
    public static LlvmConstantRange MakeSatisfyingICmpRegion(LlvmCmpPredicate predicate, LlvmConstantRange range) =>
        MakeAllowedICmpRegion(LlvmCmpInstruction.GetInversePredicate(predicate), range).Inverse();
    public static LlvmConstantRange MakeExactICmpRegion(LlvmCmpPredicate predicate, LlvmAPInt value) => MakeAllowedICmpRegion(predicate, new(value));
}
