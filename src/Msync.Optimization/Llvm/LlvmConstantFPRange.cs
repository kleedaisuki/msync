// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantFPRange.h/.cpp.
// See vendor/llvm-reference/llvm/LICENSE.TXT for license and exception.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

/// <summary>LLVM inclusive floating-point fact interval, not the language Range object.</summary>
public sealed record LlvmConstantFPRange
{
    public LlvmAPFloat Lower { get; }
    public LlvmAPFloat Upper { get; }
    public bool ContainsQNaN { get; }
    public bool ContainsSNaN { get; }
    public bool ContainsNaN => ContainsQNaN || ContainsSNaN;
    public LlvmAPFloatSemantics Semantics => Lower.Semantics;
    public bool IsNaNOnly => IsPosInf(Lower) && IsNegInf(Upper);
    public bool IsEmptySet => IsNaNOnly && !ContainsNaN;
    public bool IsFullSet => IsNegInf(Lower) && IsPosInf(Upper) && ContainsQNaN && ContainsSNaN;

    public LlvmConstantFPRange(LlvmAPFloat value)
    {
        Lower = value.IsNaN ? Inf(false) : value;
        Upper = value.IsNaN ? Inf(true) : value;
        ContainsQNaN = value.IsNaN && !value.IsSignaling;
        ContainsSNaN = value.IsSignaling;
    }

    public LlvmConstantFPRange(LlvmAPFloat lower, LlvmAPFloat upper, bool mayBeQNaN, bool mayBeSNaN)
    {
        Debug.Assert(lower.Semantics == upper.Semantics);
        Debug.Assert(!lower.IsNaN && !upper.IsNaN);
        Debug.Assert(!IsNonCanonicalEmptySet(lower, upper));
        (Lower, Upper, ContainsQNaN, ContainsSNaN) = (lower, upper, mayBeQNaN, mayBeSNaN);
    }

    public static LlvmConstantFPRange GetEmpty(LlvmAPFloatSemantics semantics = LlvmAPFloatSemantics.IEEEdouble)
    {
        RequireBinary64(semantics);
        return new(Inf(false), Inf(true), false, false);
    }

    public static LlvmConstantFPRange GetFull(LlvmAPFloatSemantics semantics = LlvmAPFloatSemantics.IEEEdouble)
    {
        RequireBinary64(semantics);
        return new(Inf(true), Inf(false), true, true);
    }

    public static LlvmConstantFPRange GetFinite(LlvmAPFloatSemantics semantics = LlvmAPFloatSemantics.IEEEdouble)
    {
        RequireBinary64(semantics);
        return new(Largest(true), Largest(false), false, false);
    }

    public static LlvmConstantFPRange GetNonNaN(LlvmAPFloatSemantics semantics = LlvmAPFloatSemantics.IEEEdouble)
    {
        RequireBinary64(semantics);
        return GetNonNaN(Inf(true), Inf(false));
    }

    public static LlvmConstantFPRange GetNonNaN(LlvmAPFloat lower, LlvmAPFloat upper) => new(lower, upper, false, false);
    public static LlvmConstantFPRange GetMayBeNaN(LlvmAPFloat lower, LlvmAPFloat upper) => new(lower, upper, true, true);
    public static LlvmConstantFPRange GetNaNOnly(LlvmAPFloatSemantics semantics, bool mayBeQNaN, bool mayBeSNaN)
    {
        RequireBinary64(semantics);
        return new(Inf(false), Inf(true), mayBeQNaN, mayBeSNaN);
    }

