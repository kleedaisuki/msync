// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp3280–3455.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldFPCallsFma(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            LlvmValue first = call.GetArgument(0), second = call.GetArgument(1), addend = call.GetArgument(2);
            if (kind == LlvmIntrinsicKind.FMulAdd && LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.FMul, first, second,
                call.FastMathFlags, Query(call)) is { } product)
            { Count("calls.floating.fmuladd_product_simplified"); return FPCallsBinary(LlvmOpcode.FAdd, product, addend, call.FastMathFlags); }
            if (FPCallsFNeg(first) is { } x && FPCallsFNeg(second) is { } y)
                return FPCallsReplace(call, FPCallsIntrinsic(kind, [x, y, addend], call.FastMathFlags), "calls.floating.fma_signs_cancelled");
            if (CallsIntrinsic(first, LlvmIntrinsicKind.FAbs, out var absFirst) &&
                CallsIntrinsic(second, LlvmIntrinsicKind.FAbs, out var absSecond) && ReferenceEquals(absFirst.GetArgument(0), absSecond.GetArgument(0)))
                return FPCallsReplace(call, FPCallsIntrinsic(kind, [absFirst.GetArgument(0), absFirst.GetArgument(0), addend], call.FastMathFlags), "calls.floating.fma_square_absolute");
            if (LlvmInstructionSimplify.SimplifyFMAFMul(first, second, call.FastMathFlags, Query(call)) is { } exactProduct)
            { Count("calls.floating.fma_product_simplified"); return FPCallsBinary(LlvmOpcode.FAdd, exactProduct, addend, call.FastMathFlags); }
            if (FPCallsFloat(addend, out var zero, true) && zero.IsZero && (zero.IsNegative || (call.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0))
            { Count("calls.floating.fma_zero_addend"); return FPCallsBinary(LlvmOpcode.FMul, first, second, call.FastMathFlags); }
            if (FPCallsSpecific(second, -1))
            { Count("calls.floating.fma_minus_one"); return FPCallsBinary(LlvmOpcode.FSub, addend, first, call.FastMathFlags); }
            return null;
        }
        private LlvmInstruction? FoldFPCallsCopySign(LlvmCallInstruction call)
        {
            LlvmValue magnitude = call.GetArgument(0), sign = call.GetArgument(1);
            if (FPCallsKnown(sign, C.All, call).SignBit is { } negative)
            {
                LlvmValue abs = FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [magnitude], call.FastMathFlags);
                if (negative) abs = Builder.CreateFNeg(abs, fastMathFlags: call.FastMathFlags);
                return FPCallsReplace(call, abs, "calls.floating.copysign_known_sign");
            }
            if (CallsIntrinsic(sign, LlvmIntrinsicKind.CopySign, out var innerSign))
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.CopySign, [magnitude, innerSign.GetArgument(1)],
                    call.FastMathFlags & ((LlvmCallInstruction)innerSign).FastMathFlags), "calls.floating.copysign_nested");
            if (FPCallsFloat(magnitude, out var constant) && constant.IsNegative)
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.CopySign,
                    [LlvmConstantFP.Get(magnitude.Type, constant.ClearSign()), sign], call.FastMathFlags), "calls.floating.copysign_constant_magnitude");
            LlvmValue? stripped = FPCallsFNeg(magnitude);
            if (stripped is null && CallsIntrinsic(magnitude, LlvmIntrinsicKind.FAbs, out var absMagnitude)) stripped = absMagnitude.GetArgument(0);
            if (stripped is not null)
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.CopySign, [stripped, sign], call.FastMathFlags), "calls.floating.copysign_magnitude_sign_removed");
            if (CallsIntrinsic(magnitude, LlvmIntrinsicKind.Floor, out var floor) && CallsIntrinsic(floor.GetArgument(0), LlvmIntrinsicKind.FAbs, out var floorAbs) &&
                ReferenceEquals(floorAbs.GetArgument(0), sign))
            {
                LlvmValue trunc = FPCallsIntrinsic(LlvmIntrinsicKind.Trunc, [sign], call.FastMathFlags);
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.CopySign, [trunc, sign], call.FastMathFlags), "calls.floating.copysign_floor_absolute");
            }
            if (sign is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true } cast && cast.Operand.HasOneUse &&
                cast.Operand.Type.IsIntOrIntVectorTy() && cast.Type.GetScalarSizeInBits() == cast.Operand.Type.GetScalarSizeInBits() &&
                cast.Type.IsVectorTy() == cast.Operand.Type.IsVectorTy() &&
                (cast.Type is not LlvmVectorType vector || vector.ElementCount == ((LlvmVectorType)cast.Operand.Type).ElementCount) &&
                LlvmAPFloat.HasSignBitInMSB(sign.Type.GetScalarType().GetFltSemantics()))
            {
                LlvmKnownBits known = new(sign.Type.GetScalarSizeInBits());
                if (SimplifyDemandedBits(cast, 0, LlvmAPInt.GetSignedMinValue(known.BitWidth), ref known, SQ))
                { Count("calls.floating.copysign_demanded_sign"); return call; }
            }
            return null;
        }
        private LlvmInstruction? FoldFPCallsFabs(LlvmCallInstruction call)
        {
            LlvmValue source = call.GetArgument(0);
            if (FPCallsFNeg(source) is { } unnegated)
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [unnegated], call.FastMathFlags), "calls.floating.fabs_negation_removed");
            if (source is LlvmSelectInstruction select)
            {
                bool trueConstant = select.TrueValue is LlvmConstant, falseConstant = select.FalseValue is LlvmConstant;
                if (source.HasOneUse ? trueConstant || falseConstant : trueConstant && falseConstant)
                {
                    var first = Builder.Insert(new LlvmCallInstruction(call.FunctionType, call.CalledOperand, [select.TrueValue]));
                    var second = Builder.Insert(new LlvmCallInstruction(call.FunctionType, call.CalledOperand, [select.FalseValue]));
                    Count("calls.floating.fabs_select_distributed");
                    return new LlvmSelectInstruction(select.Condition, first, second)
                        { FastMathFlags = (call.FastMathFlags | select.FastMathFlags) & ~LlvmFastMathFlags.NoSignedZeros };
                }
                if (FPCallsFNeg(select.TrueValue) == select.FalseValue)
                    return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [select.FalseValue], call.FastMathFlags), "calls.floating.fabs_select_opposites");
                if (FPCallsFNeg(select.FalseValue) == select.TrueValue)
                    return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [select.TrueValue], call.FastMathFlags), "calls.floating.fabs_select_opposites");
            }
            if (CallsIntrinsic(source, LlvmIntrinsicKind.CopySign, out var copy))
                return FPCallsReplace(call, FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [copy.GetArgument(0)], call.FastMathFlags), "calls.floating.fabs_copysign_removed");
            return null;
        }
    }
}
