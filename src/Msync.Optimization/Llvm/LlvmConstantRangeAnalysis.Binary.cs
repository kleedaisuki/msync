// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueTracking.cpp setLimitsForBinOp.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantRangeAnalysis
{
    private static LlvmConstantRange BinOpLimits(LlvmBinaryOperator operation, bool forSigned, bool useInfo)
    {
        uint width = operation.Type.GetScalarSizeInBits();
        var lower = LlvmAPInt.GetZero(width);
        var upper = lower;
        LlvmAPInt? lhs = MatchRangeInteger(operation.Left);
        LlvmAPInt? rhs = MatchRangeInteger(operation.Right);
        bool nsw = useInfo && operation.HasNoSignedWrap;
        bool nuw = useInfo && operation.HasNoUnsignedWrap;
        bool exact = useInfo && operation.IsExact;
        var signedMin = LlvmAPInt.GetSignedMinValue(width);
        var signedMax = LlvmAPInt.GetSignedMaxValue(width);
        switch (operation.Opcode)
        {
            case LlvmOpcode.Sub when lhs.HasValue:
            {
                var value = lhs.Value;
                if (forSigned && nsw && nuw) nuw = false;
                if (nuw) upper = LlvmConstantRange.Inc(value);
                else if (nsw)
                {
                    if (value.IsNegative) { lower = signedMin; upper = value - signedMax; }
                    else { lower = value - signedMax; upper = signedMin; }
                }
                break;
            }
            case LlvmOpcode.Add when rhs.HasValue && !rhs.Value.IsZero:
            {
                var value = rhs.Value;
                if (forSigned && nsw && nuw) nuw = false;
                if (nuw) lower = value;
                else if (nsw)
                {
                    if (value.IsNegative) { lower = signedMin; upper = LlvmConstantRange.Inc(signedMax + value); }
                    else { lower = signedMin + value; upper = LlvmConstantRange.Inc(signedMax); }
                }
                break;
            }
            case LlvmOpcode.And:
            {
                if (rhs is { } mask) upper = LlvmConstantRange.Inc(mask);
                if (IsNegationOf(operation.Left, operation.Right) || IsNegationOf(operation.Right, operation.Left))
                    upper = LlvmConstantRange.Inc(signedMin);
                break;
            }
            case LlvmOpcode.Or:
            {
                if (rhs is { } mask) lower = mask;
                break;
            }
            case LlvmOpcode.AShr:
            {
                if (rhs is { } shift && shift.GetLimitedValue(width) < width)
                {
                    lower = signedMin.AShr(shift);
                    upper = LlvmConstantRange.Inc(signedMax.AShr(shift));
                }
                else if (lhs is { } value)
                {
                    uint amount = !value.IsZero && exact ? value.CountTrailingZeros() : width - 1;
                    if (value.IsNegative) { lower = value; upper = LlvmConstantRange.Inc(value.AShr(amount)); }
                    else { lower = value.AShr(amount); upper = LlvmConstantRange.Inc(value); }
                }
                break;
            }
            case LlvmOpcode.LShr:
            {
                if (rhs is { } shift && shift.GetLimitedValue(width) < width)
                    upper = LlvmConstantRange.Inc(LlvmAPInt.GetAllOnes(width).LShr(shift));
                else if (lhs is { } value)
                {
                    uint amount = !value.IsZero && exact ? value.CountTrailingZeros() : width - 1;
                    lower = value.LShr(amount);
                    upper = LlvmConstantRange.Inc(value);
                }
                break;
            }
            case LlvmOpcode.Shl:
            {
                if (lhs is { } value)
                {
                    if (nuw)
                    {
                        lower = value;
                        upper = LlvmConstantRange.Inc(lower.Shl(lower.CountLeadingZeros()));
                    }
                    else if (operation.HasNoSignedWrap)
                    {
                        // The pinned source reads this flag directly, unlike the IIQ NUW query above.
                        uint amount = value.IsNegative ? value.CountLeadingOnes() - 1 : value.CountLeadingZeros() - 1;
                        lower = value.IsNegative ? value.Shl(amount) : value;
                        upper = LlvmConstantRange.Inc(value.IsNegative ? value : value.Shl(amount));
                    }
                    else
                    {
                        if ((value.GetWord(0) & 1) != 0) lower = new(width, 1);
                        upper = LlvmConstantRange.Inc(LlvmAPInt.GetHighBitsSet(width, value.PopCount()));
                    }
                }
                else if (rhs is { } shift && shift.GetLimitedValue(width) < width)
                    upper = LlvmConstantRange.Inc(LlvmAPInt.GetBitsSetFrom(width, (uint)shift.GetZExtValue()));
                break;
            }
            case LlvmOpcode.SDiv:
            {
                if (rhs is { } divisor)
                {
                    if (divisor.IsAllOnes) { lower = LlvmConstantRange.Inc(signedMin); upper = LlvmConstantRange.Inc(signedMax); }
                    else if (divisor.CountLeadingZeros() < width - 1)
                    {
                        lower = signedMin.SDiv(divisor);
                        upper = signedMax.SDiv(divisor);
                        if (lower.Sgt(upper)) (lower, upper) = (upper, lower);
                        upper = LlvmConstantRange.Inc(upper);
                        Debug.Assert(upper != lower);
                    }
                }
                else if (lhs is { } value)
                {
                    if (value.IsMinSignedValue) { lower = value; upper = LlvmConstantRange.Inc(lower.LShr(1)); }
                    else { upper = LlvmConstantRange.Inc(value.IsNegative ? -value : value); lower = LlvmConstantRange.Inc(-upper); }
                }
                break;
            }
            case LlvmOpcode.UDiv:
            {
                if (rhs is { IsZero: false } divisor) upper = LlvmConstantRange.Inc(LlvmAPInt.GetMaxValue(width).UDiv(divisor));
                else if (lhs is { } value) upper = LlvmConstantRange.Inc(value);
                break;
            }
            case LlvmOpcode.SRem:
            {
                if (rhs is { } divisor)
                {
                    upper = divisor.IsNegative ? -divisor : divisor;
                    lower = LlvmConstantRange.Inc(-upper);
                }
                else if (lhs is { } value)
                {
                    if (value.IsNegative) { lower = value; upper = new(width, 1); }
                    else upper = LlvmConstantRange.Inc(value);
                }
                break;
            }
            case LlvmOpcode.URem:
            {
                if (rhs is { } divisor) upper = divisor;
                else if (lhs is { } value) upper = LlvmConstantRange.Inc(value);
                break;
            }
        }
        return LlvmConstantRange.GetNonEmpty(lower, upper);
    }
    private static bool IsNegationOf(LlvmValue value, LlvmValue other) => value is LlvmBinaryOperator
        { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } operation && ReferenceEquals(operation.Right, other);
}
