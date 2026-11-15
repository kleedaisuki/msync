// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp fltSemantics
// declarations and makeZero/makeInf/makeLargest/makeSmallestNormalized/getOne.
namespace Msync.Core.Llvm;

public readonly record struct LlvmAPFloatConvertResult(LlvmAPFloat Value, LlvmFloatStatus Status, bool LosesInfo);

public readonly partial struct LlvmAPFloat
{
    private readonly record struct IeeeFormat(int Precision, int MinExponent, int MaxExponent, uint Size)
    {
        internal int FractionBits => Precision - 1;
        internal int Bias => MaxExponent;
        internal ulong FractionMask => (1UL << FractionBits) - 1;
        internal ulong ExponentMask => (ulong)(MaxExponent * 2 + 1);
        internal ulong SignMask => 1UL << (int)(Size - 1);
    }
    private IeeeFormat Format => GetFormat(Semantics);
    private static IeeeFormat GetFormat(LlvmAPFloatSemantics semantics) => semantics switch
    {
        LlvmAPFloatSemantics.IEEEhalf => new(11, -14, 15, 16),
        LlvmAPFloatSemantics.BFloat => new(8, -126, 127, 16),
        LlvmAPFloatSemantics.IEEEsingle => new(24, -126, 127, 32),
        LlvmAPFloatSemantics.IEEEdouble => new(53, -1022, 1023, 64),
        _ => throw new NotSupportedException($"APFloat semantics {semantics} needs a multiword implementation.")
    };
    public static uint SemanticsPrecision(LlvmAPFloatSemantics semantics) => (uint)GetFormat(semantics).Precision;
    public static int SemanticsMinExponent(LlvmAPFloatSemantics semantics) => GetFormat(semantics).MinExponent;
    public static int SemanticsMaxExponent(LlvmAPFloatSemantics semantics) => GetFormat(semantics).MaxExponent;
    public static uint SemanticsSizeInBits(LlvmAPFloatSemantics semantics) => GetFormat(semantics).Size;
    public static uint SemanticsIntSizeInBits(LlvmAPFloatSemantics semantics, bool isSigned) =>
        (uint)(GetFormat(semantics).MaxExponent + (isSigned ? 2 : 1));
    public static bool IsLosslesslyConvertibleTo(LlvmAPFloatSemantics from,
        LlvmAPFloatSemantics to, bool ignoreNaNs = false)
    {
        if (from == to) return true;
        var source = GetFormat(from);
        var destination = GetFormat(to);
        if (source.MaxExponent > destination.MaxExponent ||
            source.MinExponent < destination.MinExponent ||
            source.Precision > destination.Precision) return false;
        if (GetSmallest(from).Convert(to, LlvmRoundingMode.NearestTiesToEven).LosesInfo)
            return false;
        // All represented formats are IEEE754: conversion quiets signaling NaNs.
        return ignoreNaNs;
    }
    public static LlvmAPFloat GetZero(LlvmAPFloatSemantics semantics, bool negative = false) =>
        FromBits(semantics, negative ? GetFormat(semantics).SignMask : 0);
    public static LlvmAPFloat GetOne(LlvmAPFloatSemantics semantics, bool negative = false)
    {
        var format = GetFormat(semantics);
        return FromBits(semantics, ((ulong)format.Bias << format.FractionBits) | (negative ? format.SignMask : 0));
    }
    public static LlvmAPFloat GetInf(LlvmAPFloatSemantics semantics, bool negative = false)
    {
        var format = GetFormat(semantics);
        return FromBits(semantics, (format.ExponentMask << format.FractionBits) | (negative ? format.SignMask : 0));
    }
    public static LlvmAPFloat GetLargest(LlvmAPFloatSemantics semantics, bool negative = false)
    {
        var format = GetFormat(semantics);
        return FromBits(semantics, ((format.ExponentMask - 1) << format.FractionBits) |
            format.FractionMask | (negative ? format.SignMask : 0));
    }
    public static LlvmAPFloat GetSmallest(LlvmAPFloatSemantics semantics, bool negative = false) =>
        FromBits(semantics, 1 | (negative ? GetFormat(semantics).SignMask : 0));
    public static LlvmAPFloat GetSmallestNormalized(LlvmAPFloatSemantics semantics, bool negative = false) =>
        FromBits(semantics, (1UL << GetFormat(semantics).FractionBits) | (negative ? GetFormat(semantics).SignMask : 0));
}
