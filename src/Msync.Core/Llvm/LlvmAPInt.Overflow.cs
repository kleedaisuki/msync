// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/APInt.cpp
// sadd/uadd/ssub/usub/sdiv/smul/umul/sshl/ushl_ov; MathExtras.h MulOverflow.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPInt
{
    public bool IsMinSignedValue => IsNegative && IsPowerOf2;
    public LlvmAPInt SAddOverflow(LlvmAPInt right, out bool overflow)
    {
        LlvmAPInt result = this + right;
        overflow = IsNegative == right.IsNegative && result.IsNegative != IsNegative;
        return result;
    }
    public LlvmAPInt UAddOverflow(LlvmAPInt right, out bool overflow)
    {
        LlvmAPInt result = this + right;
        overflow = result.Ult(right);
        return result;
    }
    public LlvmAPInt SSubOverflow(LlvmAPInt right, out bool overflow)
    {
        LlvmAPInt result = this - right;
        overflow = IsNegative != right.IsNegative && result.IsNegative != IsNegative;
        return result;
    }
    public LlvmAPInt USubOverflow(LlvmAPInt right, out bool overflow)
    {
        LlvmAPInt result = this - right;
        overflow = result.Ugt(this);
        return result;
    }
    public LlvmAPInt SDivOverflow(LlvmAPInt right, out bool overflow)
    {
        overflow = IsMinSignedValue && right.IsAllOnes;
        return SDiv(right);
    }
    public LlvmAPInt SMulOverflow(LlvmAPInt right, out bool overflow)
    {
        Debug.Assert(BitWidth == right.BitWidth);
        if (IsSingleWord)
        {
            long product = SignedMulOverflow(GetSExtValue(), right.GetSExtValue(), out overflow);
            overflow |= !Fits(BitWidth, unchecked((ulong)product), true);
            return new(BitWidth, unchecked((ulong)product), isSigned: true, implicitTrunc: true);
        }
        LlvmAPInt result = this * right;
        overflow = !right.IsZero && (result.SDiv(right) != this || (IsMinSignedValue && right.IsAllOnes));
        return result;
    }
    private static long SignedMulOverflow(long left, long right, out bool overflow)
    {
        // MathExtras.h's portable MulOverflow<T> implementation, not a host FP cast.
        unchecked
        {
            ulong x = left < 0 ? 0UL - (ulong)left : (ulong)left;
            ulong y = right < 0 ? 0UL - (ulong)right : (ulong)right;
            ulong product = x * y;
            bool negative = (left < 0) ^ (right < 0);
            long result = (long)(negative ? 0UL - product : product);
            if (x == 0 || y == 0) { overflow = false; return result; }
            overflow = negative ? x > ((ulong)long.MaxValue + 1) / y : x > (ulong)long.MaxValue / y;
            return result;
        }
    }
    public LlvmAPInt UMulOverflow(LlvmAPInt right, out bool overflow)
    {
        Debug.Assert(BitWidth == right.BitWidth);
        if (unchecked(CountLeadingZeros() + right.CountLeadingZeros() + 2) <= BitWidth)
        { overflow = true; return this * right; }
        LlvmAPInt result = LShr(1) * right;
        overflow = result.IsNegative;
        result = result.Shl(1);
        if ((GetWord(0) & 1) != 0)
        {
            result += right;
            if (result.Ult(right)) overflow = true;
        }
        return result;
    }
    public LlvmAPInt SShlOverflow(LlvmAPInt amount, out bool overflow) =>
        SShlOverflow((uint)amount.GetLimitedValue(BitWidth), out overflow);
    public LlvmAPInt SShlOverflow(uint amount, out bool overflow)
    {
        overflow = amount >= BitWidth;
        if (overflow) return GetZero(BitWidth);
        overflow = amount >= (IsNegative ? CountLeadingOnes() : CountLeadingZeros());
        return Shl(amount);
    }
    public LlvmAPInt UShlOverflow(LlvmAPInt amount, out bool overflow) =>
        UShlOverflow((uint)amount.GetLimitedValue(BitWidth), out overflow);
    public LlvmAPInt UShlOverflow(uint amount, out bool overflow)
    {
        overflow = amount >= BitWidth;
        if (overflow) return GetZero(BitWidth);
        overflow = amount > CountLeadingZeros();
        return Shl(amount);
    }
}