    public static LlvmConstantFPRange MakeAllowedFCmpRegion(LlvmCmpPredicate predicate, LlvmConstantFPRange other)
    {
        if (other.IsEmptySet) return other;
        if (other.ContainsNaN && IsUnordered(predicate)) return GetFull(other.Semantics);
        if (other.IsNaNOnly && IsOrdered(predicate)) return GetEmpty(other.Semantics);
        switch (predicate)
        {
            case LlvmCmpPredicate.FCmpTrue: return GetFull(other.Semantics);
            case LlvmCmpPredicate.FCmpFalse: return GetEmpty(other.Semantics);
            case LlvmCmpPredicate.FCmpOrd: return GetNonNaN(other.Semantics);
            case LlvmCmpPredicate.FCmpUno: return GetNaNOnly(other.Semantics, true, true);
            case LlvmCmpPredicate.FCmpOeq:
            case LlvmCmpPredicate.FCmpUeq:
                return SetNaNField(ExtendZeroIfEqual(other, predicate), predicate);
            case LlvmCmpPredicate.FCmpOne:
            case LlvmCmpPredicate.FCmpUne:
                if (other.GetSingleElement(true) is { } single)
                {
                    if (IsPosInf(single)) return SetNaNField(GetNonNaN(Inf(true), Largest(false)), predicate);
                    if (IsNegInf(single)) return SetNaNField(GetNonNaN(Largest(true), Inf(false)), predicate);
                }
                return predicate == LlvmCmpPredicate.FCmpOne ? GetNonNaN(other.Semantics) : GetFull(other.Semantics);
            case LlvmCmpPredicate.FCmpOlt:
            case LlvmCmpPredicate.FCmpOle:
            case LlvmCmpPredicate.FCmpUlt:
            case LlvmCmpPredicate.FCmpUle:
                return SetNaNField(ExtendZeroIfEqual(MakeLessThan(other.Upper, predicate), predicate), predicate);
            case LlvmCmpPredicate.FCmpOgt:
            case LlvmCmpPredicate.FCmpOge:
            case LlvmCmpPredicate.FCmpUgt:
            case LlvmCmpPredicate.FCmpUge:
                return SetNaNField(ExtendZeroIfEqual(MakeGreaterThan(other.Lower, predicate), predicate), predicate);
            default: throw new ArgumentOutOfRangeException(nameof(predicate));
        }
    }

    public static LlvmConstantFPRange MakeSatisfyingFCmpRegion(LlvmCmpPredicate predicate, LlvmConstantFPRange other)
    {
        if (other.IsEmptySet) return GetFull(other.Semantics);
        if (other.ContainsNaN && IsOrdered(predicate)) return GetEmpty(other.Semantics);
        if (other.IsNaNOnly && IsUnordered(predicate)) return GetFull(other.Semantics);
        switch (predicate)
        {
            case LlvmCmpPredicate.FCmpTrue: return GetFull(other.Semantics);
            case LlvmCmpPredicate.FCmpFalse: return GetEmpty(other.Semantics);
            case LlvmCmpPredicate.FCmpOrd: return GetNonNaN(other.Semantics);
            case LlvmCmpPredicate.FCmpUno: return GetNaNOnly(other.Semantics, true, true);
            case LlvmCmpPredicate.FCmpOeq:
            case LlvmCmpPredicate.FCmpUeq:
                return SetNaNField(other.GetSingleElement(true).HasValue ||
                    (other.Classify() & ~LlvmFPClassTest.NaN) == LlvmFPClassTest.Zero
                        ? ExtendZeroIfEqual(other, predicate) : GetEmpty(other.Semantics), predicate);
            case LlvmCmpPredicate.FCmpOne:
            case LlvmCmpPredicate.FCmpUne:
                return GetEmpty(other.Semantics);
            case LlvmCmpPredicate.FCmpOlt:
            case LlvmCmpPredicate.FCmpOle:
            case LlvmCmpPredicate.FCmpUlt:
            case LlvmCmpPredicate.FCmpUle:
                return SetNaNField(ExtendZeroIfEqual(MakeLessThan(other.Lower, predicate), predicate), predicate);
            case LlvmCmpPredicate.FCmpOgt:
            case LlvmCmpPredicate.FCmpOge:
            case LlvmCmpPredicate.FCmpUgt:
            case LlvmCmpPredicate.FCmpUge:
                return SetNaNField(ExtendZeroIfEqual(MakeGreaterThan(other.Upper, predicate), predicate), predicate);
            default: throw new ArgumentOutOfRangeException(nameof(predicate));
        }
    }

    public static LlvmConstantFPRange? MakeExactFCmpRegion(LlvmCmpPredicate predicate, LlvmAPFloat other)
    {
        if ((predicate is LlvmCmpPredicate.FCmpUne or LlvmCmpPredicate.FCmpOne) && !other.IsNaN) return null;
        return MakeSatisfyingFCmpRegion(predicate, new(other));
    }

    public bool FCmp(LlvmCmpPredicate predicate, LlvmConstantFPRange other) => MakeSatisfyingFCmpRegion(predicate, other).Contains(this);
    public bool Contains(LlvmAPFloat value) => value.IsNaN
        ? value.IsSignaling ? ContainsSNaN : ContainsQNaN
        : StrictCompare(Lower, value) != LlvmFloatCompare.GreaterThan && StrictCompare(value, Upper) != LlvmFloatCompare.GreaterThan;
    public bool Contains(LlvmConstantFPRange other)
    {
        if (other.ContainsQNaN && !ContainsQNaN) return false;
        if (other.ContainsSNaN && !ContainsSNaN) return false;
        return StrictCompare(Lower, other.Lower) != LlvmFloatCompare.GreaterThan &&
            StrictCompare(other.Upper, Upper) != LlvmFloatCompare.GreaterThan;
    }

