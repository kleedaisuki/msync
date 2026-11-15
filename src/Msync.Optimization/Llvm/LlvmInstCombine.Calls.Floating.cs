// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// represented floating cases in visitCallInst, preserving source order.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitFloatingCallIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (!LlvmIntrinsic.IsFloatingIntrinsic(kind)) return null;
            Count("calls.floating.visited");
            LlvmInstruction? result;
            if (kind == LlvmIntrinsicKind.PowI) result = FoldFPCallsPowI(call);
            else if (LlvmIntrinsic.IsFloatingMinMax(kind)) result = FoldFPCallsMinMax(call, kind);
            else if (kind == LlvmIntrinsicKind.FMulAdd || kind == LlvmIntrinsicKind.FMA) result = FoldFPCallsFma(call, kind);
            else if (kind == LlvmIntrinsicKind.CopySign) result = FoldFPCallsCopySign(call);
            else if (kind == LlvmIntrinsicKind.FAbs) result = FoldFPCallsFabs(call) ?? FoldFPCallsRoundNarrow(call, kind);
            else if (kind == LlvmIntrinsicKind.Ceil || kind == LlvmIntrinsicKind.Floor || kind == LlvmIntrinsicKind.Round ||
                kind == LlvmIntrinsicKind.RoundEven || kind == LlvmIntrinsicKind.NearbyInt || kind == LlvmIntrinsicKind.Rint || kind == LlvmIntrinsicKind.Trunc)
                result = FoldFPCallsRoundNarrow(call, kind);
            else if (kind == LlvmIntrinsicKind.Cos || kind == LlvmIntrinsicKind.Cosh || kind == LlvmIntrinsicKind.Sin ||
                kind == LlvmIntrinsicKind.Sinh || kind == LlvmIntrinsicKind.Tan || kind == LlvmIntrinsicKind.Tanh)
                result = FoldFPCallsTrig(call, kind);
            else if (kind == LlvmIntrinsicKind.Ldexp) result = FoldFPCallsLdexp(call);
            else if (kind == LlvmIntrinsicKind.IsFPClass) result = FoldIntrinsicIsFPClass(call);
            else if (kind == LlvmIntrinsicKind.Frexp) result = FoldFPCallsFrexp(call);
            else result = null;
            Count(result is null ? "calls.floating.unchanged" : "calls.floating.rewritten");
            return result;
        }

        private static LlvmValue? FPCallsFNeg(LlvmValue value)
        {
            if (value is LlvmUnaryOperator unary) return unary.Operand;
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } subtraction &&
                ((subtraction.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0
                    ? LlvmInstructionSimplify.FMAAnyZero(subtraction.Left)
                    : FPCallsFloat(subtraction.Left, out var zero, true) && zero.IsZero && zero.IsNegative)) return subtraction.Right;
            return null;
        }
        private static bool FPCallsFloat(LlvmValue value, out LlvmAPFloat number, bool allowPoison = false)
        {
            var constant = value as LlvmConstant;
            if (constant is not LlvmConstantFP && constant?.Type.IsVectorTy() == true) constant = constant.GetSplatValue(allowPoison);
            if (constant is LlvmConstantFP floating) { number = floating.Value; return true; }
            number = default; return false;
        }
        private static bool FPCallsSpecific(LlvmValue value, double expected) => FPCallsFloat(value, out var number, true) &&
            number.BitwiseIsEqual(new LlvmAPFloat(expected).Convert(number.Semantics).Value);
        private static LlvmConstantFP FPCallsConstant(LlvmType type, double number) => LlvmConstantFP.Get(type, number);
        private static LlvmBinaryOperator FPCallsBinary(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmFastMathFlags flags) =>
            new(opcode, left, right) { FastMathFlags = flags };
        private LlvmValue FPCallsIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments, LlvmFastMathFlags flags, string name = "")
        {
            LlvmFastMathFlags saved = Builder.FastMathFlags;
            Builder.FastMathFlags = flags;
            try { return Builder.CreateIntrinsic(kind, arguments, name); }
            finally { Builder.FastMathFlags = saved; }
        }
        private LlvmKnownFPClass FPCallsKnown(LlvmValue value, C interested, LlvmInstruction context) =>
            LlvmKnownFPClasses.Compute(value, interested, context, DT, Metrics, assumptions: AC,
                allowEphemerals: SQ.AllowEphemerals, conditions: DC, condContext: SQ.CondContext);
        private LlvmInstruction? FPCallsReplace(LlvmCallInstruction call, LlvmValue replacement, string outcome)
        { Count(outcome); return ReplaceInstUsesWith(call, replacement); }

        private LlvmInstruction? FoldFPCallsPowI(LlvmCallInstruction call)
        {
            LlvmValue source = call.GetArgument(0), exponent = call.GetArgument(1);
            if (exponent is LlvmConstantInt power)
            {
                if (power.IsMinusOne) { Count("calls.floating.powi_reciprocal"); return FPCallsBinary(LlvmOpcode.FDiv, FPCallsConstant(call.Type, 1), source, call.FastMathFlags); }
                if (power.EqualsInt(2)) { Count("calls.floating.powi_square"); return FPCallsBinary(LlvmOpcode.FMul, source, source, call.FastMathFlags); }
                if ((power.Value.GetWord(0) & 1) == 0)
                {
                    LlvmValue? unsigned = FPCallsFNeg(source);
                    if (unsigned is null && CallsIntrinsic(source, LlvmIntrinsicKind.FAbs, out var abs)) unsigned = abs.GetArgument(0);
                    if (unsigned is null && CallsIntrinsic(source, LlvmIntrinsicKind.CopySign, out var copy)) unsigned = copy.GetArgument(0);
                    if (unsigned is not null)
                    { Count("calls.floating.powi_even_sign"); return new LlvmCallInstruction(call.FunctionType, call.CalledOperand, [unsigned, exponent]); }
                }
            }
            if (source is LlvmConstantFP && FPCallsSpecific(source, 2) && (call.FastMathFlags & LlvmFastMathFlags.ApproxFunc) != 0)
            {
                if (source.Type is LlvmVectorType vector) exponent = Builder.CreateVectorSplat(vector.ElementCount, exponent);
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.Ldexp, [FPCallsConstant(source.Type, 1), exponent], call.FastMathFlags), "calls.floating.powi_to_ldexp");
            }
            return null;
        }

        private LlvmInstruction? FoldFPCallsRoundNarrow(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (call.GetArgument(0) is not LlvmCastInstruction { Opcode: LlvmOpcode.FPExt, HasOneUse: true } cast) return null;
            LlvmValue narrow = FPCallsIntrinsic(kind, [cast.Operand], call.FastMathFlags);
            Count("calls.floating.round_narrowed");
            return new LlvmCastInstruction(LlvmOpcode.FPExt, narrow, call.Type);
        }

        private LlvmInstruction? FoldFPCallsFrexp(LlvmCallInstruction call)
        {
            LlvmValue source = call.GetArgument(0);
            if (source is not LlvmExtractValueInstruction { Indices.Count: 1 } extract || extract.Indices[0] != 0 ||
                !CallsIntrinsic(extract.AggregateOperand, LlvmIntrinsicKind.Frexp, out _)) return null;
            LlvmValue result = Builder.CreateInsertValue(LlvmPoisonValue.Get(call.Type), source, 0);
            result = Builder.CreateInsertValue(result, LlvmConstant.GetNullValue(((LlvmStructType)call.Type).Elements[1]), 1);
            return FPCallsReplace(call, result, "calls.floating.frexp_normalized");
        }
    }
}
