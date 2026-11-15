// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// visitCallInst (2036–4771), represented call dispatch and common postlude.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitCallInst(LlvmCallInstruction call)
        {
            Count("calls.visited");
            if (!call.UseEmpty && LlvmInstructionSimplify.SimplifyCall(call, call.CalledOperand, call.Arguments.ToArray(), Query(call)) is { } simplified)
                return ReplaceInstUsesWith(call, simplified);
            // All C library functions are disabled for this target runtime ABI.
            // Allocation/free attributes, when represented, are queried by their source utilities, never by user symbol spelling.
            if (F.DoesNotThrow && !call.DoesNotThrow) { call.NoUnwind = true; Count("calls.nounwind_from_caller"); return call; }
            if (call.CalledFunction?.IntrinsicDescriptor is not { } descriptor) return VisitCallBase(call);
            LlvmIntrinsicKind kind = descriptor.Kind;
            if (descriptor.IsCommutative)
            {
                if (MatchSymmetricPair(call.GetArgument(0), call.GetArgument(1)) is { } pair)
                {
                    ReplaceOperand(call, 0, pair.First); ReplaceOperand(call, 1, pair.Second);
                    call.DropPoisonGeneratingAnnotations(); call.DropUBImplyingAttrsAndMetadata();
                    Count("calls.symmetric_pair"); return call;
                }
                if (call.GetArgument(0) is LlvmConstant && call.GetArgument(1) is not LlvmConstant)
                {
                    LlvmValue first = call.GetArgument(0); call.SetArgument(0, call.GetArgument(1)); call.SetArgument(1, first);
                    LlvmParameterAttributes attributes = call.GetParamAttributes(0);
                    call.SetParamAttributes(0, call.GetParamAttributes(1)); call.SetParamAttributes(1, attributes);
                    Count("calls.constant_commuted"); return call;
                }
            }
            if (kind == LlvmIntrinsicKind.Assume) return VisitAssume(call);
            LlvmInstruction? result;
            if (LlvmIntrinsic.IsMinMax(kind)) result = FoldIntegerMinMax(call, kind);
            else if (kind == LlvmIntrinsicKind.Abs) result = FoldAbs(call);
            else if (kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp) result = FoldCompareIntrinsic(call, kind);
            else if (kind == LlvmIntrinsicKind.CTTZ || kind == LlvmIntrinsicKind.CTLZ) result = FoldCttzCtlz(call, kind);
            else if (kind == LlvmIntrinsicKind.CTPop) result = FoldCtpop(call);
            else if (kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr) result = FoldFunnelShift(call, kind);
            else if (kind == LlvmIntrinsicKind.BSwap || kind == LlvmIntrinsicKind.BitReverse) result = FoldBitOrder(call, kind);
            else if (kind == LlvmIntrinsicKind.PtrMask) result = FoldPtrMask(call);
            else if (kind == LlvmIntrinsicKind.UAddWithOverflow || kind == LlvmIntrinsicKind.SAddWithOverflow
                || kind == LlvmIntrinsicKind.USubWithOverflow || kind == LlvmIntrinsicKind.SSubWithOverflow
                || kind == LlvmIntrinsicKind.UMulWithOverflow || kind == LlvmIntrinsicKind.SMulWithOverflow) result = FoldOverflowIntrinsic(call, kind);
            else if (kind == LlvmIntrinsicKind.UAddSat || kind == LlvmIntrinsicKind.SAddSat
                || kind == LlvmIntrinsicKind.USubSat || kind == LlvmIntrinsicKind.SSubSat) result = FoldSaturatingIntrinsic(call, kind);
            else if (kind == LlvmIntrinsicKind.SMulH && call.Type.GetScalarSizeInBits() > 1 && CallsIsOne(call.GetArgument(1)))
                result = ReplaceInstUsesWith(call, Builder.CreateBinary(LlvmOpcode.AShr, call.GetArgument(0), CallsInt(call.Type, call.Type.GetScalarSizeInBits() - 1)));
            else result = VisitFloatingCallIntrinsic(call, kind);
            if (result is not null) return result;
            return VisitIntrinsicPostlude(call, kind);
        }
        private LlvmInstruction? VisitIntrinsicPostlude(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (LlvmValueTracking.IsSafeToSpeculativelyExecuteWithVariableReplaced(call, metrics: Metrics))
            {
                foreach (LlvmValue operand in call.Arguments)
                {
                    if (operand is LlvmSelectInstruction select && FoldOpIntoSelect(call, select, false, false) is { } folded) return folded;
                    if (operand is LlvmPhiInstruction phi && FoldOpIntoPhi(call, phi) is { } merged) return merged;
                }
            }
            if (FoldIdempotentBinaryIntrinsicRecurrence(call, kind) is { } invariant) return ReplaceInstUsesWith(call, invariant);
            return VisitCallBase(call);
        }
    }
}
