// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// ConstantFoldScalarCall1, ConstantFoldIntrinsicCall2/ScalarCall3.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmConstant> operands, LlvmType type, LlvmDataLayout layout,
        LlvmFunction? contextFunction = null, Dictionary<string, ulong>? metrics = null) =>
        ConstantFoldIntrinsic(kind, operands, type, metrics, contextFunction: contextFunction);

    private static LlvmConstant? ConstantFoldFloatingIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmConstant> operands, LlvmType type, Dictionary<string, ulong>? metrics, LlvmCallBase? call)
    {
        Count(metrics, "constant_fold.floating.queries");
        if (operands.Any(value => value is LlvmPoisonValue))
        {
            Count(metrics, "constant_fold.floating.poison");
            return LlvmPoisonValue.Get(type);
        }
        var function = call?.Parent?.Parent;
        if (LlvmIntrinsic.IsFloatingMinMax(kind) && operands.Count == 2)
        {
            if (operands[0] is LlvmUndefValue) return operands[1];
            if (operands[1] is LlvmUndefValue) return operands[0];
        }
        if (operands[0] is LlvmUndefValue && operands.Count == 1 &&
            (kind == LlvmIntrinsicKind.Cos || kind == LlvmIntrinsicKind.Canonicalize))
            return LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(type.GetFltSemantics()));
        if (operands[0] is not LlvmConstantFP first) return null;
        LlvmAPFloat a = first.Value;
        LlvmAPFloat? result = null;
        if (kind == LlvmIntrinsicKind.FAbs) result = a.ClearSign();
        else if (kind == LlvmIntrinsicKind.Canonicalize) result = FoldCanonicalize(type, a, function);
        else if (TryFloatingRoundingMode(kind, out var rounding)) result = a.RoundToIntegral(rounding).Value;
        else if (operands.Count == 2 && operands[1] is LlvmConstantFP second)
            result = FoldFloatingPair(kind, a, second.Value);
        else if (operands.Count == 2 && operands[1] is LlvmConstantInt integer)
        {
            if (kind == LlvmIntrinsicKind.IsFPClass)
            {
                var classes = FloatingConstantClass(a);
                Count(metrics, "constant_fold.floating.class_test");
                return LlvmConstantInt.Get(type, ((ulong)classes & integer.Value.GetZExtValue()) != 0 ? 1UL : 0UL);
            }
            if (kind == LlvmIntrinsicKind.Ldexp) result = a.Scalbn(ClampFloatingExponent(integer.Value), LlvmRoundingMode.NearestTiesToEven).Value;
            if (kind == LlvmIntrinsicKind.PowI) result = FoldFloatingPowI(a, unchecked((int)integer.Value.GetSExtValue()));
        }
        else if (operands.Count == 3 && operands[1] is LlvmConstantFP multiplier && operands[2] is LlvmConstantFP addend &&
            (kind == LlvmIntrinsicKind.FMA || kind == LlvmIntrinsicKind.FMulAdd))
            result = a.FusedMultiplyAdd(multiplier.Value, addend.Value).Value;
        if (result is null) return FoldFloatingHostIntrinsic(kind, operands, type, metrics);
        Count(metrics, "constant_fold.floating.exact");
        return LlvmConstantFP.Get(type, result.Value);
    }

    private static LlvmFPClassTest FloatingConstantClass(LlvmAPFloat value)
    {
        if (value.IsNaN) return value.IsSignaling ? LlvmFPClassTest.SNaN : LlvmFPClassTest.QNaN;
        if (value.IsInfinity) return value.IsNegative ? LlvmFPClassTest.NegInfinity : LlvmFPClassTest.PosInfinity;
        if (value.IsZero) return value.IsNegative ? LlvmFPClassTest.NegZero : LlvmFPClassTest.PosZero;
        if (value.IsDenormal) return value.IsNegative ? LlvmFPClassTest.NegSubnormal : LlvmFPClassTest.PosSubnormal;
        return value.IsNegative ? LlvmFPClassTest.NegNormal : LlvmFPClassTest.PosNormal;
    }

    private static bool TryFloatingRoundingMode(LlvmIntrinsicKind kind, out LlvmRoundingMode mode)
    {
        mode = LlvmRoundingMode.NearestTiesToEven;
        if (kind == LlvmIntrinsicKind.Rint || kind == LlvmIntrinsicKind.NearbyInt || kind == LlvmIntrinsicKind.RoundEven) return true;
        if (kind == LlvmIntrinsicKind.Round) mode = LlvmRoundingMode.NearestTiesToAway;
        else if (kind == LlvmIntrinsicKind.Ceil) mode = LlvmRoundingMode.TowardPositive;
        else if (kind == LlvmIntrinsicKind.Floor) mode = LlvmRoundingMode.TowardNegative;
        else if (kind == LlvmIntrinsicKind.Trunc) mode = LlvmRoundingMode.TowardZero;
        else return false;
        return true;
    }

    private static LlvmAPFloat? FoldFloatingPair(LlvmIntrinsicKind kind, LlvmAPFloat a, LlvmAPFloat b)
    {
        if (kind == LlvmIntrinsicKind.CopySign) return a.CopySign(b);
        if (kind == LlvmIntrinsicKind.MinNum) return LlvmAPFloat.MinNum(a, b);
        if (kind == LlvmIntrinsicKind.MaxNum) return LlvmAPFloat.MaxNum(a, b);
        if (kind == LlvmIntrinsicKind.Minimum) return LlvmAPFloat.Minimum(a, b);
        if (kind == LlvmIntrinsicKind.Maximum) return LlvmAPFloat.Maximum(a, b);
        if (kind == LlvmIntrinsicKind.MinimumNum) return LlvmAPFloat.MinimumNum(a, b);
        if (kind == LlvmIntrinsicKind.MaximumNum) return LlvmAPFloat.MaximumNum(a, b);
        return null;
    }

    private static LlvmAPFloat FoldFloatingPowI(LlvmAPFloat value, int exponent)
    {
        uint magnitude = exponent < 0 ? unchecked(0U - (uint)exponent) : (uint)exponent;
        var result = LlvmAPFloat.GetOne(value.Semantics);
        while (magnitude != 0)
        {
            if ((magnitude & 1) != 0) result = result.Multiply(value).Value;
            value = value.Multiply(value).Value;
            magnitude >>= 1;
        }
        return exponent < 0 ? LlvmAPFloat.GetOne(value.Semantics).Divide(result).Value : result;
    }

    private static int ClampFloatingExponent(LlvmAPInt exponent)
    {
        if (exponent.BitWidth < 32) return (int)exponent.GetSExtValue();
        if (exponent.GetSignificantBits() <= 32) return (int)exponent.GetSExtValue();
        return exponent.IsNegative ? int.MinValue : int.MaxValue;
    }

    private static LlvmAPFloat? FoldCanonicalize(LlvmType type, LlvmAPFloat value, LlvmFunction? function)
    {
        if (value.IsZero) return LlvmAPFloat.GetZero(value.Semantics, value.IsNegative);
        if (!type.IsIEEELikeFPTy()) return null;
        if (value.IsNormal || value.IsInfinity) return value;
        if (!value.IsDenormal || function is null) return null;
        var mode = function.GetDenormalMode(value.Semantics);
        if (mode.Input == LlvmDenormalMode.IEEE && mode.Output == LlvmDenormalMode.IEEE) return value;
        if (mode.Input == LlvmDenormalMode.Dynamic ||
            mode.Input == LlvmDenormalMode.IEEE && mode.Output == LlvmDenormalMode.Dynamic) return null;
        bool positive = !value.IsNegative || mode.Input == LlvmDenormalMode.PositiveZero ||
            mode.Output == LlvmDenormalMode.PositiveZero && mode.Input == LlvmDenormalMode.IEEE;
        return LlvmAPFloat.GetZero(value.Semantics, !positive);
    }
}