    public LlvmAPFloat? GetSingleElement(bool excludesNaN = false) =>
        !excludesNaN && ContainsNaN ? null : Lower.BitwiseIsEqual(Upper) ? Lower : null;
    public bool IsSingleElement(bool excludesNaN = false) => GetSingleElement(excludesNaN).HasValue;
    public bool? GetSignBit() => !ContainsNaN && Lower.IsNegative == Upper.IsNegative ? Lower.IsNegative : null;

    public LlvmFPClassTest Classify()
    {
        uint mask = (ContainsSNaN ? (uint)LlvmFPClassTest.SNaN : 0) | (ContainsQNaN ? (uint)LlvmFPClassTest.QNaN : 0);
        if (!IsNaNOnly)
        {
            uint lowerMask = (uint)new LlvmKnownFPClass(Lower).GetKnownFPClasses();
            uint upperMask = (uint)new LlvmKnownFPClass(Upper).GetKnownFPClasses();
            Debug.Assert(lowerMask <= upperMask);
            mask |= (upperMask << 1) - lowerMask;
        }
        return (LlvmFPClassTest)mask;
    }

    public LlvmConstantFPRange IntersectWith(LlvmConstantFPRange other)
    {
        LlvmAPFloat lower = MaxNum(Lower, other.Lower), upper = MinNum(Upper, other.Upper);
        CanonicalizeRange(ref lower, ref upper);
        return new(lower, upper, ContainsQNaN & other.ContainsQNaN, ContainsSNaN & other.ContainsSNaN);
    }

    public LlvmConstantFPRange UnionWith(LlvmConstantFPRange other) =>
        new(MinNum(Lower, other.Lower), MaxNum(Upper, other.Upper), ContainsQNaN | other.ContainsQNaN, ContainsSNaN | other.ContainsSNaN);
    public LlvmConstantFPRange Negate() => new(Upper.ChangeSign(), Lower.ChangeSign(), ContainsQNaN, ContainsSNaN);
    public LlvmConstantFPRange Abs()
    {
        if (IsNaNOnly) return this;
        if (Lower.IsNegative == Upper.IsNegative) return Lower.IsNegative ? Negate() : this;
        return new(Zero(false), MaxNum(Lower.ChangeSign(), Upper), ContainsQNaN, ContainsSNaN);
    }

    public LlvmConstantFPRange GetWithoutNaN() => new(Lower, Upper, false, false);
    public LlvmConstantFPRange GetWithoutInf()
    {
        if (IsNaNOnly) return this;
        LlvmAPFloat lower = IsNegInf(Lower) ? Largest(true) : Lower;
        LlvmAPFloat upper = IsPosInf(Upper) ? Largest(false) : Upper;
        CanonicalizeRange(ref lower, ref upper);
        return new(lower, upper, ContainsQNaN, ContainsSNaN);
    }

    private static LlvmFloatCompare StrictCompare(LlvmAPFloat left, LlvmAPFloat right)
    {
        Debug.Assert(!left.IsNaN && !right.IsNaN);
        if (left.IsZero && right.IsZero)
            return left.IsNegative == right.IsNegative ? LlvmFloatCompare.Equal : left.IsNegative ? LlvmFloatCompare.LessThan : LlvmFloatCompare.GreaterThan;
        return left.Compare(right);
    }

    private static bool IsNonCanonicalEmptySet(LlvmAPFloat lower, LlvmAPFloat upper) =>
        StrictCompare(lower, upper) == LlvmFloatCompare.GreaterThan && !(lower.IsInfinity && upper.IsInfinity);
    private static void CanonicalizeRange(ref LlvmAPFloat lower, ref LlvmAPFloat upper)
    {
        if (!IsNonCanonicalEmptySet(lower, upper)) return;
        lower = Inf(false);
        upper = Inf(true);
    }

    private static bool ExcludesEqual(LlvmCmpPredicate predicate) => ((int)predicate & (int)LlvmCmpPredicate.FCmpOeq) == 0;
    private static bool IsUnordered(LlvmCmpPredicate predicate) => predicate >= LlvmCmpPredicate.FCmpUno && predicate <= LlvmCmpPredicate.FCmpUne;
    private static bool IsOrdered(LlvmCmpPredicate predicate) => predicate >= LlvmCmpPredicate.FCmpOeq && predicate <= LlvmCmpPredicate.FCmpOrd;
    private static LlvmConstantFPRange MakeLessThan(LlvmAPFloat value, LlvmCmpPredicate predicate)
    {
        if (ExcludesEqual(predicate))
        {
            if (IsNegInf(value)) return GetEmpty(value.Semantics);
            value = Next(value, true);
        }
        return GetNonNaN(Inf(true), value);
    }

