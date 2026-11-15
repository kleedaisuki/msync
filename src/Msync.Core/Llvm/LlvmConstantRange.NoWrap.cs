// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/ConstantRange.cpp makeExactNoWrapRegion / makeExactMulNUWRegion / makeExactMulNSWRegion.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public static LlvmConstantRange MakeExactNoWrapRegion(LlvmOpcode opcode, LlvmAPInt other, uint noWrapKind)
    {
        Debug.Assert(noWrapKind is 1 or 2);
        bool unsigned = noWrapKind == 1;
        uint width = other.BitWidth;
        LlvmAPInt zero = LlvmAPInt.GetZero(width), signedMin = LlvmAPInt.GetSignedMinValue(width);
        switch (opcode)
        {
            case LlvmOpcode.Add:
                if (unsigned) return GetNonEmpty(zero, -other);
                return other.IsNegative ? GetNonEmpty(signedMin - other, signedMin) : GetNonEmpty(signedMin, signedMin - other);
            case LlvmOpcode.Sub:
                if (unsigned) return GetNonEmpty(other, zero);
                return other.IsNegative ? GetNonEmpty(signedMin, signedMin + other) : GetNonEmpty(signedMin + other, signedMin);
            case LlvmOpcode.Mul:
                return unsigned ? MakeExactMulNUWRegion(other) : MakeExactMulNSWRegion(other);
            case LlvmOpcode.Shl:
                if (other.Uge(new LlvmAPInt(width, width))) return GetFull(width);
                uint amount = (uint)other.GetZExtValue();
                LlvmAPInt one = new(width, 1);
                return unsigned ? GetNonEmpty(zero, LlvmAPInt.GetMaxValue(width).LShr(amount) + one)
                    : GetNonEmpty(signedMin.AShr(amount), LlvmAPInt.GetSignedMaxValue(width).AShr(amount) + one);
            default:
                throw new InvalidOperationException("The source exact no-wrap region supports add, sub, mul and shl.");
        }
    }

    private static LlvmConstantRange MakeExactMulNUWRegion(LlvmAPInt value)
    {
        uint width = value.BitWidth;
        if (value.IsZero) return GetFull(width);
        return GetNonEmpty(LlvmAPInt.RoundingUDiv(LlvmAPInt.GetMinValue(width), value, LlvmAPIntRounding.Up),
            LlvmAPInt.RoundingUDiv(LlvmAPInt.GetMaxValue(width), value, LlvmAPIntRounding.Down) + new LlvmAPInt(width, 1));
    }

    private static LlvmConstantRange MakeExactMulNSWRegion(LlvmAPInt value)
    {
        uint width = value.BitWidth;
        if (value.IsZero) return GetFull(width);
        LlvmAPInt minimum = LlvmAPInt.GetSignedMinValue(width), maximum = LlvmAPInt.GetSignedMaxValue(width);
        if (value.IsAllOnes) return new(-maximum, minimum);
        LlvmAPInt lower, upper;
        if (value.IsNegative)
        {
            lower = LlvmAPInt.RoundingSDiv(maximum, value, LlvmAPIntRounding.Up);
            upper = LlvmAPInt.RoundingSDiv(minimum, value, LlvmAPIntRounding.Down);
        }
        else
        {
            lower = LlvmAPInt.RoundingSDiv(minimum, value, LlvmAPIntRounding.Up);
            upper = LlvmAPInt.RoundingSDiv(maximum, value, LlvmAPIntRounding.Down);
        }
        return GetNonEmpty(lower, upper + new LlvmAPInt(width, 1));
    }
}
