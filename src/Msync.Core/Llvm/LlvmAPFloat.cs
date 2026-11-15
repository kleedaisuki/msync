// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.h semantics IDs and
// APFloat.cpp initFromIEEEAPInt/convertIEEEFloatToAPInt for one-word IEEE formats.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public enum LlvmAPFloatSemantics : byte { IEEEhalf = 0, BFloat = 1, IEEEsingle = 2, IEEEdouble = 3 }
public enum LlvmFloatCategory : byte { Infinity, NaN, Normal, Zero }

public readonly partial struct LlvmAPFloat : IEquatable<LlvmAPFloat>
{
    private readonly ulong bits;
    // Preserve the existing CLR-default value as binary64 positive zero.
    private readonly byte semanticsCode;
    public LlvmAPFloatSemantics Semantics => (LlvmAPFloatSemantics)(semanticsCode ^ 3);
    public LlvmAPFloat(double value)
    { bits = BitConverter.DoubleToUInt64Bits(value); semanticsCode = 0; }
    public LlvmAPFloat(LlvmAPFloatSemantics semantics, LlvmAPInt value)
    {
        Debug.Assert(value.BitWidth == SemanticsSizeInBits(semantics));
        bits = value.GetZExtValue();
        semanticsCode = (byte)((byte)semantics ^ 3);
    }
    public static LlvmAPFloat FromBits(ulong value) => FromBits(LlvmAPFloatSemantics.IEEEdouble, value);
    public static LlvmAPFloat FromBits(LlvmAPFloatSemantics semantics, ulong value) =>
        new(semantics, new LlvmAPInt(SemanticsSizeInBits(semantics), value, implicitTrunc: true));
    public LlvmAPInt BitcastToAPInt() => new(SemanticsSizeInBits(Semantics), bits);
    public bool IsNegative => (bits & Format.SignMask) != 0;
    public LlvmFloatCategory Category
    {
        get
        {
            var format = Format;
            ulong significand = bits & format.FractionMask;
            ulong exponent = (bits >> format.FractionBits) & format.ExponentMask;
            if (exponent == format.ExponentMask) return significand == 0 ? LlvmFloatCategory.Infinity : LlvmFloatCategory.NaN;
            return exponent == 0 && significand == 0 ? LlvmFloatCategory.Zero : LlvmFloatCategory.Normal;
        }
    }
    public bool IsZero => Category == LlvmFloatCategory.Zero;
    public bool IsNaN => Category == LlvmFloatCategory.NaN;
    public bool IsInfinity => Category == LlvmFloatCategory.Infinity;
    public bool IsFinite => !IsNaN && !IsInfinity;
    public bool IsFiniteNonZero => Category == LlvmFloatCategory.Normal;
    public bool IsDenormal => IsFiniteNonZero && (bits & (Format.ExponentMask << Format.FractionBits)) == 0;
    public bool IsNormal => IsFiniteNonZero && !IsDenormal;
    public bool IsSmallest => IsFiniteNonZero && (bits & ~Format.SignMask) == 1;
    public bool IsSmallestNormalized => IsFiniteNonZero && (bits & ~Format.SignMask) == (1UL << Format.FractionBits);
    public bool IsLargest => IsFiniteNonZero && (bits & ~Format.SignMask) == ((Format.ExponentMask - 1) << Format.FractionBits | Format.FractionMask);
    public bool BitwiseIsEqual(LlvmAPFloat other) => Semantics == other.Semantics && bits == other.bits;
    public bool Equals(LlvmAPFloat other) => BitwiseIsEqual(other);
    public override bool Equals(object? value) => value is LlvmAPFloat other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Semantics, bits);
}
