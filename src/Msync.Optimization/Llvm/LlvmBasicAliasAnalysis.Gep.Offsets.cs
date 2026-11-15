// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp 1947..2165.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    private readonly record struct VariableGepOffsetInfo(LlvmAPInt Gcd, LlvmConstantRange OffsetRange, List<LlvmKnownBits> KnownBits);
    private static LlvmAPInt GepAbs(LlvmAPInt value) => value.IsNegative ? -value : value;

    private VariableGepOffsetInfo AnalyzeVariableOffsets(DecomposedGep gep, LlvmDominatorTree? tree)
    {
        var gcd = LlvmAPInt.GetZero(gep.Offset.BitWidth);
        var offsetRange = new LlvmConstantRange(gep.Offset);
        List<LlvmKnownBits> knownBits = new(gep.VarIndices.Count);
        for (int index = 0; index < gep.VarIndices.Count; index++)
        {
            Count("aa.gep.variable_offsets.indices");
            var variable = gep.VarIndices[index];
            var scale = variable.Scale;
            var known = LlvmKnownBitsAnalysis.Compute(variable.Val.V, context: variable.CtxI, tree: tree, metrics: Metrics);
            knownBits.Add(known);
            var scaleForGcd = variable.IsNSW ? scale : new LlvmAPInt(scale.BitWidth, 1).Shl(scale.CountTrailingZeros());
            uint trailing = known.CountMinTrailingZeros();
            if (trailing > 0)
                scaleForGcd = scaleForGcd.Shl(Math.Min(trailing, scale.BitWidth - scaleForGcd.GetSignificantBits()));
            gcd = index == 0 ? GepAbs(scaleForGcd) : GepGcd(gcd, GepAbs(scaleForGcd));
            var range = LlvmConstantRangeAnalysis.Compute(variable.Val.V, context: variable.CtxI, tree: tree, metrics: Metrics);
            range = range.IntersectWith(LlvmConstantRange.FromKnownBits(known, true), LlvmPreferredRangeType.Signed);
            range = variable.Val.EvaluateWith(range).SExtOrTrunc(offsetRange.BitWidth);
            range = variable.IsNSW ? GepMulSat(range, new(scale)) : GepMulFast(range, new(scale));
            offsetRange = variable.IsNegated ? offsetRange.Sub(range) : offsetRange.Add(range);
        }
        return new(gcd, offsetRange, knownBits);
    }

    private static bool MultiplyByScaleNoWrap(VariableGepIndex variable)
    {
        if (variable.IsNSW) return true;
        int originalWidth = (int)variable.Val.V.Type.GetPrimitiveSizeInBits().GetFixedValue();
        int maximumScaleWidth = (int)variable.Val.BitWidth - originalWidth;
        return maximumScaleWidth > 0 && variable.Scale.Ule(LlvmAPInt.GetMaxValue((uint)maximumScaleWidth).ZExt(variable.Scale.BitWidth));
    }

    private LlvmAPInt? ComputeMinAbsVarOffset(DecomposedGep gep, IReadOnlyList<LlvmKnownBits> known,
        LlvmDominatorTree? tree, LlvmAAQueryInfo query)
    {
        if (gep.VarIndices.Count == 1)
        {
            var variable = gep.VarIndices[0];
            if (variable.Val.TruncBits == 0 && LlvmKnownBitsAnalysis.IsKnownNonZero(variable.Val.V,
                context: variable.CtxI, tree: tree, metrics: Metrics) && MultiplyByScaleNoWrap(variable)) return GepAbs(variable.Scale);
            return null;
        }
        if (gep.VarIndices.Count != 2) return null;
        var first = gep.VarIndices[0];
        var second = gep.VarIndices[1];
        if (first.Val.TruncBits != 0 || !first.Val.HasSameCastsAs(second.Val) || query.MayBeCrossIteration ||
            !MultiplyByScaleNoWrap(first) || !MultiplyByScaleNoWrap(second)) return null;
        if (first.HasNegatedScaleOf(second))
        {
            if (LlvmKnownBitsAnalysis.IsKnownNonEqual(first.Val.V, second.Val.V,
                context: first.CtxI ?? second.CtxI, tree: tree, metrics: Metrics)) return GepAbs(first.Scale);
            return null;
        }
        bool negativeFirst = first.IsNegated ^ first.Scale.IsNegative;
        bool negativeSecond = second.IsNegated ^ second.Scale.IsNegative;
        if (negativeFirst == negativeSecond) return null;
        var firstScale = GepAbs(first.Scale);
        var secondScale = GepAbs(second.Scale);
        var gcd = GepGcd(firstScale, secondScale);
        var firstCoefficient = firstScale.UDiv(gcd);
        var secondCoefficient = secondScale.UDiv(gcd);
        var firstKnown = LlvmKnownBits.Mul(first.Val.EvaluateWith(known[0]), LlvmKnownBits.MakeConstant(firstCoefficient));
        var secondKnown = LlvmKnownBits.Mul(second.Val.EvaluateWith(known[1]), LlvmKnownBits.MakeConstant(secondCoefficient));
        return LlvmKnownBitsAnalysis.Compare(LlvmCmpPredicate.ICmpNe, firstKnown, secondKnown) == true ? gcd : null;
    }

    private bool ComputeConstantOffsetHeuristic(DecomposedGep gep, LlvmLocationSize firstSize,
        LlvmLocationSize secondSize, LlvmDominatorTree? tree, LlvmAAQueryInfo query)
    {
        if (gep.VarIndices.Count != 2 || !firstSize.HasValue || !secondSize.HasValue) return false;
        var first = gep.VarIndices[0];
        var second = gep.VarIndices[1];
        if (first.Val.TruncBits != 0 || !first.Val.HasSameCastsAs(second.Val) || !first.HasNegatedScaleOf(second)
            || !ReferenceEquals(first.Val.V.Type, second.Val.V.Type)) return false;
        if (!MultiplyByScaleNoWrap(first) || !MultiplyByScaleNoWrap(second)) return false;
        var left = GetLinearExpression(new(first.Val.V), 0, tree);
        var right = GetLinearExpression(new(second.Val.V), 0, tree);
        if (left.Scale != right.Scale || !left.Val.HasSameCastsAs(right.Val)
            || !EqualInPotentialCycles(left.Val.V, right.Val.V, query)) return false;
        var minimum = left.Offset - right.Offset;
        if ((-minimum).Ult(minimum)) minimum = -minimum;
        var bytes = minimum.ZExtOrTrunc(first.Scale.BitWidth) * GepAbs(first.Scale);
        var offset = GepAbs(gep.Offset);
        return bytes.Uge(new LlvmAPInt(bytes.BitWidth, firstSize.GetValue().GetFixedValue(), implicitTrunc: true) + offset)
            && bytes.Uge(new LlvmAPInt(bytes.BitWidth, secondSize.GetValue().GetFixedValue(), implicitTrunc: true) + offset);
    }
}
