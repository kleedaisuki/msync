// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
//1920–1976 sincos, 3456–3612 trigonometric and ldexp cases.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldFPCallsTrig(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            LlvmValue source = call.GetArgument(0);
            if (kind == LlvmIntrinsicKind.Cos || kind == LlvmIntrinsicKind.Cosh)
            {
                LlvmValue? unsigned = FPCallsFNeg(source);
                if (unsigned is null && CallsIntrinsic(source, LlvmIntrinsicKind.FAbs, out var absolute)) unsigned = absolute.GetArgument(0);
                if (unsigned is null && CallsIntrinsic(source, LlvmIntrinsicKind.CopySign, out var copy)) unsigned = copy.GetArgument(0);
                if (unsigned is not null)
                    return FPCallsReplace(call, FPCallsIntrinsic(kind, [unsigned], call.FastMathFlags), "calls.floating.even_function_sign_removed");
            }
            else if (source.HasOneUse && FPCallsFNeg(source) is { } unnegated)
            {
                LlvmDenormalModeInfo mode = F.GetDenormalMode(call.Type.GetScalarType().GetFltSemantics());
                if (!mode.InputsMayBePositiveZero && !mode.OutputsMayBePositiveZero)
                {
                    LlvmValue function = FPCallsIntrinsic(kind, [unnegated], call.FastMathFlags);
                    Count("calls.floating.odd_function_negation");
                    return new LlvmUnaryOperator(function) { FastMathFlags = call.FastMathFlags };
                }
            }
            if ((kind == LlvmIntrinsicKind.Sin || kind == LlvmIntrinsicKind.Cos) && FoldSinAndCosToSinCos(call, kind) is { } pair)
                return FPCallsReplace(call, pair, "calls.floating.sincos_combined");
            return null;
        }
        private LlvmValue? FoldSinAndCosToSinCos(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            LlvmValue argument = call.GetArgument(0);
            if (argument is LlvmConstant) return null;
            bool sine = kind == LlvmIntrinsicKind.Sin;
            LlvmIntrinsicKind otherKind = sine ? LlvmIntrinsicKind.Cos : LlvmIntrinsicKind.Sin;
            LlvmCallInstruction? matching = null;
            foreach (var user in argument.Users)
                if (user is LlvmCallInstruction candidate && candidate != call && !candidate.UseEmpty &&
                    candidate.CalledFunction?.IntrinsicDescriptor?.Kind == otherKind)
                { matching = candidate; break; }
            if (matching is null) return null;
            using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            if (argument is LlvmInstruction definition)
            {
                LlvmInstruction? next = definition.GetInsertionPointAfterDef();
                if (next is null) return null;
                Builder.SetInsertPoint(next);
            }
            else Builder.SetInsertPoint(F.BasicBlocks[0], F.BasicBlocks[0].FirstInstruction);
            var declaration = LlvmIntrinsic.GetDeclaration(F.Parent!, LlvmIntrinsicKind.SinCos, argument.Type, Metrics);
            var combined = Builder.Insert(new LlvmCallInstruction(declaration.FunctionType, declaration, [argument])
                { FastMathFlags = call.FastMathFlags & matching.FastMathFlags }, "sincos");
            LlvmValue sin = Builder.CreateExtractValue(combined, 0, "sin");
            LlvmValue cos = Builder.CreateExtractValue(combined, 1, "cos");
            ReplaceInstUsesWith(matching, sine ? cos : sin);
            EraseInstFromFunction(matching);
            return sine ? sin : cos;
        }
        private LlvmInstruction? FoldFPCallsLdexp(LlvmCallInstruction call)
        {
            LlvmValue source = call.GetArgument(0), exponent = call.GetArgument(1);
            if (FPCallsInteger(exponent, out var amount) && amount.GetActiveBits() <= 64)
            {
                var semantics = source.Type.GetScalarType().GetFltSemantics();
                var scaled = LlvmAPFloat.GetOne(semantics).Scalbn(unchecked((int)amount.GetZExtValue()), LlvmRoundingMode.NearestTiesToEven).Value;
                if (!scaled.IsZero && !scaled.IsInfinity)
                { Count("calls.floating.ldexp_constant_multiply"); return FPCallsBinary(LlvmOpcode.FMul, source, LlvmConstantFP.Get(source.Type, scaled), call.FastMathFlags); }
            }
            if (source.HasOneUse && CallsIntrinsic(source, LlvmIntrinsicKind.Ldexp, out var inner) &&
                ReferenceEquals(exponent.Type, inner.GetArgument(1).Type))
            {
                LlvmFastMathFlags flags = call.FastMathFlags | ((LlvmCallInstruction)inner).FastMathFlags;
                bool reassociate = (call.FastMathFlags & ((LlvmCallInstruction)inner).FastMathFlags & LlvmFastMathFlags.AllowReassoc) != 0;
                if (FPCallsLdexpSaturatingAddIsSafe(call.Type, exponent.Type) &&
                    (reassociate || CallsKnownSign(inner.GetArgument(1), call) is { } innerSign &&
                        CallsKnownSign(exponent, call) is { } outerSign && outerSign == innerSign))
                {
                    LlvmValue sum = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SAddSat, inner.GetArgument(1), exponent);
                    return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.Ldexp, [inner.GetArgument(0), sum], flags), "calls.floating.ldexp_nested");
                }
            }
            if (exponent is LlvmCastInstruction cast && cast.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt && cast.Operand.Type.GetScalarSizeInBits() == 1)
            {
                LlvmValue scale = Builder.CreateSelect(cast.Operand, FPCallsConstant(call.Type, cast.Opcode == LlvmOpcode.ZExt ? 2 : 0.5), FPCallsConstant(call.Type, 1));
                Count("calls.floating.ldexp_boolean_exponent");
                return FPCallsBinary(LlvmOpcode.FMul, source, scale, call.FastMathFlags);
            }
            if (exponent is LlvmSelectInstruction { HasOneUse: true } select)
            {
                LlvmValue result;
                if (select.FalseValue is LlvmConstant { IsNullValue: true })
                    result = Builder.CreateSelect(select.Condition, FPCallsIntrinsic(LlvmIntrinsicKind.Ldexp, [source, select.TrueValue], call.FastMathFlags), source);
                else if (select.TrueValue is LlvmConstant { IsNullValue: true })
                    result = Builder.CreateSelect(select.Condition, source, FPCallsIntrinsic(LlvmIntrinsicKind.Ldexp, [source, select.FalseValue], call.FastMathFlags));
                else return null;
                if (result is LlvmInstruction instruction) instruction.TakeName(call);
                return FPCallsReplace(call, result, "calls.floating.ldexp_select_zero");
            }
            return null;
        }
        private static bool FPCallsInteger(LlvmValue value, out LlvmAPInt bits)
        {
            var constant = value as LlvmConstant;
            if (constant is not LlvmConstantInt && constant?.Type.IsVectorTy() == true) constant = constant.GetSplatValue();
            if (constant is LlvmConstantInt integer) { bits = integer.Value; return true; }
            bits = default; return false;
        }
        private static bool FPCallsLdexpSaturatingAddIsSafe(LlvmType floatingType, LlvmType exponentType)
        {
            var semantics = floatingType.GetScalarType().GetFltSemantics();
            if (!LlvmAPFloat.SemanticsHasInf(semantics)) return false;
            uint bits = Math.Min(exponentType.GetScalarSizeInBits(), 32U);
            int maximum = unchecked((int)((1UL << (int)(bits - 1)) - 1));
            int minimum = unchecked((int)(0UL - (1UL << (int)(bits - 1))));
            var up = LlvmAPFloat.GetSmallest(semantics).Scalbn(maximum, LlvmRoundingMode.NearestTiesToEven).Value;
            var down = LlvmAPFloat.GetLargest(semantics).Scalbn(minimum, LlvmRoundingMode.NearestTiesToEven).Value;
            return up.IsInfinity && down.IsZero;
        }
    }
}
