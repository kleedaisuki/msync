// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantRange.cpp set operations.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    private static LlvmConstantRange Preferred(LlvmConstantRange a, LlvmConstantRange b, LlvmPreferredRangeType type)
    {
        if (type == LlvmPreferredRangeType.Unsigned)
        {
            if (!a.IsWrappedSet && b.IsWrappedSet) return a;
            if (a.IsWrappedSet && !b.IsWrappedSet) return b;
        }
        else if (type == LlvmPreferredRangeType.Signed)
        {
            if (!a.IsSignWrappedSet && b.IsSignWrappedSet) return a;
            if (a.IsSignWrappedSet && !b.IsSignWrappedSet) return b;
        }
        return a.IsSizeStrictlySmallerThan(b) ? a : b;
    }
    public LlvmConstantRange IntersectWith(LlvmConstantRange other, LlvmPreferredRangeType type = LlvmPreferredRangeType.Smallest)
    {
        Debug.Assert(BitWidth == other.BitWidth);
        if (IsEmptySet || other.IsFullSet) return this;
        if (other.IsEmptySet || IsFullSet) return other;
        if (!IsUpperWrapped && other.IsUpperWrapped) return other.IntersectWith(this, type);
        if (!IsUpperWrapped && !other.IsUpperWrapped)
        {
            if (Lower.Ult(other.Lower))
            {
                if (Upper.Ule(other.Lower)) return GetEmpty();
                if (Upper.Ult(other.Upper)) return new(other.Lower, Upper);
                return other;
            }
            if (Upper.Ult(other.Upper)) return this;
            if (Lower.Ult(other.Upper)) return new(Lower, other.Upper);
            return GetEmpty();
        }
        if (IsUpperWrapped && !other.IsUpperWrapped)
        {
            if (other.Lower.Ult(Upper))
            {
                if (other.Upper.Ult(Upper)) return other;
                if (other.Upper.Ule(Lower)) return new(other.Lower, Upper);
                return Preferred(this, other, type);
            }
            if (other.Lower.Ult(Lower))
            {
                if (other.Upper.Ule(Lower)) return GetEmpty();
                return new(Lower, other.Upper);
            }
            return other;
        }
        if (other.Upper.Ult(Upper))
        {
            if (other.Lower.Ult(Upper)) return Preferred(this, other, type);
            if (other.Lower.Ult(Lower)) return new(Lower, other.Upper);
            return other;
        }
        if (other.Upper.Ule(Lower))
        {
            if (other.Lower.Ult(Lower)) return this;
            return new(other.Lower, Upper);
        }
        return Preferred(this, other, type);
    }
    public LlvmConstantRange UnionWith(LlvmConstantRange other, LlvmPreferredRangeType type = LlvmPreferredRangeType.Smallest)
    {
        Debug.Assert(BitWidth == other.BitWidth);
        if (IsFullSet || other.IsEmptySet) return this;
        if (other.IsFullSet || IsEmptySet) return other;
        if (!IsUpperWrapped && other.IsUpperWrapped) return other.UnionWith(this, type);
        if (!IsUpperWrapped && !other.IsUpperWrapped)
        {
            if (other.Upper.Ult(Lower) || Upper.Ult(other.Lower))
                return Preferred(new(Lower, other.Upper), new(other.Lower, Upper), type);
            var lower = other.Lower.Ult(Lower) ? other.Lower : Lower;
            var upper = Dec(other.Upper).Ugt(Dec(Upper)) ? other.Upper : Upper;
            if (lower.IsZero && upper.IsZero) return GetFull();
            return new(lower, upper);
        }
        if (!other.IsUpperWrapped)
        {
            if (other.Upper.Ule(Upper) || other.Lower.Uge(Lower)) return this;
            if (other.Lower.Ule(Upper) && Lower.Ule(other.Upper)) return GetFull();
            if (Upper.Ult(other.Lower) && other.Upper.Ult(Lower))
                return Preferred(new(Lower, other.Upper), new(other.Lower, Upper), type);
            if (Upper.Ult(other.Lower) && Lower.Ule(other.Upper)) return new(other.Lower, Upper);
            Debug.Assert(other.Lower.Ule(Upper) && other.Upper.Ult(Lower));
            return new(Lower, other.Upper);
        }
        if (other.Lower.Ule(Upper) || Lower.Ule(other.Upper)) return GetFull();
        return new(UMin(other.Lower, Lower), UMax(other.Upper, Upper));
    }
    public LlvmConstantRange? ExactIntersectWith(LlvmConstantRange other)
    {
        var result = IntersectWith(other);
        return result == Inverse().UnionWith(other.Inverse()).Inverse() ? result : null;
    }
    public LlvmConstantRange? ExactUnionWith(LlvmConstantRange other)
    {
        var result = UnionWith(other);
        return result == Inverse().IntersectWith(other.Inverse()).Inverse() ? result : null;
    }
}
