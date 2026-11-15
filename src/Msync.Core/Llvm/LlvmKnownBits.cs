// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownBits.h/.cpp.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmKnownBits
{
    public LlvmAPInt Zero { get; }
    public LlvmAPInt One { get; }
    public uint BitWidth => Zero.BitWidth;
    public LlvmKnownBits(uint bitWidth) : this(LlvmAPInt.GetZero(bitWidth), LlvmAPInt.GetZero(bitWidth)) { }
    public LlvmKnownBits(LlvmAPInt zero, LlvmAPInt one)
    { Debug.Assert(zero.BitWidth == one.BitWidth); Zero = zero; One = one; }
    public bool HasConflict => !(Zero & One).IsZero;
    public bool IsUnknown => Zero.IsZero && One.IsZero;
    public bool IsConstant => Zero == ~One;
    public bool IsZero => Zero.IsAllOnes;
    public bool IsAllOnes => One.IsAllOnes;
    public bool IsNegative => One.IsNegative;
    public bool IsNonNegative => Zero.IsNegative;
    public bool IsNonZero => !One.IsZero;
    public bool IsStrictlyPositive => IsNonNegative && IsNonZero;
    public LlvmAPInt GetConstant() { Debug.Assert(IsConstant); return One; }
    public LlvmAPInt GetMinValue() => One;
    public LlvmAPInt GetMaxValue() => ~Zero;
    public LlvmAPInt GetSignedMinValue() => IsNonNegative ? One : One | LlvmAPInt.GetSignedMinValue(BitWidth);
    public LlvmAPInt GetSignedMaxValue() => IsNegative ? ~Zero : ~Zero & LlvmAPInt.GetSignedMaxValue(BitWidth);
    public uint CountMinTrailingZeros() => Zero.CountTrailingOnes();
    public uint CountMinTrailingOnes() => One.CountTrailingOnes();
    public uint CountMinLeadingZeros() => Zero.CountLeadingOnes();
    public uint CountMinLeadingOnes() => One.CountLeadingOnes();
    public uint CountMaxTrailingZeros() => One.CountTrailingZeros();
    public uint CountMaxTrailingOnes() => Zero.CountTrailingZeros();
    public uint CountMaxLeadingZeros() => One.CountLeadingZeros();
    public uint CountMaxLeadingOnes() => Zero.CountLeadingZeros();
    public uint CountMinSignBits() => IsNonNegative ? CountMinLeadingZeros() : IsNegative ? CountMinLeadingOnes() : 1;
    public uint CountMaxSignificantBits() => BitWidth - CountMinSignBits() + 1;
    public uint CountMaxActiveBits() => BitWidth - CountMinLeadingZeros();
    public static LlvmKnownBits MakeConstant(LlvmAPInt value) => new(~value, value);
    public LlvmKnownBits IntersectWith(LlvmKnownBits other) => new(Zero & other.Zero, One & other.One);
    public LlvmKnownBits UnionWith(LlvmKnownBits other) => new(Zero | other.Zero, One | other.One);
    public LlvmKnownBits Trunc(uint width) => new(Zero.Trunc(width), One.Trunc(width));
    public LlvmKnownBits AnyExt(uint width) => new(Zero.ZExt(width), One.ZExt(width));
    public LlvmKnownBits ZExt(uint width) => new(Zero.ZExt(width) | LlvmAPInt.GetHighBitsSet(width, width - BitWidth), One.ZExt(width));
    public LlvmKnownBits SExt(uint width) => new(Zero.SExt(width), One.SExt(width));
    public LlvmKnownBits AnyExtOrTrunc(uint width) => width < BitWidth ? Trunc(width) : AnyExt(width);
    public LlvmKnownBits ZExtOrTrunc(uint width) => width < BitWidth ? Trunc(width) : ZExt(width);
    public LlvmKnownBits SExtOrTrunc(uint width) => width < BitWidth ? Trunc(width) : SExt(width);
    public static bool HaveNoCommonBitsSet(LlvmKnownBits left, LlvmKnownBits right) => (left.Zero | right.Zero).IsAllOnes;
    public static LlvmKnownBits operator &(LlvmKnownBits left, LlvmKnownBits right) => new(left.Zero | right.Zero, left.One & right.One);
    public static LlvmKnownBits operator |(LlvmKnownBits left, LlvmKnownBits right) => new(left.Zero & right.Zero, left.One | right.One);
    public static LlvmKnownBits operator ^(LlvmKnownBits left, LlvmKnownBits right) => new((left.Zero & right.Zero) | (left.One & right.One), (left.Zero & right.One) | (left.One & right.Zero));
    public static LlvmKnownBits operator ~(LlvmKnownBits value) => new(value.One, value.Zero);
    internal LlvmKnownBits WithNonNegative() => new(Zero | LlvmAPInt.GetSignedMinValue(BitWidth), One);
    internal LlvmKnownBits WithNegative() => new(Zero, One | LlvmAPInt.GetSignedMinValue(BitWidth));
    internal static LlvmKnownBits Conflict(uint width) => new(LlvmAPInt.GetAllOnes(width), LlvmAPInt.GetAllOnes(width));
    internal LlvmKnownBits ResolveConflict() => HasConflict ? MakeConstant(LlvmAPInt.GetZero(BitWidth)) : this;
}
