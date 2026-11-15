// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineCompares.cpp foldFCmpFpTrunc and visitFCmpInst fpext constant path.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool FCmpDenormalsAreIEEE(LlvmType type) => F.GetDenormalMode(type.GetScalarType().GetFltSemantics()) == LlvmDenormalModeInfo.IEEE;
        private bool FCmpInputFlushesDenormals(LlvmType type) => F.GetDenormalMode(type.GetScalarType().GetFltSemantics()).InputsAreZero;
        private bool FCmpInputsMayBeZero(LlvmType type) => F.GetDenormalMode(type.GetScalarType().GetFltSemantics()).InputsMayBeZero;

        private static LlvmAPFloat FCmpOne(LlvmAPFloatSemantics semantics) => LlvmAPFloat.GetOne(semantics);
        private static bool FCmpIsSmallestNormalized(LlvmAPFloat value) => value.IsSmallestNormalized;
        private static LlvmAPFloat FCmpConvert(LlvmAPFloat value, LlvmAPFloatSemantics semantics) => value.Convert(semantics, LlvmRoundingMode.NearestTiesToEven).Value;

        private LlvmInstruction? FoldFCmpFpTrunc(LlvmFCmpInstruction comparison, LlvmCastInstruction truncation, LlvmConstant constant)
        {
            if (constant is not LlvmConstantFP floating) return null;
            Count("fcmp.truncation.queries");
            LlvmCmpPredicate predicate = comparison.Predicate;
            LlvmValue input = truncation.Operand;
            LlvmType destination = input.Type;
            LlvmAPFloat value = floating.Value;
            if (predicate is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno)
            {
                Debug.Assert(!value.IsNaN);
                Count("fcmp.rewrite.truncation_ordered");
                return NewFCmp(predicate, input, FCmpZero(destination), comparison);
            }
            bool down;
            if (predicate is LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpUlt) down = true;
            else if (predicate is LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUle) down = false;
            else return null;
            if (value.IsNaN || value.IsInfinity) return null;
            LlvmAPFloat next = value.Next(down).Value;
            LlvmAPFloatSemantics destinationSemantics = destination.GetScalarType().GetFltSemantics();
            LlvmAPFloat extended = FCmpConvert(value, destinationSemantics), extendedNext = FCmpConvert(next, destinationSemantics);
            if (next.IsInfinity)
            {
                LlvmAPFloat previous = value.Next(!down).Value;
                LlvmAPFloat bias = FCmpConvert(value.Subtract(previous).Value, destinationSemantics);
                extendedNext = extended.Add(bias).Value;
            }
            LlvmAPFloat midpoint = extended.Add(extendedNext).Value.Scalbn(-1, LlvmRoundingMode.NearestTiesToEven).Value;
            LlvmAPFloatSemantics sourceSemantics = constant.Type.GetScalarType().GetFltSemantics();
            if (FCmpConvert(midpoint, sourceSemantics).Compare(value) != LlvmFloatCompare.Equal) midpoint = midpoint.Next(!down).Value;
            if (next.IsInfinity)
            {
                if (FCmpConvert(midpoint, sourceSemantics).IsInfinity) return null;
                LlvmAPFloat afterMidpoint = midpoint.Next(down).Value;
                LlvmAPFloat narrowed = FCmpConvert(afterMidpoint, sourceSemantics);
                if (!narrowed.IsNaN && !narrowed.IsInfinity) return null;
            }
            Count("fcmp.rewrite.truncation_midpoint");
            return NewFCmp(predicate, input, LlvmConstantFP.Get(destination, midpoint), comparison);
        }

        private LlvmInstruction? FoldFCmpExtensionConstant(LlvmFCmpInstruction comparison, LlvmCastInstruction extension, LlvmConstantFP constant)
        {
            Count("fcmp.extension.queries");
            LlvmValue input = extension.Operand;
            LlvmAPFloatSemantics semantics = input.Type.GetScalarType().GetFltSemantics();
            LlvmAPFloatConvertResult converted = constant.Value.Convert(semantics, LlvmRoundingMode.NearestTiesToEven);
            LlvmAPFloat narrowed = converted.Value;
            if (converted.LosesInfo)
            {
                switch (comparison.Predicate)
                {
                    case LlvmCmpPredicate.FCmpOeq: return FCmpBoolean(comparison, false, "extension_impossible_equal");
                    case LlvmCmpPredicate.FCmpOne: return NewFCmp(LlvmCmpPredicate.FCmpOrd, input, FCmpZero(input.Type));
                    case LlvmCmpPredicate.FCmpUeq: return NewFCmp(LlvmCmpPredicate.FCmpUno, input, FCmpZero(input.Type));
                    case LlvmCmpPredicate.FCmpUne: return FCmpBoolean(comparison, true, "extension_impossible_unequal");
                }
            }
            LlvmAPFloat magnitude = narrowed.IsNegative ? narrowed.ChangeSign() : narrowed;
            if (!converted.LosesInfo && (magnitude.IsZero || magnitude.Compare(LlvmAPFloat.GetSmallestNormalized(semantics)) != LlvmFloatCompare.LessThan))
            {
                Count("fcmp.rewrite.lossless_extension_constant");
                return NewFCmp(comparison.Predicate, input, LlvmConstantFP.Get(input.Type, narrowed), comparison);
            }
            Count("fcmp.extension.retained_lossy_or_denormal");
            return null;
        }
    }
}
