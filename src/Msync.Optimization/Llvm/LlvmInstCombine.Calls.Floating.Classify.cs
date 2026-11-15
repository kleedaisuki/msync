// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp1062–1202.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
using P = Msync.Core.Llvm.LlvmCmpPredicate;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldIntrinsicIsFPClass(LlvmCallInstruction call)
        {
            LlvmValue source = call.GetArgument(0), maskOperand = call.GetArgument(1);
            C mask = (C)((LlvmConstantInt)maskOperand).GetZExtValue();
            bool unordered = (mask & C.NaN) == C.NaN, ordered = (mask & C.NaN) == C.None;
            C orderedMask = mask & ~C.NaN, inverseMask = C.All & ~orderedMask & ~C.NaN;
            if (FPCallsFNeg(source) is { } negative)
            {
                Count("calls.floating.classify_negation");
                return new LlvmCallInstruction(call.FunctionType, call.CalledOperand,
                    [negative, LlvmConstantInt.Get((LlvmIntegerType)maskOperand.Type, (ushort)NegateFPDemand(mask))]);
            }
            if (CallsIntrinsic(source, LlvmIntrinsicKind.FAbs, out var abs))
            {
                Count("calls.floating.classify_absolute");
                return new LlvmCallInstruction(call.FunctionType, call.CalledOperand,
                    [abs.GetArgument(0), LlvmConstantInt.Get((LlvmIntegerType)maskOperand.Type, (ushort)LlvmKnownFPClasses.InverseFAbs(mask))]);
            }
            if (!F.StrictFP && (ordered || unordered))
            {
                if (orderedMask == C.Infinity || inverseMask == C.Infinity)
                {
                    P predicate = inverseMask == C.Infinity ? unordered ? P.FCmpUne : P.FCmpOne : unordered ? P.FCmpUeq : P.FCmpOeq;
                    LlvmValue magnitude = FPCallsIntrinsic(LlvmIntrinsicKind.FAbs, [source], LlvmFastMathFlags.None);
                    return FPCallsReplace(call, Builder.CreateFCmp(predicate, magnitude, LlvmConstantFP.Get(source.Type,
                        LlvmAPFloat.GetInf(source.Type.GetScalarType().GetFltSemantics()))), "calls.floating.classify_absolute_infinity");
                }
                if (orderedMask == C.PosInfinity || orderedMask == C.NegInfinity || inverseMask == C.PosInfinity || inverseMask == C.NegInfinity)
                {
                    bool inverse = inverseMask == C.PosInfinity || inverseMask == C.NegInfinity;
                    C tested = inverse ? inverseMask : orderedMask;
                    P predicate = inverse ? unordered ? P.FCmpUne : P.FCmpOne : unordered ? P.FCmpUeq : P.FCmpOeq;
                    LlvmValue infinity = LlvmConstantFP.Get(source.Type, LlvmAPFloat.GetInf(source.Type.GetScalarType().GetFltSemantics(), tested == C.NegInfinity));
                    return FPCallsReplace(call, Builder.CreateFCmp(predicate, source, infinity), "calls.floating.classify_signed_infinity");
                }
            }
            if (!F.StrictFP && (mask == C.NaN || mask == (C.All & ~C.NaN)))
                return FPCallsReplace(call, Builder.CreateFCmp(mask == C.NaN ? P.FCmpUno : P.FCmpOrd, source,
                    LlvmConstant.GetNullValue(source.Type)), "calls.floating.classify_nan");
            if (!F.StrictFP && (ordered || unordered))
            {
                P predicate = FPClassTestIsFCmp0(orderedMask, F, source.Type);
                if (predicate != P.BadFCmpPredicate)
                    return FPCallsReplace(call, Builder.CreateFCmp(unordered ? (P)((byte)predicate | 8) : predicate,
                        source, LlvmConstant.GetNullValue(source.Type)), "calls.floating.classify_zero_compare");
            }
            LlvmKnownFPClass known = FPCallsKnown(source, mask, call);
            if (known.IsKnownAlways(mask))
                return FPCallsReplace(call, LlvmConstantInt.GetBool(call.Type, true), "calls.floating.classify_known_true");
            if ((mask & known.GetKnownFPClasses()) != mask)
            {
                ReplaceOperand(call, 1, LlvmConstantInt.Get((LlvmIntegerType)maskOperand.Type, (ushort)(mask & known.GetKnownFPClasses())));
                Count("calls.floating.classify_impossible_bits_removed");
                return call;
            }
            return null;
        }
    }
}
