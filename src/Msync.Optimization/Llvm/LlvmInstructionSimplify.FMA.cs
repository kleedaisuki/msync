// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp6249–6300 simplifyFMAFMul, default FP environment.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal static LlvmValue? SimplifyFMAFMul(LlvmValue left, LlvmValue right, LlvmFastMathFlags flags, BinaryQuery query)
    {
        Count(query.Metrics, "simplify.fma_product.queries");
        if (SimplifyFPOperands(left, right, flags, query) is { } special) return special;
        if (FMAAnyZero(left) || FMAConstant(left, out var first) && first.BitwiseIsEqual(LlvmAPFloat.GetOne(first.Semantics)))
            (left, right) = (right, left);
        if (FMAConstant(right, out var constant) && constant.BitwiseIsEqual(LlvmAPFloat.GetOne(constant.Semantics))) return left;
        if (FMAAnyZero(right))
        {
            if ((flags & (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros)) == (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros))
                return LlvmConstantFP.Get(left.Type, LlvmAPFloat.GetZero(left.Type.GetScalarType().GetFltSemantics()));
            var known = LlvmKnownFPClasses.Compute(left, flags, LlvmFPClassTest.Infinity | LlvmFPClassTest.NaN,
                query.Context, query.Tree, query.Metrics, assumptions: query.Assumptions,
                allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext);
            if (known.IsKnownNeverInfOrNaN())
            {
                if ((flags & LlvmFastMathFlags.NoSignedZeros) != 0) return LlvmConstantFP.Get(left.Type, LlvmAPFloat.GetZero(left.Type.GetScalarType().GetFltSemantics()));
                if (known.SignBit == false) return right;
                if (known.SignBit == true && right is LlvmConstant zero)
                    return LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, zero, query.Layout, query.Metrics);
            }
        }
        const LlvmFastMathFlags squareFlags = LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros;
        if (ReferenceEquals(left, right) && (flags & squareFlags) == squareFlags && left is LlvmCallBase sqrt &&
            sqrt.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Sqrt) return sqrt.GetArgument(0);
        return null;
    }
    private static bool FMAConstant(LlvmValue value, out LlvmAPFloat number)
    {
        var constant = value as LlvmConstant;
        if (constant is not LlvmConstantFP && constant?.Type.IsVectorTy() == true) constant = constant.GetSplatValue(true);
        if (constant is LlvmConstantFP floating) { number = floating.Value; return true; }
        number = default; return false;
    }
    internal static bool FMAAnyZero(LlvmValue value)
    {
        if (FMAConstant(value, out var number)) return number.IsZero;
        if (value is not LlvmConstant constant || value.Type is not LlvmFixedVectorType vector) return false;
        bool matched = false;
        for (uint element = 0; element < vector.NumElements; ++element)
        {
            LlvmConstant? lane = constant.GetAggregateElement(element);
            if (lane is LlvmPoisonValue) continue;
            if (lane is not LlvmConstantFP { Value.IsZero: true }) return false;
            matched = true;
        }
        return matched;
    }
}
