// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat convertToSignExtendedInteger / convertToInteger; binary64 input.
using System.Diagnostics;
using C = Msync.Core.Llvm.LlvmFloatCategory;
using S = Msync.Core.Llvm.LlvmFloatStatus;

namespace Msync.Core.Llvm;

public readonly record struct LlvmFloatIntegerResult(LlvmAPInt Value, LlvmFloatStatus Status, bool IsExact);

public readonly partial struct LlvmAPFloat
{
    public LlvmFloatIntegerResult ConvertToInteger(uint width, bool isSigned,
        LlvmRoundingMode mode = LlvmRoundingMode.TowardZero)
    {
        Debug.Assert(width != 0);
        IeeeFloat value = new(this);
        ulong[] parts = new ulong[((ulong)width + 63) / 64];
        S status = value.ConvertToSignExtendedInteger(parts, width, isSigned, mode, out bool exact);
        LlvmAPInt integer = new(width, parts);
        if (status == S.InvalidOp)
        {
            if (IsNaN) integer = LlvmAPInt.GetZero(width);
            else if (IsNegative) integer = isSigned ? LlvmAPInt.GetSignedMinValue(width) : LlvmAPInt.GetZero(width);
            else integer = isSigned ? LlvmAPInt.GetSignedMaxValue(width) : LlvmAPInt.GetAllOnes(width);
        }
        return new(integer, status, exact);
    }
    private partial struct IeeeFloat
    {
        internal S ConvertToSignExtendedInteger(Span<ulong> parts, uint width, bool isSigned,
            LlvmRoundingMode mode, out bool exact)
        {
            exact = false;
            if (Category is C.Infinity or C.NaN) return S.InvalidOp;
            if (Category == C.Zero) { parts.Clear(); exact = !Sign; return S.OK; }
            uint truncated;
            ReadOnlySpan<ulong> source = [Significand];
            if (Exponent < 0)
            {
                parts.Clear();
                truncated = (uint)(Precision - 1 - Exponent);
            }
            else
            {
                uint count = (uint)Exponent + 1;
                if (count > width) return S.InvalidOp;
                if (count < Precision)
                {
                    truncated = (uint)Precision - count;
                    LlvmAPInt.ExtractWords(parts, source, count, truncated);
                }
                else
                {
                    LlvmAPInt.ExtractWords(parts, source, (uint)Precision, 0);
                    LlvmAPInt.ShiftWordsLeft(parts, count - (uint)Precision);
                    truncated = 0;
                }
            }
            LostFraction fraction = LostFraction.ExactlyZero;
            if (truncated != 0)
            {
                fraction = TruncatedFraction(Significand, truncated);
                if (fraction != LostFraction.ExactlyZero && RoundAwayFromZero(mode, fraction, truncated))
                {
                    int index = 0;
                    for (; index < parts.Length; ++index)
                    {
                        parts[index] = unchecked(parts[index] + 1);
                        if (parts[index] != 0) break;
                    }
                    if (index == parts.Length) return S.InvalidOp;
                }
            }
            uint omsb = unchecked(LlvmAPInt.WordsMSB(parts) + 1);
            if (Sign)
            {
                if (!isSigned) { if (omsb != 0) return S.InvalidOp; }
                else
                {
                    if (omsb == width && unchecked(LlvmAPInt.WordsLSB(parts) + 1) != omsb) return S.InvalidOp;
                    if (omsb > width) return S.InvalidOp;
                }
                // tcNegate: complement then increment, including unused high bits.
                for (int i = 0; i < parts.Length; ++i) parts[i] = ~parts[i];
                for (int i = 0; i < parts.Length; ++i)
                {
                    parts[i] = unchecked(parts[i] + 1);
                    if (parts[i] != 0) break;
                }
            }
            else if (omsb >= width + (isSigned ? 0U : 1U)) return S.InvalidOp;
            if (fraction == LostFraction.ExactlyZero) { exact = true; return S.OK; }
            return S.Inexact;
        }
    }
}
