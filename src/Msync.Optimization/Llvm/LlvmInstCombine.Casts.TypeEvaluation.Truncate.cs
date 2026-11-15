// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 546–707.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed partial class TypeEvaluationHelper
        {
            private bool CanEvaluateTruncatedPred(LlvmValue value, LlvmType type, Work work, LlvmInstruction context)
            {
                uint originalWidth = value.Type.GetScalarSizeInBits(), width = type.GetScalarSizeInBits();
                switch (value)
                {
                    case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor:
                        return Both(binary, type, work, context);
                    case LlvmBinaryOperator binary when binary.Opcode is LlvmOpcode.UDiv or LlvmOpcode.URem:
                        var mask = LlvmAPInt.GetBitsSetFrom(originalWidth, width);
                        // Source keeps the div/rem's own context: later facts may otherwise introduce a trap.
                        return work.MaskedValueIsZero(binary.Left, mask, binary) && work.MaskedValueIsZero(binary.Right, mask, binary) && Both(binary, type, work, context);
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } binary:
                        var shlKnown = LlvmKnownBitsAnalysis.Compute(binary.Right, metrics: work.Metrics);
                        return shlKnown.GetMaxValue().Ult(new(shlKnown.BitWidth, width)) && Both(binary, type, work, context);
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.LShr } binary:
                        var maximum = work.ComputeKnownBits(binary.Right, context).GetMaxValue();
                        if (!maximum.Ult(new(maximum.BitWidth, width))) return false;
                        if (value.Users.Last() is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncate &&
                            (maximum + new LlvmAPInt(maximum.BitWidth, truncate.Type.GetScalarSizeInBits())).Ule(new(maximum.BitWidth, width)))
                            return Both(binary, type, work, context);
                        return work.MaskedValueIsZero(binary.Left, LlvmAPInt.GetBitsSetFrom(originalWidth, width), context) && Both(binary, type, work, context);
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.AShr } binary:
                        var ashrKnown = LlvmKnownBitsAnalysis.Compute(binary.Right, metrics: work.Metrics);
                        return ashrKnown.GetMaxValue().Ult(new(ashrKnown.BitWidth, width)) &&
                            originalWidth - width < work.ComputeNumSignBits(binary.Left, context) && Both(binary, type, work, context);
                    case LlvmCastInstruction { Opcode: LlvmOpcode.Trunc or LlvmOpcode.ZExt or LlvmOpcode.SExt }: return true;
                    case LlvmSelectInstruction select:
                        return CanEvaluateTruncatedImpl(select.TrueValue, type, work, context) && CanEvaluateTruncatedImpl(select.FalseValue, type, work, context);
                    case LlvmPhiInstruction phi:
                        for (int index = 0; index < phi.IncomingCount; ++index)
                            if (!CanEvaluateTruncatedImpl(phi.GetIncomingValue(index), type, work, context)) return false;
                        return true;
                    case LlvmCastInstruction { Opcode: LlvmOpcode.FPToUI or LlvmOpcode.FPToSI } cast:
                        return width >= LlvmAPFloat.SemanticsIntSizeInBits(cast.Operand.Type.GetScalarType().GetFltSemantics(), cast.Opcode == LlvmOpcode.FPToSI);
                    case LlvmCallInstruction call when call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Abs:
                        return work.ComputeMaxSignificantBits(call.GetArgument(0), context) <= width && CanEvaluateTruncatedImpl(call.GetArgument(0), type, work, context);
                    case LlvmCallInstruction call when call.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsMinMax(descriptor.Kind):
                        var first = call.GetArgument(0);
                        var second = call.GetArgument(1);
                        if (descriptor.Kind == LlvmIntrinsicKind.SMin || descriptor.Kind == LlvmIntrinsicKind.SMax)
                        {
                            if (work.ComputeMaxSignificantBits(first, context) > width || work.ComputeMaxSignificantBits(second, context) > width) return false;
                        }
                        else
                        {
                            var high = LlvmAPInt.GetBitsSetFrom(originalWidth, width);
                            if (!work.MaskedValueIsZero(first, high, context) || !work.MaskedValueIsZero(second, high, context)) return false;
                        }
                        return CanEvaluateTruncatedImpl(first, type, work, context) && CanEvaluateTruncatedImpl(second, type, work, context);
                    default: return false;
                }
            }
            private bool Both(LlvmBinaryOperator binary, LlvmType type, Work work, LlvmInstruction context) =>
                CanEvaluateTruncatedImpl(binary.Left, type, work, context) && CanEvaluateTruncatedImpl(binary.Right, type, work, context);
        }
    }
}
