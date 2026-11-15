// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: demanded FPClass exp/sqrt/integral.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool TryFloatingIntegralIntrinsic(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.Trunc ||
            kind == LlvmIntrinsicKind.Floor || kind == LlvmIntrinsicKind.Ceil || kind == LlvmIntrinsicKind.Round ||
            kind == LlvmIntrinsicKind.RoundEven || kind == LlvmIntrinsicKind.Rint || kind == LlvmIntrinsicKind.NearbyInt;
        private LlvmValue? DemandedFPExp(LlvmCallInstruction call, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            var type = call.Type; var semantics = type.GetScalarType().GetFltSemantics();
            var x = call.GetArgument(0);
            if ((demanded & C.Positive) == 0)
            {
                if ((demanded & C.NaN) == 0) return LlvmPoisonValue.Get(type);
                if ((demanded & ~C.NaN) == 0) return LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0x7ff8000000000000UL).Convert(semantics).Value);
                return x;
            }
            C sourceDemand = demanded & C.NaN;
            if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN;
            if ((demanded & C.Zero) != 0) sourceDemand |= C.NegInfinity | C.NegNormal;
            if ((demanded & C.PosSubnormal) != 0) sourceDemand |= C.NegNormal;
            if ((demanded & C.PosNormal) != 0) sourceDemand |= C.Normal | C.Subnormal | C.Zero;
            if ((demanded & C.PosInfinity) != 0) sourceDemand |= C.PosInfinity | C.PosNormal;
            if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1)) return call;
            var one = LlvmConstantFP.Get(type, LlvmAPFloat.GetOne(semantics));
            if (source.IsKnownAlways(C.Zero)) return one;
            if (source.IsKnownAlways(C.NaN)) return x;
            using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            Builder.SetInsertPoint(call);
            if (source.IsKnownAlways(C.Zero | C.NaN)) return Builder.CreateBinary(LlvmOpcode.FAdd, x, one, fastMathFlags: flags);
            if (source.IsKnownAlways(C.Infinity | C.NaN))
            {
                var infinity = LlvmConstantFP.Get(type, LlvmAPFloat.GetInf(semantics));
                var positive = Builder.CreateFCmp(LlvmCmpPredicate.FCmpUeq, x, infinity, fastMathFlags: flags);
                return Builder.CreateSelect(positive, x, LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(semantics)), fastMathFlags: flags);
            }
            known = LlvmKnownFPClass.Exp(source); known.KnownNot(~demanded);
            return DemandedFPResult(call, flags, demanded, known, source);
        }
        private LlvmValue? DemandedFPSqrt(LlvmCallInstruction call, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth, LlvmDenormalModeInfo mode)
        {
            C sourceDemand = demanded & C.SNaN;
            if ((demanded & C.QNaN) != 0) sourceDemand |= C.QNaN | C.SNaN | C.NegInfinity | C.NegNormal | C.NegSubnormal;
            if ((demanded & C.NegZero) != 0)
            {
                sourceDemand |= C.NegZero;
                if (mode.Input is LlvmDenormalMode.PreserveSign or LlvmDenormalMode.Dynamic) sourceDemand |= C.NegSubnormal;
            }
            if ((demanded & C.PosZero) != 0)
            {
                sourceDemand |= C.PosZero;
                if (mode.InputsMayBeZero) sourceDemand |= C.PosSubnormal;
                if (FPInputMayBePositiveZero(mode)) sourceDemand |= C.NegSubnormal;
            }
            if ((demanded & C.PosNormal) != 0) sourceDemand |= C.PosSubnormal | C.PosNormal;
            if ((demanded & C.PosInfinity) != 0) sourceDemand |= C.PosInfinity;
            if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1)) return call;
            source.KnownNot(~sourceDemand);
            var semantics = call.Type.GetScalarType().GetFltSemantics();
            if (source.IsKnownNever(C.Positive) && source.IsKnownNeverLogicalZero(mode))
                return LlvmConstantFP.Get(call.Type, LlvmAPFloat.FromBits(0x7ff8000000000000UL).Convert(semantics).Value);
            known = LlvmKnownFPClass.Sqrt(source, mode); known.KnownNot(~demanded);
            if (known.GetKnownFPClasses() == C.Zero)
            {
                var zero = LlvmConstantFP.Get(call.Type, LlvmAPFloat.GetZero(semantics));
                if ((flags & LlvmFastMathFlags.NoSignedZeros) != 0) return zero;
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                Builder.SetInsertPoint(call);
                return Builder.CreateCopySign(zero, call.GetArgument(0), flags: flags);
            }
            return DemandedFPResult(call, flags, demanded, known, source);
        }
        private LlvmValue? DemandedFPIntegral(LlvmCallInstruction call, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth,
            LlvmDenormalModeInfo mode, LlvmIntrinsicKind kind)
        {
            C sourceDemand = demanded;
            if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN;
            if ((demanded & C.NegZero) != 0) sourceDemand |= C.NegSubnormal | C.NegNormal;
            if ((demanded & C.PosZero) != 0)
            {
                sourceDemand |= C.PosSubnormal | C.PosNormal;
                if (FPInputMayBePositiveZero(mode)) sourceDemand |= C.NegSubnormal;
            }
            if ((demanded & C.NegNormal) != 0) sourceDemand |= C.NegSubnormal;
            if ((demanded & C.PosNormal) != 0) sourceDemand |= C.PosSubnormal;
            if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1)) return call;
            var value = call.GetArgument(0); var type = call.Type; var semantics = type.GetScalarType().GetFltSemantics();
            if (source.IsKnownAlways(C.Infinity | C.NaN | C.Zero)) return value;
            bool nearestOrTrunc = kind != LlvmIntrinsicKind.Ceil && kind != LlvmIntrinsicKind.Floor;
            if ((kind == LlvmIntrinsicKind.Floor || nearestOrTrunc) && source.IsKnownAlways(C.PosZero | C.PosSubnormal)) return LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(semantics));
            if ((kind == LlvmIntrinsicKind.Ceil || nearestOrTrunc) && source.IsKnownAlways(C.NegZero | C.NegSubnormal) && !FPInputMayBePositiveZero(mode)) return LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(semantics, true));
            if (kind == LlvmIntrinsicKind.Floor && source.IsKnownAlways(C.NegSubnormal) && !mode.InputsMayBeZero) return LlvmConstantFP.Get(type, -1);
            if (kind == LlvmIntrinsicKind.Ceil && source.IsKnownAlways(C.PosSubnormal) && !mode.InputsMayBeZero) return LlvmConstantFP.Get(type, 1);
            known = kind == LlvmIntrinsicKind.Trunc ? LlvmKnownFPClass.Trunc(source, mode) :
                kind == LlvmIntrinsicKind.Floor ? LlvmKnownFPClass.Floor(source, false, mode) :
                kind == LlvmIntrinsicKind.Ceil ? LlvmKnownFPClass.Ceil(source, false, mode) : LlvmKnownFPClass.RoundToIntegral(source, false, mode);
            known.KnownNot(~demanded);
            if (FPClassConstant(type, known.GetKnownFPClasses(), true) is { } constant) return constant;
            if (nearestOrTrunc && source.IsKnownAlways(C.Zero | C.Subnormal) &&
                (!FPInputMayBePositiveZero(mode) || source.IsKnownNever(C.NegSubnormal)))
            {
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                Builder.SetInsertPoint(call);
                return Builder.CreateCopySign(LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(semantics)), value);
            }
            var inferred = InferFPValueFlags(flags, known.GetKnownFPClasses(), source);
            if (inferred == flags) return null;
            call.DropUBImplyingAttrsAndMetadata(); call.FastMathFlags = inferred; return call;
        }
    }
}