    private static LlvmConstantFPRange MakeGreaterThan(LlvmAPFloat value, LlvmCmpPredicate predicate)
    {
        if (ExcludesEqual(predicate))
        {
            if (IsPosInf(value)) return GetEmpty(value.Semantics);
            value = Next(value, false);
        }
        return GetNonNaN(value, Inf(false));
    }

    private static LlvmConstantFPRange ExtendZeroIfEqual(LlvmConstantFPRange range, LlvmCmpPredicate predicate)
    {
        if (ExcludesEqual(predicate)) return range;
        LlvmAPFloat lower = range.Lower.IsZero && !range.Lower.IsNegative ? Zero(true) : range.Lower;
        LlvmAPFloat upper = range.Upper.IsZero && range.Upper.IsNegative ? Zero(false) : range.Upper;
        return new(lower, upper, range.ContainsQNaN, range.ContainsSNaN);
    }

    private static LlvmConstantFPRange SetNaNField(LlvmConstantFPRange range, LlvmCmpPredicate predicate) =>
        new(range.Lower, range.Upper, IsUnordered(predicate), IsUnordered(predicate));
    private static bool IsPosInf(LlvmAPFloat value) => value.IsInfinity && !value.IsNegative;
    private static bool IsNegInf(LlvmAPFloat value) => value.IsInfinity && value.IsNegative;
    private static LlvmAPFloat Inf(bool negative) => LlvmAPFloat.FromBits(0x7ff0000000000000UL | (negative ? 1UL << 63 : 0));
    private static LlvmAPFloat Largest(bool negative) => LlvmAPFloat.FromBits(0x7fefffffffffffffUL | (negative ? 1UL << 63 : 0));
    private static LlvmAPFloat Zero(bool negative) => LlvmAPFloat.FromBits(negative ? 1UL << 63 : 0);
    private static void RequireBinary64(LlvmAPFloatSemantics semantics)
    {
        if (semantics != LlvmAPFloatSemantics.IEEEdouble) throw new NotSupportedException($"ConstantFPRange format {semantics} is not implemented.");
    }

    // APFloat.h minnum/maxnum, with the source's signed-zero and signaling-NaN ordering.
    private static LlvmAPFloat MinNum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsSignaling) return Quiet(left);
        if (right.IsSignaling) return Quiet(right);
        if (left.IsNaN) return right;
        if (right.IsNaN) return left;
        if (left.IsZero && right.IsZero && left.IsNegative != right.IsNegative) return left.IsNegative ? left : right;
        return right.Compare(left) == LlvmFloatCompare.LessThan ? right : left;
    }

    private static LlvmAPFloat MaxNum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsSignaling) return Quiet(left);
        if (right.IsSignaling) return Quiet(right);
        if (left.IsNaN) return right;
        if (right.IsNaN) return left;
        if (left.IsZero && right.IsZero && left.IsNegative != right.IsNegative) return left.IsNegative ? right : left;
        return left.Compare(right) == LlvmFloatCompare.LessThan ? right : left;
    }

    private static LlvmAPFloat Quiet(LlvmAPFloat value) => LlvmAPFloat.FromBits(value.BitcastToAPInt().GetZExtValue() | 0x0008000000000000UL);

    // IEEEFloat::next: binary64 packed increment/decrement is the source's
    // significand step plus exponent carry across normal/subnormal binades.
    private static LlvmAPFloat Next(LlvmAPFloat value, bool nextDown)
    {
        if (nextDown) value = value.ChangeSign();
        ulong bits = value.BitcastToAPInt().GetZExtValue();
        switch (value.Category)
        {
            case LlvmFloatCategory.Infinity:
                if (value.IsNegative) value = Largest(true);
                break;
            case LlvmFloatCategory.NaN:
                if (value.IsSignaling) value = LlvmAPFloat.FromBits((bits & (1UL << 63)) | 0x7ff8000000000000UL);
                break;
            case LlvmFloatCategory.Zero:
                value = LlvmAPFloat.FromBits(1);
                break;
            case LlvmFloatCategory.Normal:
                value = LlvmAPFloat.FromBits(value.IsNegative ? bits - 1 : bits + 1);
                break;
        }
        return nextDown ? value.ChangeSign() : value;
    }
}
