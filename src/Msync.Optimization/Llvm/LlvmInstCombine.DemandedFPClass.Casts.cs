// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: demanded FPClass cast/frexp extract paths.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? DemandedFPCast(LlvmCastInstruction cast, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            var destination = cast.Type.GetScalarType().GetFltSemantics();
            var origin = cast.Operand.Type.GetScalarType().GetFltSemantics();
            C sourceDemand = demanded;
            if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN;
            if (cast.Opcode == LlvmOpcode.FPTrunc)
            {
                if ((demanded & C.NegZero) != 0) sourceDemand |= C.NegSubnormal | C.NegNormal;
                if ((demanded & C.PosZero) != 0) sourceDemand |= C.PosSubnormal | C.PosNormal;
                if ((demanded & C.NegSubnormal) != 0) sourceDemand |= C.NegNormal;
                if ((demanded & C.PosSubnormal) != 0) sourceDemand |= C.PosNormal;
                if (LlvmAPFloat.SemanticsMinExponent(destination) <= LlvmAPFloat.SemanticsMinExponent(origin))
                {
                    if ((demanded & C.NegNormal) != 0) sourceDemand |= C.NegSubnormal;
                    if ((demanded & C.PosNormal) != 0) sourceDemand |= C.PosSubnormal;
                }
                if ((demanded & C.PosInfinity) != 0) sourceDemand |= C.PosNormal;
                if ((demanded & C.NegInfinity) != 0) sourceDemand |= C.NegNormal;
            }
            else
            {
                if ((demanded & C.NegNormal) != 0) sourceDemand |= C.NegSubnormal;
                if ((demanded & C.PosNormal) != 0) sourceDemand |= C.PosSubnormal;
            }
            if (SimplifyDemandedFPClass(cast, 0, sourceDemand, out var source, query, depth + 1)) return cast;
            known = LlvmKnownFPClass.FPConvert(source, destination, origin, F.GetDenormalMode(destination), F.GetDenormalMode(origin));
            known.KnownNot(~demanded);
            return DemandedFPResult(cast, flags, demanded, known, source);
        }
        private bool DemandedFPFrexpExtract(LlvmExtractValueInstruction extract, C demanded,
            ref LlvmKnownFPClass known, LlvmInstructionSimplify.BinaryQuery query, uint depth, out LlvmValue? replacement)
        {
            replacement = null;
            if (extract.Indices.Count != 1 || extract.Indices[0] != 0 || !extract.AggregateOperand.HasOneUse ||
                extract.AggregateOperand is not LlvmCallInstruction call || call.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.Frexp) return false;
            C sourceDemand = demanded & C.NaN;
            if ((demanded & C.PosZero) != 0) sourceDemand |= C.PosZero | C.Subnormal;
            if ((demanded & C.NegZero) != 0) sourceDemand |= C.NegZero | C.NegSubnormal;
            if ((demanded & (C.NegNormal | C.NegSubnormal)) != 0) sourceDemand |= C.NegNormal | C.NegSubnormal;
            if ((demanded & (C.PosNormal | C.PosSubnormal)) != 0) sourceDemand |= C.PosNormal | C.PosSubnormal;
            sourceDemand |= demanded & C.Infinity;
            if (SimplifyDemandedFPClass(call, 0, sourceDemand, out var source, query, depth + 1))
            { replacement = extract; return true; }
            known = LlvmKnownFPClass.FrexpMantissa(source, F.GetDenormalMode(extract.Type.GetScalarType().GetFltSemantics()));
            known.SetKnownFPClasses(known.GetKnownFPClasses() & demanded);
            replacement = FPClassConstant(extract.Type, known.GetKnownFPClasses(), true);
            if (replacement is null && source.IsKnownAlways(C.Zero | C.Infinity | C.NaN)) replacement = call.GetArgument(0);
            return true;
        }
    }
}
