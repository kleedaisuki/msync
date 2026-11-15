// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/APInt.cpp
// GreatestCommonDivisor, sqrtFloor, RoundingUDiv and RoundingSDiv.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public enum LlvmAPIntRounding : byte { Down, TowardZero, Up }

public readonly partial struct LlvmAPInt
{
    public static LlvmAPInt GreatestCommonDivisor(LlvmAPInt left, LlvmAPInt right, bool isSigned = false)
    {
        Debug.Assert(left.BitWidth == right.BitWidth);
        if (isSigned) { if (left.IsNegative) left = -left; if (right.IsNegative) right = -right; }
        if (left == right) return left;
        if (left.IsZero) return right;
        if (right.IsZero) return left;
        uint a = left.CountTrailingZeros(), b = right.CountTrailingZeros(), common;
        if (a > b) { left = left.LShr(a - b); common = b; }
        else if (b > a) { right = right.LShr(b - a); common = a; }
        else common = a;
        while (left != right)
        {
            if (left.Ugt(right)) { left -= right; left = left.LShr(left.CountTrailingZeros() - common); }
            else { right -= left; right = right.LShr(right.CountTrailingZeros() - common); }
        }
        return left;
    }

    public LlvmAPInt SqrtFloor()
    {
        uint magnitude = GetActiveBits();
        if (magnitude <= 5)
        {
            ReadOnlySpan<byte> values = [0, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3,
                4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5];
            return new(BitWidth, values[(int)GetWord(0)]);
        }
        // The upstream algorithm explicitly uses hardware/libc sqrt only below
        // 52 active bits, where the integer input is represented exactly.
        if (magnitude < 52) return new(BitWidth, (ulong)Math.Floor(Math.Sqrt(GetWord(0))));
        uint bit = 4;
        LlvmAPInt test = new(BitWidth, 16), previous = new(BitWidth, 1), two = new(BitWidth, 2);
        for (; ; bit += 2, test = test.Shl(2))
            if (bit >= BitWidth || Ule(test)) { previous = previous.Shl(bit / 2); break; }
        while (true)
        {
            LlvmAPInt current = (UDiv(previous) + previous).UDiv(two);
            if (previous.Ule(current)) return previous;
            previous = current;
        }
    }

    public static LlvmAPInt RoundingUDiv(LlvmAPInt left, LlvmAPInt right, LlvmAPIntRounding rounding)
    {
        if (rounding is LlvmAPIntRounding.Down or LlvmAPIntRounding.TowardZero) return left.UDiv(right);
        UDivRem(left, right, out var quotient, out var remainder);
        return remainder.IsZero ? quotient : quotient + new LlvmAPInt(quotient.BitWidth, 1, implicitTrunc: true);
    }

    public static LlvmAPInt RoundingSDiv(LlvmAPInt left, LlvmAPInt right, LlvmAPIntRounding rounding)
    {
        if (rounding == LlvmAPIntRounding.TowardZero) return left.SDiv(right);
        SDivRem(left, right, out var quotient, out var remainder);
        if (remainder.IsZero) return quotient;
        bool negativeFraction = remainder.IsNegative != right.IsNegative;
        LlvmAPInt one = new(quotient.BitWidth, 1, implicitTrunc: true);
        return rounding == LlvmAPIntRounding.Down
            ? negativeFraction ? quotient - one : quotient
            : negativeFraction ? quotient : quotient + one;
    }
}
