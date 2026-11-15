// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantRange.cpp arithmetic.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public const uint NoUnsignedWrap = 1, NoSignedWrap = 2;
    private const uint BothNoWrap = NoUnsignedWrap | NoSignedWrap;
    public LlvmConstantRange BinaryOp(LlvmBinaryOperator operation, LlvmConstantRange other)
    {
        if (operation.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl)
            return OverflowingBinaryOp(operation.Opcode, other, (operation.HasNoUnsignedWrap ? NoUnsignedWrap : 0)
                | (operation.HasNoSignedWrap ? NoSignedWrap : 0));
        if (operation.Opcode == LlvmOpcode.Or) return BinaryOr(other, operation.IsDisjoint);
        return BinaryOp(operation.Opcode, other);
    }
    public LlvmConstantRange BinaryOp(LlvmOpcode opcode, LlvmConstantRange other) => opcode switch
    {
        LlvmOpcode.Add or LlvmOpcode.FAdd => Add(other), LlvmOpcode.Sub or LlvmOpcode.FSub => Sub(other),
        LlvmOpcode.Mul or LlvmOpcode.FMul => Multiply(other), LlvmOpcode.UDiv => UDiv(other),
        LlvmOpcode.SDiv => SDiv(other), LlvmOpcode.URem => URem(other), LlvmOpcode.SRem => SRem(other),
        LlvmOpcode.Shl => Shl(other), LlvmOpcode.LShr => LShr(other), LlvmOpcode.AShr => AShr(other),
        LlvmOpcode.And => BinaryAnd(other), LlvmOpcode.Or => BinaryOr(other), LlvmOpcode.Xor => BinaryXor(other),
        _ => GetFull() // The pinned source's binaryOp default, not an unported integer operation.
    };
    public LlvmConstantRange OverflowingBinaryOp(LlvmOpcode opcode, LlvmConstantRange other, uint flags) => opcode switch
    {
        LlvmOpcode.Add => AddWithNoWrap(other, flags), LlvmOpcode.Sub => SubWithNoWrap(other, flags),
        LlvmOpcode.Mul => Multiply(other, flags), LlvmOpcode.Shl => ShlWithNoWrap(other, flags),
        _ => BinaryOp(opcode, other)
    };
    public LlvmConstantRange Add(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (IsFullSet || other.IsFullSet) return GetFull();
        var lower = Lower + other.Lower;
        var upper = Dec(Upper + other.Upper);
        if (lower == upper) return GetFull();
        var result = new LlvmConstantRange(lower, upper);
        return result.IsSizeStrictlySmallerThan(this) || result.IsSizeStrictlySmallerThan(other) ? GetFull() : result;
    }
    public LlvmConstantRange Sub(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (IsFullSet || other.IsFullSet) return GetFull();
        var lower = Inc(Lower - other.Upper);
        var upper = Upper - other.Lower;
        if (lower == upper) return GetFull();
        var result = new LlvmConstantRange(lower, upper);
        return result.IsSizeStrictlySmallerThan(this) || result.IsSizeStrictlySmallerThan(other) ? GetFull() : result;
    }
    public LlvmConstantRange AddWithNoWrap(LlvmConstantRange other, uint flags, LlvmPreferredRangeType type = LlvmPreferredRangeType.Smallest)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (IsFullSet && other.IsFullSet) return GetFull();
        var result = Add(other);
        if ((flags & NoSignedWrap) != 0) result = result.IntersectWith(SAddSat(other), type);
        if ((flags & NoUnsignedWrap) != 0) result = result.IntersectWith(UAddSat(other), type);
        return result;
    }
    public LlvmConstantRange SubWithNoWrap(LlvmConstantRange other, uint flags, LlvmPreferredRangeType type = LlvmPreferredRangeType.Smallest)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (IsFullSet && other.IsFullSet) return GetFull();
        var result = Sub(other);
        if ((flags & NoSignedWrap) != 0) result = result.IntersectWith(SSubSat(other), type);
        if ((flags & NoUnsignedWrap) != 0)
        {
            if (GetUnsignedMax().Ult(other.GetUnsignedMin())) return GetEmpty();
            result = result.IntersectWith(USubSat(other), type);
        }
        return result;
    }
    public LlvmConstantRange Multiply(LlvmConstantRange other, uint flags = 0)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (GetSingleElement() is { } left)
        {
            if (left.IsOne) return other;
            if (left.IsAllOnes) return new LlvmConstantRange(LlvmAPInt.GetZero(BitWidth)).Sub(other);
        }
        if (other.GetSingleElement() is { } right)
        {
            if (right.IsOne) return this;
            if (right.IsAllOnes) return new LlvmConstantRange(LlvmAPInt.GetZero(BitWidth)).Sub(this);
        }
        LlvmConstantRange unsignedResult;
        if ((flags & NoUnsignedWrap) != 0)
        {
            var minimum = GetUnsignedMin().UMulOverflow(other.GetUnsignedMin(), out bool overflow);
            if (overflow) return GetEmpty();
            unsignedResult = GetNonEmpty(minimum, Inc(GetUnsignedMax().UMulSat(other.GetUnsignedMax())));
        }
        else
        {
            var lower = GetUnsignedMin().ZExt(BitWidth * 2) * other.GetUnsignedMin().ZExt(BitWidth * 2);
            var upper = Inc(GetUnsignedMax().ZExt(BitWidth * 2) * other.GetUnsignedMax().ZExt(BitWidth * 2));
            unsignedResult = new LlvmConstantRange(lower, upper).Truncate(BitWidth);
        }
        if ((flags & NoSignedWrap) == 0 && !unsignedResult.IsUpperWrapped
            && (!unsignedResult.Upper.IsNegative || unsignedResult.Upper.IsMinSignedValue)) return unsignedResult;
        var a = GetSignedMin().SExt(BitWidth * 2);
        var b = GetSignedMax().SExt(BitWidth * 2);
        var c = other.GetSignedMin().SExt(BitWidth * 2);
        var d = other.GetSignedMax().SExt(BitWidth * 2);
        var signedResult = new LlvmConstantRange(SMin(SMin(a * c, a * d), SMin(b * c, b * d)),
            Inc(SMax(SMax(a * c, a * d), SMax(b * c, b * d))));
        if ((flags & NoSignedWrap) != 0)
            signedResult = signedResult.IntersectWith(new(LlvmAPInt.GetSignedMinValue(BitWidth).SExt(BitWidth * 2),
                Inc(LlvmAPInt.GetSignedMaxValue(BitWidth).SExt(BitWidth * 2))));
        signedResult = signedResult.Truncate(BitWidth);
        var result = unsignedResult.IsSizeStrictlySmallerThan(signedResult) ? unsignedResult : signedResult;
        if (flags == (NoUnsignedWrap | NoSignedWrap) && !result.IsAllNonNegative
            && (GetSignedMin().Sgt(new(BitWidth, 1)) || other.GetSignedMin().Sgt(new(BitWidth, 1))))
            result = result.IntersectWith(GetNonEmpty(LlvmAPInt.GetZero(BitWidth), LlvmAPInt.GetSignedMinValue(BitWidth)));
        return result;
    }
    public LlvmConstantRange UDiv(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet || other.GetUnsignedMax().IsZero) return GetEmpty();
        var lower = GetUnsignedMin().UDiv(other.GetUnsignedMax());
        var minimum = other.GetUnsignedMin();
        if (minimum.IsZero) minimum = other.Upper.IsOne ? other.Lower : new(BitWidth, 1);
        return GetNonEmpty(lower, Inc(GetUnsignedMax().UDiv(minimum)));
    }
    private (LlvmConstantRange Positive, LlvmConstantRange Negative) SplitPosNeg()
    {
        var zero = LlvmAPInt.GetZero(BitWidth);
        var one = new LlvmAPInt(BitWidth, 1);
        var signedMin = LlvmAPInt.GetSignedMinValue(BitWidth);
        return (IntersectWith(BitWidth == 1 ? GetEmpty() : new(one, signedMin)), IntersectWith(new(signedMin, zero)));
    }
    public LlvmConstantRange SDiv(LlvmConstantRange other)
    {
        var zero = LlvmAPInt.GetZero(BitWidth);
        var signedMin = LlvmAPInt.GetSignedMinValue(BitWidth);
        var (pl, nl) = SplitPosNeg();
        var (pr, nr) = other.SplitPosNeg();
        var positive = GetEmpty();
        if (!pl.IsEmptySet && !pr.IsEmptySet)
            positive = new(pl.Lower.SDiv(Dec(pr.Upper)), Inc(Dec(pl.Upper).SDiv(pr.Lower)));
        if (!nl.IsEmptySet && !nr.IsEmptySet)
        {
            var lower = Dec(nl.Upper).SDiv(nr.Lower);
            if (nl.Lower.IsMinSignedValue && nr.Upper.IsZero)
            {
                if (!nr.Lower.IsAllOnes)
                {
                    var adjusted = other.Lower.IsAllOnes ? other.Upper : Dec(nr.Upper);
                    positive = positive.UnionWith(new(lower, Inc(nl.Lower.SDiv(Dec(adjusted)))));
                }
                if (nl.Upper != Inc(signedMin))
                {
                    var adjusted = Upper == Inc(signedMin) ? Lower : Inc(nl.Lower);
                    positive = positive.UnionWith(new(lower, Inc(adjusted.SDiv(Dec(nr.Upper)))));
                }
            }
            else positive = positive.UnionWith(new(lower, Inc(nl.Lower.SDiv(Dec(nr.Upper)))));
        }
        var negative = GetEmpty();
        if (!pl.IsEmptySet && !nr.IsEmptySet)
            negative = new(Dec(pl.Upper).SDiv(Dec(nr.Upper)), Inc(pl.Lower.SDiv(nr.Lower)));
        if (!nl.IsEmptySet && !pr.IsEmptySet)
            negative = negative.UnionWith(new(nl.Lower.SDiv(pr.Lower), Inc(Dec(nl.Upper).SDiv(Dec(pr.Upper)))));
        var result = negative.UnionWith(positive, LlvmPreferredRangeType.Signed);
        if (Contains(zero) && (!pr.IsEmptySet || !nr.IsEmptySet)) result = result.UnionWith(new(zero));
        return result;
    }
    public LlvmConstantRange URem(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet || other.GetUnsignedMax().IsZero) return GetEmpty();
        if (other.GetSingleElement() is { } right)
        {
            if (right.IsZero) return GetEmpty();
            if (GetSingleElement() is { } left) return new(left.URem(right));
        }
        if (GetUnsignedMax().Ult(other.GetUnsignedMin())) return this;
        return GetNonEmpty(LlvmAPInt.GetZero(BitWidth), Inc(UMin(GetUnsignedMax(), Dec(other.GetUnsignedMax()))));
    }
    public LlvmConstantRange SRem(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        if (other.GetSingleElement() is { } right)
        {
            if (right.IsZero) return GetEmpty();
            if (GetSingleElement() is { } left) return new(left.SRem(right));
        }
        var absolute = other.Abs();
        var minimum = absolute.GetUnsignedMin();
        var maximum = absolute.GetUnsignedMax();
        if (maximum.IsZero) return GetEmpty();
        if (minimum.IsZero) minimum = Inc(minimum);
        var lower = GetSignedMin();
        var upper = GetSignedMax();
        if (!lower.IsNegative)
        {
            if (upper.Ult(minimum)) return this;
            return new(LlvmAPInt.GetZero(BitWidth), Inc(UMin(upper, Dec(maximum))));
        }
        if (upper.IsNegative)
        {
            if (lower.Ugt(-minimum)) return this;
            return new(UMax(lower, Inc(-maximum)), new(BitWidth, 1));
        }
        return new(UMax(lower, Inc(-maximum)), Inc(UMin(upper, Dec(maximum))));
    }
    public LlvmConstantRange Abs(bool intMinIsPoison = false)
    {
        if (IsEmptySet) return GetEmpty();
        if (IsSignWrappedSet)
        {
            var lower = Upper.IsStrictlyPositive || !Lower.IsStrictlyPositive ? LlvmAPInt.GetZero(BitWidth) : UMin(Lower, Inc(-Upper));
            return new(lower, intMinIsPoison ? LlvmAPInt.GetSignedMinValue(BitWidth) : Inc(LlvmAPInt.GetSignedMinValue(BitWidth)));
        }
        var minimum = GetSignedMin();
        var maximum = GetSignedMax();
        if (intMinIsPoison && minimum.IsMinSignedValue)
        {
            if (maximum.IsMinSignedValue) return GetEmpty();
            minimum = Inc(minimum);
        }
        if (!minimum.IsNegative) return new(minimum, Inc(maximum));
        if (maximum.IsNegative) return new(-maximum, Inc(-minimum));
        return GetNonEmpty(LlvmAPInt.GetZero(BitWidth), Inc(UMax(-minimum, maximum)));
    }
    public LlvmConstantRange UAddSat(LlvmConstantRange other) => IsEmptySet || other.IsEmptySet ? GetEmpty()
        : GetNonEmpty(GetUnsignedMin().UAddSat(other.GetUnsignedMin()), Inc(GetUnsignedMax().UAddSat(other.GetUnsignedMax())));
    public LlvmConstantRange SAddSat(LlvmConstantRange other) => IsEmptySet || other.IsEmptySet ? GetEmpty()
        : GetNonEmpty(GetSignedMin().SAddSat(other.GetSignedMin()), Inc(GetSignedMax().SAddSat(other.GetSignedMax())));
    public LlvmConstantRange USubSat(LlvmConstantRange other) => IsEmptySet || other.IsEmptySet ? GetEmpty()
        : GetNonEmpty(GetUnsignedMin().USubSat(other.GetUnsignedMax()), Inc(GetUnsignedMax().USubSat(other.GetUnsignedMin())));
    public LlvmConstantRange SSubSat(LlvmConstantRange other) => IsEmptySet || other.IsEmptySet ? GetEmpty()
        : GetNonEmpty(GetSignedMin().SSubSat(other.GetSignedMax()), Inc(GetSignedMax().SSubSat(other.GetSignedMin())));
}
