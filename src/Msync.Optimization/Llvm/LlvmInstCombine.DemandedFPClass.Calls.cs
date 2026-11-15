// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: demanded FPClass intrinsic calls.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? DemandedFPCall(LlvmCallInstruction call, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            if (call.CalledFunction?.IntrinsicDescriptor is not { } descriptor)
            { known = ComputeDemandedFP(call, demanded, query, depth + 1); known.KnownNot(~demanded); return FPClassConstant(call.Type, known.GetKnownFPClasses()); }
            var kind = descriptor.Kind;
            bool noSignedZeros = (flags & LlvmFastMathFlags.NoSignedZeros) != 0;
            if (kind == LlvmIntrinsicKind.FAbs)
            {
                if (SimplifyDemandedFPClass(call, 0, LlvmKnownFPClasses.InverseFAbs(demanded), out var source, query, depth + 1)) return call;
                var replacement = DemandedFAbs(ref known, call.GetArgument(0), demanded, source, noSignedZeros);
                return replacement ?? FPClassConstant(call.Type, known.GetKnownFPClasses());
            }
            if (kind == LlvmIntrinsicKind.ArithmeticFence)
            {
                if (SimplifyDemandedFPClass(call, 0, demanded, out known, query, depth + 1)) return call;
                return FPClassConstant(call.Type, known.GetKnownFPClasses());
            }
            if (kind == LlvmIntrinsicKind.CopySign)
            {
                if (SimplifyDemandedFPClass(call, 0, UnknownSignFPDemand(demanded), out var magnitude, query, depth + 1)) return call;
                if ((demanded & C.Negative) == demanded) return ReplaceOperand(call, 1, LlvmConstantFP.Get(call.Type, -1));
                if ((demanded & C.Positive) == demanded) return ReplaceOperand(call, 1, LlvmConstantFP.Get(call.Type, 0));
                if (DemandedCopySignMagnitude(call.GetArgument(0), demanded, magnitude, noSignedZeros) is { } replacement) return replacement;
                var sign = ComputeDemandedFP(call.GetArgument(1), C.All, query, depth + 1);
                if (magnitude.SignBit is bool magSign && sign.SignBit == magSign) return call.GetArgument(0);
                if ((flags & LlvmFastMathFlags.NoNaNs) != 0) sign.KnownNot(C.NaN);
                if (sign.SignBit is bool negative)
                {
                    call.DropUBImplyingAttrsAndMetadata();
                    return ReplaceOperand(call, 1, LlvmConstantFP.Get(call.Type, negative ? -1 : 0));
                }
                known = LlvmKnownFPClass.CopySign(magnitude, sign); known.KnownNot(~demanded);
                return FPClassConstant(call.Type, known.GetKnownFPClasses());
            }
            if (call.Type.IsAggregateType() || kind == LlvmIntrinsicKind.IsFPClass)
            {
                known = ComputeDemandedFP(call, demanded, query, depth + 1); known.KnownNot(~demanded);
                return FPClassConstant(call.Type, known.GetKnownFPClasses());
            }
            var semantics = call.Type.GetScalarType().GetFltSemantics();
            var mode = F.GetDenormalMode(semantics);
            if (kind == LlvmIntrinsicKind.FMA || kind == LlvmIntrinsicKind.FMulAdd)
            {
                C sourceDemand = demanded | (C.All & ~C.NaN);
                if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN;
                var sources = new LlvmKnownFPClass[3];
                bool fused = kind == LlvmIntrinsicKind.FMA;
                if (call.GetArgument(0) == call.GetArgument(1) && DemandedFPNoUndef(call.GetArgument(0), query, depth + 1))
                {
                    if (SimplifyDemandedFPClass(call, 0, sourceDemand, out sources[0], query, depth + 1) ||
                        SimplifyDemandedFPClass(call, 2, sourceDemand, out sources[2], query, depth + 1)) return call;
                    sources[1] = sources[0];
                    known = fused ? LlvmKnownFPClass.FMASquare(sources[0], sources[2], mode) : LlvmKnownFPClass.FMulAddSquare(sources[0], sources[2], mode);
                }
                else
                {
                    for (int index = 0; index < 3; ++index)
                        if (SimplifyDemandedFPClass(call, index, sourceDemand, out sources[index], query, depth + 1)) return call;
                    known = fused ? LlvmKnownFPClass.FMA(sources[0], sources[1], sources[2], mode) : LlvmKnownFPClass.FMulAdd(sources[0], sources[1], sources[2], mode);
                }
                return DemandedFPResult(call, flags, demanded, known, sources);
            }
            if (LlvmIntrinsic.IsFloatingMinMax(kind))
            {
                bool propagateNaN = kind == LlvmIntrinsicKind.Minimum || kind == LlvmIntrinsicKind.Maximum;
                C sourceDemand = propagateNaN && (demanded & C.NaN) == 0 ? demanded | (C.All & ~C.NaN) : C.All;
                if (SimplifyDemandedFPClass(call, 1, sourceDemand, out var right, query, depth + 1) ||
                    SimplifyDemandedFPClass(call, 0, sourceDemand, out var left, query, depth + 1)) return call;
                if (DemandedFPMinMax(call, demanded, ref known, left, right, noSignedZeros) is { } replacement) return replacement;
                C valid = demanded & known.GetKnownFPClasses();
                var inferred = flags;
                C zero = mode == LlvmDenormalModeInfo.IEEE ? C.Zero : C.Zero | C.Subnormal;
                if (!noSignedZeros && ((valid & zero) == 0 ||
                    (left.IsKnownNeverLogicalNegZero(mode) || right.IsKnownNeverLogicalPosZero(mode)) &&
                    (left.IsKnownNeverLogicalPosZero(mode) || right.IsKnownNeverLogicalNegZero(mode)))) inferred |= LlvmFastMathFlags.NoSignedZeros;
                if ((flags & LlvmFastMathFlags.NoNaNs) == 0 && (propagateNaN && (valid & C.NaN) == 0 || left.IsKnownNeverNaN() && right.IsKnownNeverNaN()))
                { call.DropUBImplyingAttrsAndMetadata(); inferred |= LlvmFastMathFlags.NoNaNs; }
                if (inferred == flags) return null;
                call.FastMathFlags = inferred; return call;
            }
            if (kind == LlvmIntrinsicKind.Exp || kind == LlvmIntrinsicKind.Exp2 || kind == LlvmIntrinsicKind.Exp10)
                return DemandedFPExp(call, demanded, ref known, flags, query, depth);
            if (kind == LlvmIntrinsicKind.Log || kind == LlvmIntrinsicKind.Log2 || kind == LlvmIntrinsicKind.Log10)
            {
                C sourceDemand = demanded & (C.NaN | C.PosInfinity);
                if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN | (C.Negative & ~C.NegZero);
                if ((demanded & C.NegInfinity) != 0) { sourceDemand |= C.Zero; if (mode.InputsMayBeZero) sourceDemand |= C.Subnormal; }
                if ((demanded & C.Normal) != 0) sourceDemand |= C.Normal | C.Subnormal;
                if ((demanded & C.Zero) != 0) sourceDemand |= C.PosNormal;
                if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1)) return call;
                known = LlvmKnownFPClass.Log(source, mode, false); known.KnownNot(~demanded);
                return DemandedFPResult(call, flags, demanded, known, source);
            }
            if (kind == LlvmIntrinsicKind.Sqrt) return DemandedFPSqrt(call, demanded, ref known, flags, query, depth, mode);
            if (kind == LlvmIntrinsicKind.Ldexp)
            {
                C sourceDemand = demanded & C.NaN;
                if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN;
                if ((demanded & C.PosInfinity) != 0) sourceDemand |= C.PosInfinity | C.PosNormal | C.PosSubnormal;
                if ((demanded & C.NegInfinity) != 0) sourceDemand |= C.NegInfinity | C.NegNormal | C.NegSubnormal;
                if ((demanded & (C.PosNormal | C.PosSubnormal)) != 0) sourceDemand |= C.PosNormal | C.PosSubnormal;
                if ((demanded & (C.NegNormal | C.NegSubnormal)) != 0) sourceDemand |= C.NegNormal | C.NegSubnormal;
                if ((demanded & C.PosZero) != 0)
                {
                    sourceDemand |= C.PosFinite;
                    if (FPInputMayBePositiveZero(mode)) sourceDemand |= C.NegSubnormal;
                    if (FPOutputMayBePositiveZero(mode)) sourceDemand |= C.NegNormal | C.NegSubnormal;
                }
                if ((demanded & C.NegZero) != 0) sourceDemand |= C.NegFinite;
                if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1)) return call;
                var exponent = LlvmKnownBitsAnalysis.Compute(call.GetArgument(1), depth + 1, query.Context, query.Tree,
                    query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC, query.CondContext);
                known = LlvmKnownFPClass.Ldexp(source, exponent.GetSignedMinValue(), exponent.GetSignedMaxValue(), semantics, mode);
                known.KnownNot(~demanded);
                return DemandedFPResult(call, flags, demanded, known, source);
            }
            if (TryFloatingIntegralIntrinsic(kind)) return DemandedFPIntegral(call, demanded, ref known, flags, query, depth, mode, kind);
            if (kind == LlvmIntrinsicKind.Canonicalize)
            {
                C sourceDemand = demanded;
                if ((demanded & C.QNaN) != 0) sourceDemand |= C.SNaN;
                if (mode != LlvmDenormalModeInfo.IEEE)
                {
                    if ((demanded & C.PosZero) != 0)
                    {
                        sourceDemand |= C.PosSubnormal;
                        if (FPInputMayBePositiveZero(mode) || FPOutputMayBePositiveZero(mode)) sourceDemand |= C.NegSubnormal;
                    }
                    if ((demanded & C.NegZero) != 0) sourceDemand |= C.NegSubnormal;
                }
                if (mode == LlvmDenormalModeInfo.PreserveSign)
                {
                    if ((demanded & C.PosZero) == 0) sourceDemand &= ~C.PosSubnormal;
                    if ((demanded & C.NegZero) == 0) sourceDemand &= ~C.NegSubnormal;
                }
                if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1)) return call;
                known = LlvmKnownFPClass.Canonicalize(source, mode); known.KnownNot(~demanded);
                if (FPClassConstant(call.Type, known.GetKnownFPClasses()) is { } constant) return constant;
                if (source.IsKnownNeverNaN() && (mode == LlvmDenormalModeInfo.IEEE || source.IsKnownNever(C.Subnormal))) return call.GetArgument(0);
                var inferred = InferFPValueFlags(flags, known.GetKnownFPClasses(), source);
                if (inferred == flags) return null;
                call.DropUBImplyingAttrsAndMetadata(); call.FastMathFlags = inferred; return call;
            }
            known = ComputeDemandedFP(call, demanded, query, depth + 1); known.KnownNot(~demanded);
            return FPClassConstant(call.Type, known.GetKnownFPClasses());
        }
    }
}
