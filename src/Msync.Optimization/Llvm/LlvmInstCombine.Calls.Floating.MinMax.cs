// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp3063–3232.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldFPCallsMinMax(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            LlvmValue first = call.GetArgument(0), second = call.GetArgument(1);
            LlvmValue? negativeFirst = FPCallsFNeg(first), negativeSecond = FPCallsFNeg(second);
            if (negativeFirst is not null && negativeSecond is not null && (first.HasOneUse || second.HasOneUse))
            {
                LlvmValue inverted = FPCallsIntrinsic(FPCallsInverseMinMax(kind), [negativeFirst, negativeSecond], call.FastMathFlags);
                Count("calls.floating.minmax_negated");
                return new LlvmUnaryOperator(inverted) { FastMathFlags = call.FastMathFlags };
            }
            if (CallsIntrinsic(first, kind, out var inner) && FPCallsFloat(second, out var outerConstant))
            {
                LlvmValue? source = null;
                LlvmAPFloat innerConstant = default;
                if (FPCallsFloat(inner.GetArgument(1), out innerConstant)) source = inner.GetArgument(0);
                else if (FPCallsFloat(inner.GetArgument(0), out innerConstant)) source = inner.GetArgument(1);
                if (source is not null)
                {
                    LlvmAPFloat combined = FPCallsMinMaxConstant(kind, outerConstant, innerConstant);
                    return FPCallsReplace(call, FPCallsIntrinsic(kind, [source, LlvmConstantFP.Get(first.Type, combined)],
                        call.FastMathFlags & ((LlvmCallInstruction)inner).FastMathFlags), "calls.floating.minmax_constants_merged");
                }
            }
            if (first is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } firstExt &&
                second is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } secondExt &&
                (first.HasOneUse || second.HasOneUse) && ReferenceEquals(firstExt.Operand.Type, secondExt.Operand.Type))
            {
                LlvmValue narrowed = FPCallsIntrinsic(kind, [firstExt.Operand, secondExt.Operand], call.FastMathFlags, call.Name);
                Count("calls.floating.minmax_extensions_narrowed");
                return new LlvmCastInstruction(LlvmOpcode.FPExt, narrowed, call.Type);
            }
            if (first is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt, HasOneUse: true } extension &&
                second is LlvmConstant and not LlvmConstantExpr &&
                GetLosslessInvCast((LlvmConstant)second, extension.Operand.Type, LlvmOpcode.FPExt, DL) is { } narrowConstant)
            {
                LlvmValue narrowed = FPCallsIntrinsic(kind, [extension.Operand, narrowConstant], call.FastMathFlags, call.Name);
                Count("calls.floating.minmax_constant_narrowed");
                return new LlvmCastInstruction(LlvmOpcode.FPExt, narrowed, call.Type);
            }
            if (first is LlvmCastInstruction firstCast && firstCast.Opcode is LlvmOpcode.SIToFP or LlvmOpcode.UIToFP &&
                second is LlvmCastInstruction secondCast && firstCast.Opcode == secondCast.Opcode &&
                (first.HasOneUse || second.HasOneUse) && ReferenceEquals(firstCast.Operand.Type, secondCast.Operand.Type))
            {
                bool signed = firstCast.Opcode == LlvmOpcode.SIToFP, maximum = FPCallsIsMaximum(kind);
                LlvmIntrinsicKind integerKind = maximum ? signed ? LlvmIntrinsicKind.SMax : LlvmIntrinsicKind.UMax
                    : signed ? LlvmIntrinsicKind.SMin : LlvmIntrinsicKind.UMin;
                LlvmValue minimum = Builder.CreateBinaryIntrinsic(integerKind, firstCast.Operand, secondCast.Operand);
                Count("calls.floating.minmax_integer_casts");
                return new LlvmCastInstruction(firstCast.Opcode, minimum, call.Type)
                    { IsNonNegative = !signed && firstCast.IsNonNegative && secondCast.IsNonNegative };
            }
            LlvmValue? absolute = FPCallsNegatedPair(first, second, kind) ?? FPCallsNegatedPair(second, first, kind);
            if (absolute is null) return null;
            LlvmValue result = FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [absolute], call.FastMathFlags);
            if (!FPCallsIsMaximum(kind)) result = Builder.CreateFNeg(result, fastMathFlags: call.FastMathFlags);
            return FPCallsReplace(call, result, "calls.floating.minmax_absolute");
        }
        private static LlvmValue? FPCallsNegatedPair(LlvmValue first, LlvmValue second, LlvmIntrinsicKind kind) =>
            FPCallsFNeg(first) is { } source && ReferenceEquals(source, second) && (first.HasOneUse || FPCallsIsMaximum(kind)) ? source : null;
        private static bool FPCallsIsMaximum(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.MaxNum ||
            kind == LlvmIntrinsicKind.Maximum || kind == LlvmIntrinsicKind.MaximumNum;
        private static LlvmIntrinsicKind FPCallsInverseMinMax(LlvmIntrinsicKind kind) => kind.Name switch
        {
            "llvm.minnum" => LlvmIntrinsicKind.MaxNum, "llvm.maxnum" => LlvmIntrinsicKind.MinNum,
            "llvm.minimumnum" => LlvmIntrinsicKind.MaximumNum, "llvm.maximumnum" => LlvmIntrinsicKind.MinimumNum,
            "llvm.minimum" => LlvmIntrinsicKind.Maximum, "llvm.maximum" => LlvmIntrinsicKind.Minimum,
            _ => throw new InvalidOperationException("Expected a floating min/max intrinsic.")
        };
        private static LlvmAPFloat FPCallsMinMaxConstant(LlvmIntrinsicKind kind, LlvmAPFloat first, LlvmAPFloat second) => kind.Name switch
        {
            "llvm.minnum" => LlvmAPFloat.MinNum(first, second), "llvm.maxnum" => LlvmAPFloat.MaxNum(first, second),
            "llvm.minimumnum" => LlvmAPFloat.MinimumNum(first, second), "llvm.maximumnum" => LlvmAPFloat.MaximumNum(first, second),
            "llvm.minimum" => LlvmAPFloat.Minimum(first, second), "llvm.maximum" => LlvmAPFloat.Maximum(first, second),
            _ => throw new InvalidOperationException("Expected a floating min/max intrinsic.")
        };
    }
}
