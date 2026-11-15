// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeKnownFPClass Call scalar intrinsic branches.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownFPClasses
{
    private static LlvmKnownFPClass Intrinsic(LlvmCallInstruction call, C interested,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth,
        LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions, LlvmCondContext? condContext = null)
    {
        if (call.CalledFunction?.IntrinsicDescriptor is not { } descriptor) return new();
        var kind = descriptor.Kind;
        if (!LlvmIntrinsic.IsFloatingIntrinsic(kind)) return new();
        if (metrics is not null) Count(metrics, "known_fp.intrinsic." + kind.Name);
        LlvmKnownFPClass Recurse(int index, C classes) =>
            Compute(call.GetArgument(index), classes, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext);
        var mode = call.Parent?.Parent?.GetDenormalMode(call.GetArgument(0).Type.GetScalarType().GetFltSemantics()) ?? LlvmDenormalModeInfo.Dynamic;
        if (kind == LlvmIntrinsicKind.FAbs)
            return LlvmKnownFPClass.FAbs((interested & (C.NaN | C.Positive)) == 0 ? new() : Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.CopySign) return LlvmKnownFPClass.CopySign(Recurse(0, interested), Recurse(1, interested));
        if (kind == LlvmIntrinsicKind.ArithmeticFence) return Recurse(0, interested);
        if (kind == LlvmIntrinsicKind.Canonicalize) return LlvmKnownFPClass.Canonicalize(Recurse(0, interested), mode);
        if (kind == LlvmIntrinsicKind.Sqrt)
        {
            C sourceInterest = interested;
            if ((interested & C.QNaN) != 0) sourceInterest |= LlvmKnownFPClass.OrderedLessThanZeroMask | C.SNaN;
            if ((interested & C.Zero) != 0) sourceInterest |= C.Subnormal;
            bool noSignedZeros = (call.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0;
            var known = LlvmKnownFPClass.Sqrt(Recurse(0, sourceInterest), noSignedZeros ? LlvmDenormalModeInfo.Dynamic : mode);
            if (noSignedZeros) known.KnownNot(C.NegZero);
            return known;
        }
        if (kind == LlvmIntrinsicKind.Sin) return LlvmKnownFPClass.Sin(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.Cos) return LlvmKnownFPClass.Cos(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.Tan) return LlvmKnownFPClass.Tan(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.Sinh) return LlvmKnownFPClass.Sinh(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.Cosh) return LlvmKnownFPClass.Cosh(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.Tanh) return LlvmKnownFPClass.Tanh(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.ASin) return LlvmKnownFPClass.ASin(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.ACos) return LlvmKnownFPClass.ACos(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.ATan) return LlvmKnownFPClass.ATan(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.ATan2) return IntrinsicATan2(call, interested, Recurse, mode);
        if (LlvmIntrinsic.IsFloatingMinMax(kind))
        {
            LlvmFPMinMaxKind operation = kind == LlvmIntrinsicKind.MinNum ? LlvmFPMinMaxKind.MinNum :
                kind == LlvmIntrinsicKind.MaxNum ? LlvmFPMinMaxKind.MaxNum :
                kind == LlvmIntrinsicKind.Minimum ? LlvmFPMinMaxKind.Minimum :
                kind == LlvmIntrinsicKind.Maximum ? LlvmFPMinMaxKind.Maximum :
                kind == LlvmIntrinsicKind.MinimumNum ? LlvmFPMinMaxKind.MinimumNum : LlvmFPMinMaxKind.MaximumNum;
            return LlvmKnownFPClass.MinMaxLike(Recurse(0, interested), Recurse(1, interested), operation, mode);
        }
        if (kind == LlvmIntrinsicKind.FMA || kind == LlvmIntrinsicKind.FMulAdd)
            return IntrinsicFMA(call, interested, Recurse, mode);
        if (TryFloatingIntegralIntrinsic(kind))
        {
            C sourceInterest = interested;
            if ((sourceInterest & C.NegFinite) != 0) sourceInterest |= C.NegFinite;
            if ((sourceInterest & C.PosFinite) != 0) sourceInterest |= C.PosFinite | C.NegSubnormal;
            var source = Recurse(0, sourceInterest);
            bool multiUnit = call.Type.IsPPC_FP128Ty();
            if (kind == LlvmIntrinsicKind.Trunc) return LlvmKnownFPClass.Trunc(source, mode);
            if (kind == LlvmIntrinsicKind.Floor) return LlvmKnownFPClass.Floor(source, multiUnit, mode);
            if (kind == LlvmIntrinsicKind.Ceil) return LlvmKnownFPClass.Ceil(source, multiUnit, mode);
            return LlvmKnownFPClass.RoundToIntegral(source, multiUnit, mode);
        }
        if (kind == LlvmIntrinsicKind.Exp || kind == LlvmIntrinsicKind.Exp2 || kind == LlvmIntrinsicKind.Exp10)
            return LlvmKnownFPClass.Exp(Recurse(0, interested));
        if (kind == LlvmIntrinsicKind.Log || kind == LlvmIntrinsicKind.Log2 || kind == LlvmIntrinsicKind.Log10)
        {
            C sourceInterest = C.None;
            if ((interested & C.NaN) != 0) sourceInterest |= C.NaN | C.Negative;
            if ((interested & C.NegInfinity) != 0) sourceInterest |= C.Zero | C.Subnormal;
            if ((interested & C.NegNormal) != 0) sourceInterest |= C.PosSubnormal | C.PosNormal;
            if ((interested & (C.PosZero | C.PosNormal)) != 0) sourceInterest |= C.PosNormal;
            if ((interested & C.PosInfinity) != 0) sourceInterest |= C.PosInfinity;
            return LlvmKnownFPClass.Log(sourceInterest == C.None ? new() : Recurse(0, sourceInterest), mode, call.Type.IsPPC_FP128Ty());
        }
        if (kind == LlvmIntrinsicKind.Pow)
        {
            bool wantNaN = (interested & C.NaN) != 0, wantNegative = (interested & C.Negative) != 0;
            if (!wantNaN && !wantNegative) return new();
            C leftInterest = wantNaN ? C.NaN | C.NegNormal | C.NegSubnormal : C.None;
            C rightInterest = wantNaN ? C.NaN : C.None;
            if (wantNegative) { leftInterest |= C.Negative; rightInterest |= C.Normal; }
            var left = Recurse(0, leftInterest);
            return left.IsUnknown ? new() : LlvmKnownFPClass.Pow(left, Recurse(1, rightInterest));
        }
        if (kind == LlvmIntrinsicKind.PowI)
        {
            if ((interested & (C.NaN | C.Infinity | C.Negative)) == 0) return new();
            var exponent = LlvmKnownBitsAnalysis.Compute(call.GetArgument(1), (uint)(depth + 1), context, tree, metrics,
                assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
            C sourceInterest = (interested & C.NaN) != 0 ? C.NaN : C.None;
            if (!exponent.IsZero)
            {
                if ((interested & C.Infinity) != 0) sourceInterest |= C.Finite | C.Infinity;
                if ((interested & C.Negative) != 0 && (exponent.Zero.GetWord(0) & 1) == 0) sourceInterest |= C.Negative;
            }
            return LlvmKnownFPClass.PowI(sourceInterest == C.None ? new() : Recurse(0, sourceInterest), exponent);
        }
        if (kind == LlvmIntrinsicKind.Ldexp)
        {
            var source = Recurse(0, interested);
            var exponentValue = call.GetArgument(1);
            var range = (source.GetKnownFPClasses() & (C.Zero | C.Subnormal | C.Infinity)) != 0
                ? LlvmConstantRangeAnalysis.Compute(exponentValue, true, (uint)(depth + 1), context, tree, metrics,
                    assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions)
                : LlvmConstantRange.Full(exponentValue.Type.GetIntegerBitWidth());
            return LlvmKnownFPClass.Ldexp(source, range.GetSignedMin(), range.GetSignedMax(), call.GetArgument(0).Type.GetScalarType().GetFltSemantics(), mode);
        }
        return new();
    }

    private static bool TryFloatingIntegralIntrinsic(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.Trunc ||
        kind == LlvmIntrinsicKind.Floor || kind == LlvmIntrinsicKind.Ceil || kind == LlvmIntrinsicKind.Round ||
        kind == LlvmIntrinsicKind.RoundEven || kind == LlvmIntrinsicKind.Rint || kind == LlvmIntrinsicKind.NearbyInt;

    private static LlvmKnownFPClass IntrinsicFMA(LlvmCallInstruction call, C interested,
        Func<int, C, LlvmKnownFPClass> recurse, LlvmDenormalModeInfo mode)
    {
        if ((interested & (C.Negative | C.NaN)) == 0) return new();
        bool fused = call.CalledFunction!.IntrinsicDescriptor!.Kind == LlvmIntrinsicKind.FMA;
        C excluded = Excluded(call.FastMathFlags);
        if (ReferenceEquals(call.GetArgument(0), call.GetArgument(1)))
        {
            C squaredInterest = interested, addendInterest = interested;
            if ((interested & C.QNaN) != 0)
            { squaredInterest |= C.SNaN | C.Infinity; addendInterest |= C.SNaN | C.NegInfinity; }
            var addend = recurse(2, addendInterest);
            var squared = recurse(0, squaredInterest);
            squared.KnownNot(excluded); addend.KnownNot(excluded);
            return fused ? LlvmKnownFPClass.FMASquare(squared, addend, mode) : LlvmKnownFPClass.FMulAddSquare(squared, addend, mode);
        }
        var left = recurse(0, interested);
        if (left.IsUnknown) return new();
        left.KnownNot(excluded);
        var right = recurse(1, interested);
        if (right.IsUnknown) return new();
        right.KnownNot(excluded);
        var third = recurse(2, interested);
        if (third.IsUnknown) return new();
        third.KnownNot(excluded);
        return fused ? LlvmKnownFPClass.FMA(left, right, third, mode) : LlvmKnownFPClass.FMulAdd(left, right, third, mode);
    }

    private static LlvmKnownFPClass IntrinsicATan2(LlvmCallInstruction call, C interested,
        Func<int, C, LlvmKnownFPClass> recurse, LlvmDenormalModeInfo mode)
    {
        C yInterest = interested, xInterest = interested;
        if ((interested & C.NegFinite) != 0) yInterest |= C.Negative;
        if ((interested & C.PosFinite) != 0) yInterest |= C.Positive | C.NegSubnormal;
        if ((interested & (C.Zero | C.Subnormal)) != 0) xInterest |= C.Positive | C.NegSubnormal;
        return LlvmKnownFPClass.ATan2(recurse(0, yInterest), recurse(1, xInterest), mode);
    }
}
