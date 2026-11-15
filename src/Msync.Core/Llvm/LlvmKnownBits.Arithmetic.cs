// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: lib/Support/KnownBits.cpp.

namespace Msync.Core.Llvm;

public readonly partial struct LlvmKnownBits
{
    public static LlvmKnownBits AddSub(bool add, bool nsw, bool nuw, LlvmKnownBits left, LlvmKnownBits right)
    {
        uint width = left.BitWidth;
        if (left.IsUnknown && right.IsUnknown) return new(width);
        LlvmKnownBits result = new(width);
        if (!left.IsUnknown && !right.IsUnknown)
        {
            LlvmKnownBits rhs = add ? right : ~right;
            LlvmAPInt max = left.GetMaxValue() + rhs.GetMaxValue() + new LlvmAPInt(width, add ? 0UL : 1UL);
            LlvmAPInt min = left.One + rhs.One + new LlvmAPInt(width, add ? 0UL : 1UL);
            LlvmAPInt carryZero = ~(max ^ left.Zero ^ rhs.Zero);
            LlvmAPInt carryOne = min ^ left.One ^ rhs.One;
            LlvmAPInt known = (left.Zero | left.One) & (rhs.Zero | rhs.One) & (carryZero | carryOne);
            result = new(~max & known, min & known);
        }
        LlvmAPInt zero = result.Zero, one = result.One;
        if (nuw)
        {
            LlvmAPInt bound = add ? left.One.UAddSat(right.One) : left.GetMaxValue().USubSat(right.One);
            uint count = add ? bound.CountLeadingOnes() : bound.CountLeadingZeros();
            LlvmAPInt mask = LlvmAPInt.GetHighBitsSet(width, count);
            if (nsw)
            {
                uint extra = add ? bound.Trunc(width - 1).CountLeadingOnes() : bound.Trunc(width - 1).CountLeadingZeros();
                mask |= LlvmAPInt.GetBitsSet(width, width - 1 - extra, width - 1);
            }
            if (add) one |= mask; else zero |= mask;
        }
        if (nsw)
        {
            LlvmAPInt min = add ? left.GetSignedMinValue().SAddSat(right.GetSignedMinValue()) : left.GetSignedMinValue().SSubSat(right.GetSignedMaxValue());
            LlvmAPInt max = add ? left.GetSignedMaxValue().SAddSat(right.GetSignedMaxValue()) : left.GetSignedMaxValue().SSubSat(right.GetSignedMinValue());
            if (!min.IsNegative)
            {
                one |= LlvmAPInt.GetBitsSet(width, width - 1 - min.Trunc(width - 1).CountLeadingOnes(), width - 1);
                zero |= LlvmAPInt.GetSignedMinValue(width);
            }
            if (max.IsNegative)
            {
                zero |= LlvmAPInt.GetBitsSet(width, width - 1 - max.Trunc(width - 1).CountLeadingZeros(), width - 1);
                one |= LlvmAPInt.GetSignedMinValue(width);
            }
        }
        return new LlvmKnownBits(zero, one).ResolveConflict();
    }

    public static LlvmKnownBits Mul(LlvmKnownBits left, LlvmKnownBits right, bool noUndefSelfMultiply = false)
    {
        uint width = left.BitWidth;
        LlvmAPInt max = left.GetMaxValue().UMulOverflow(right.GetMaxValue(), out bool overflow);
        uint leading = overflow ? 0 : max.CountLeadingZeros();
        uint lhsKnown = (left.Zero | left.One).CountTrailingOnes(), rhsKnown = (right.Zero | right.One).CountTrailingOnes();
        uint lhsZeros = left.CountMinTrailingZeros(), rhsZeros = right.CountMinTrailingZeros();
        uint bits = Math.Min(Math.Min(lhsKnown - lhsZeros, rhsKnown - rhsZeros) + lhsZeros + rhsZeros, width);
        LlvmAPInt bottom = left.One * right.One, mask = LlvmAPInt.GetLowBitsSet(width, bits);
        LlvmAPInt zero = LlvmAPInt.GetHighBitsSet(width, leading) | (~bottom & mask);
        if (noUndefSelfMultiply)
        {
            uint bit = 2 * lhsZeros + 1;
            if (bit < width) zero |= LlvmAPInt.GetOneBitSet(width, bit);
            if (lhsZeros < width && !(left.One & LlvmAPInt.GetOneBitSet(width, lhsZeros)).IsZero && bit + 1 < width)
                zero |= LlvmAPInt.GetOneBitSet(width, bit + 1);
        }
        return new(zero, bottom & mask);
    }

    public static LlvmKnownBits Shift(LlvmOpcode opcode, LlvmKnownBits left, LlvmKnownBits right, bool nuw = false, bool nsw = false, bool exact = false, bool nonZero = false)
    {
        uint width = left.BitWidth;
        uint min = (uint)right.One.GetLimitedValue(width);
        if (min == 0 && nonZero) min = 1;
        if (left.IsUnknown)
        {
            if (opcode == LlvmOpcode.Shl)
            {
                LlvmKnownBits unknown = new(LlvmAPInt.GetLowBitsSet(width, min), LlvmAPInt.GetZero(width));
                return nuw && nsw && min != 0 ? unknown.WithNonNegative() : unknown;
            }
            if (opcode == LlvmOpcode.LShr) return new(LlvmAPInt.GetHighBitsSet(width, min), LlvmAPInt.GetZero(width));
            return min == width ? MakeConstant(LlvmAPInt.GetZero(width)) : new(width);
        }
        uint max = (width & (width - 1)) == 0
            ? (uint)(right.GetMaxValue() & LlvmAPInt.GetLowBitsSet(right.BitWidth, Math.Min(right.BitWidth, (uint)System.Numerics.BitOperations.Log2(width)))).GetZExtValue()
            : (uint)right.GetMaxValue().GetLimitedValue(width - 1);
        if (opcode == LlvmOpcode.Shl)
        {
            if (nuw && nsw) max = Math.Min(max, left.CountMaxLeadingZeros() - 1);
            if (nuw) max = Math.Min(max, left.CountMaxLeadingZeros());
            if (nsw) max = Math.Min(max, Math.Max(left.CountMaxLeadingZeros(), left.CountMaxLeadingOnes()) - 1);
        }
        else if (exact) max = Math.Min(max, left.CountMaxTrailingZeros());
        if (opcode == LlvmOpcode.Shl && min == 0 && max == width - 1 && (width & (width - 1)) == 0)
        {
            LlvmKnownBits allShifts = new(LlvmAPInt.GetLowBitsSet(width, left.CountMinTrailingZeros()), left.IsAllOnes ? LlvmAPInt.GetSignedMinValue(width) : LlvmAPInt.GetZero(width));
            if (nsw && left.IsNonNegative) allShifts = allShifts.WithNonNegative();
            if (nsw && left.IsNegative) allShifts = allShifts.WithNegative();
            return allShifts;
        }

        LlvmKnownBits result = Conflict(width);
        uint zeroMask = (uint)right.Zero.ZExtOrTrunc(32).GetZExtValue(), oneMask = (uint)right.One.ZExtOrTrunc(32).GetZExtValue();
        for (uint amount = min; amount <= max; ++amount)
        {
            if ((zeroMask & amount) != 0 || (oneMask | amount) != amount) continue;
            LlvmKnownBits shifted;
            if (opcode == LlvmOpcode.Shl)
            {
                LlvmAPInt zero = left.Zero.UShlOverflow(amount, out bool outZero) | LlvmAPInt.GetLowBitsSet(width, amount);
                LlvmAPInt one = left.One.UShlOverflow(amount, out bool outOne);
                shifted = new(zero, one);
                if (nsw && ((nuw && amount != 0) || outZero)) shifted = shifted.WithNonNegative();
                else if (nsw && outOne) shifted = shifted.WithNegative();
            }
            else if (opcode == LlvmOpcode.LShr)
                shifted = new(left.Zero.LShr(amount) | LlvmAPInt.GetHighBitsSet(width, amount), left.One.LShr(amount));
            else shifted = new(left.Zero.AShr(amount), left.One.AShr(amount));
            result = result.IntersectWith(shifted);
            if (result.IsUnknown) break;
        }
        return result.ResolveConflict();
    }
}
