// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp6030–6072 propagateNaN; PatternMatch.h cstfp_pred_ty.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmConstantFP CanonicalNaN(LlvmType type) => LlvmConstantFP.Get(type,
        LlvmAPFloat.FromBits(0x7ff8000000000000UL).Convert(type.GetScalarType().GetFltSemantics()).Value);

    private static bool MatchFPClassConstant(LlvmValue value, bool nan)
    {
        if (value is LlvmConstantFP floating) return nan ? floating.Value.IsNaN : floating.Value.IsInfinity;
        if (value is not LlvmConstant constant || value.Type is not LlvmVectorType vector) return false;
        if (constant.GetSplatValue(true) is LlvmConstantFP splat) return nan ? splat.Value.IsNaN : splat.Value.IsInfinity;
        if (vector is not LlvmFixedVectorType fixedVector) return false;
        bool matched = false;
        for (uint element = 0; element < fixedVector.NumElements; ++element)
        {
            LlvmConstant? lane = constant.GetAggregateElement(element);
            if (lane is LlvmPoisonValue) continue;
            if (lane is not LlvmConstantFP item || !(nan ? item.Value.IsNaN : item.Value.IsInfinity)) return false;
            matched = true;
        }
        return matched;
    }

    private static LlvmConstant PropagateNaN(LlvmConstant value)
    {
        LlvmType type = value.Type;
        if (type is LlvmFixedVectorType vector)
        {
            var elements = new LlvmConstant[vector.NumElements];
            for (uint element = 0; element < vector.NumElements; ++element)
            {
                LlvmConstant? lane = value.GetAggregateElement(element);
                elements[element] = lane is LlvmPoisonValue ? lane : lane is LlvmConstantFP { Value.IsNaN: true } floating
                    ? LlvmConstantFP.Get(floating.Type, floating.Value.MakeQuiet()) : CanonicalNaN(vector.ElementType);
            }
            return LlvmConstantVector.Get(elements);
        }
        if (!MatchFPClassConstant(value, true)) return CanonicalNaN(type);
        if (type is LlvmScalableVectorType) value = value.GetSplatValue()!;
        return LlvmConstantFP.Get(type, ((LlvmConstantFP)value).Value.MakeQuiet());
    }
}
