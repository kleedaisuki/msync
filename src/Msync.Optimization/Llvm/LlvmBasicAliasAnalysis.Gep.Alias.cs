// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp 1139..1386.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    private LlvmAliasResult AliasGEP(LlvmGetElementPtrInstruction gep, LlvmLocationSize firstSize,
        LlvmValue other, LlvmLocationSize secondSize, LlvmValue underlyingFirst, LlvmValue underlyingSecond, LlvmAAQueryInfo query)
    {
        Count("aa.gep.alias.queries");
        LlvmAliasResult BaseObjectsAlias()
        {
            var result = query.Results.Alias(LlvmMemoryLocation.GetBeforeOrAfter(underlyingFirst),
                LlvmMemoryLocation.GetBeforeOrAfter(underlyingSecond), query);
            return result == LlvmAliasKind.NoAlias ? LlvmAliasResult.NoAlias : LlvmAliasResult.MayAlias;
        }
        if (!firstSize.HasValue && !secondSize.HasValue)
        {
            if (other is LlvmPhiInstruction or LlvmSelectInstruction) return LlvmAliasResult.MayAlias;
            if (other is LlvmGetElementPtrInstruction || underlyingFirst is LlvmGetElementPtrInstruction or LlvmPhiInstruction or LlvmSelectInstruction
                || underlyingSecond is LlvmGetElementPtrInstruction or LlvmPhiInstruction or LlvmSelectInstruction) return BaseObjectsAlias();
            return LlvmAliasResult.MayAlias;
        }
        var tree = query.UseDominatorTree ? Tree : null;
        var first = DecomposeGepExpression(gep, tree);
        var second = DecomposeGepExpression(other, tree);
        if (ReferenceEquals(first.Base, gep) && ReferenceEquals(second.Base, other)) return LlvmAliasResult.MayAlias;
        if (first.Offset.BitWidth != second.Offset.BitWidth) return BaseObjectsAlias();
        if (first.VarIndices.Count < second.VarIndices.Count)
        {
            (first, second) = (second, first);
            (firstSize, secondSize) = (secondSize, firstSize);
            (underlyingFirst, underlyingSecond) = (underlyingSecond, underlyingFirst);
        }
        SubtractDecomposedGeps(first, second, query);
        if (first.NWFlags.IsInBounds && first.VarIndices.Count == 0 && secondSize.HasValue && !secondSize.IsScalable
            && GepSignedAtLeast(first.Offset, (long)secondSize.GetValue().GetFixedValue()) && IsBaseOfObject(second.Base))
            return LlvmAliasResult.NoAlias;
        if (second.NWFlags.IsInBounds && first.VarIndices.Count == 0 && firstSize.HasValue && !firstSize.IsScalable
            && GepSignedAtMost(first.Offset, -(long)firstSize.GetValue().GetFixedValue()) && IsBaseOfObject(first.Base))
            return LlvmAliasResult.NoAlias;
        if (first.Offset.IsZero && first.VarIndices.Count == 0)
            return query.Results.Alias(new(first.Base, firstSize), new(second.Base, secondSize), query);
        var baseAlias = query.Results.Alias(LlvmMemoryLocation.GetBeforeOrAfter(first.Base),
            LlvmMemoryLocation.GetBeforeOrAfter(second.Base), query);
        if (baseAlias != LlvmAliasKind.MustAlias) return baseAlias;
        if (first.VarIndices.Count == 0)
        {
            bool swapped = first.Offset.IsNegative;
            var leftSize = swapped ? firstSize : secondSize;
            var rightSize = swapped ? secondSize : firstSize;
            if (swapped) first.Offset = -first.Offset;
            if (!leftSize.HasValue) return LlvmAliasResult.MayAlias;
            var size = leftSize.GetValue();
            if (!size.IsScalable)
            {
                if (!GepUlt64(first.Offset, size.GetFixedValue())) return LlvmAliasResult.NoAlias;
                var partial = LlvmAliasResult.PartialAlias;
                if (rightSize.HasValue && !rightSize.IsScalable && GepUle64(first.Offset, int.MaxValue)
                    && GepUle64(first.Offset + new LlvmAPInt(first.Offset.BitWidth, rightSize.GetValue().GetFixedValue(), implicitTrunc: true), size.GetFixedValue()))
                    partial = partial.WithOffset(checked(-(int)first.Offset.GetSExtValue())).Swap(swapped);
                return partial;
            }
            // ValueTracking.cpp getVScaleRange with no vscale_range attribute.
            var maximumScale = LlvmAPInt.GetMaxValue(first.Offset.BitWidth);
            var upper = maximumScale.UMulOverflow(new(first.Offset.BitWidth, size.GetKnownMinValue(), implicitTrunc: true), out bool overflow);
            if (!overflow && first.Offset.Uge(upper)) return LlvmAliasResult.NoAlias;
        }
        // No vscale intrinsic ID/attribute carrier is represented, so the source
        // vscale-specific branch cannot match a value in this IR model.
        if (first.VarIndices.Count != 0 && first.NWFlags.HasNoUnsignedWrap && secondSize.HasValue
            && !secondSize.IsScalable && !GepUlt64(first.Offset, secondSize.GetValue().GetFixedValue())) return LlvmAliasResult.NoAlias;
        if (firstSize.IsScalable || secondSize.IsScalable) return LlvmAliasResult.MayAlias;
        uint width = first.Offset.BitWidth;
        if (!firstSize.HasValue || !secondSize.HasValue || !GepFitsUInt(width, firstSize.GetValue().GetFixedValue())
            || !GepFitsUInt(width, secondSize.GetValue().GetFixedValue())) return LlvmAliasResult.MayAlias;
        var info = AnalyzeVariableOffsets(first, tree);
        var modulo = first.Offset.SRem(info.Gcd);
        if (modulo.IsNegative) modulo += info.Gcd;
        if (!GepUlt64(modulo, secondSize.GetValue().GetFixedValue())
            && !GepUlt64(info.Gcd - modulo, firstSize.GetValue().GetFixedValue())) return LlvmAliasResult.NoAlias;
        var zero = LlvmAPInt.GetZero(width);
        var rangeFirst = info.OffsetRange.Add(new(zero, new(width, firstSize.GetValue().GetFixedValue(), implicitTrunc: true)));
        var rangeSecond = new LlvmConstantRange(zero, new(width, secondSize.GetValue().GetFixedValue(), implicitTrunc: true));
        if (rangeFirst.IntersectWith(rangeSecond).IsEmptySet) return LlvmAliasResult.NoAlias;
        if (ComputeMinAbsVarOffset(first, info.KnownBits, tree, query) is { } minimum)
        {
            var low = first.Offset - minimum;
            var high = first.Offset + minimum;
            if (low.IsNegative && !GepUlt64(-low, firstSize.GetValue().GetFixedValue())
                && !high.IsNegative && !GepUlt64(high, secondSize.GetValue().GetFixedValue())) return LlvmAliasResult.NoAlias;
        }
        if (ComputeConstantOffsetHeuristic(first, firstSize, secondSize, tree, query)) return LlvmAliasResult.NoAlias;
        Count("aa.gep.alias.dynamic_unresolved");
        return LlvmAliasResult.MayAlias;
    }
    private static bool GepUlt64(LlvmAPInt value, ulong other) => value.GetActiveBits() <= 64 && value.GetZExtValue() < other;
    private static bool GepUle64(LlvmAPInt value, ulong other) => value.GetActiveBits() <= 64 && value.GetZExtValue() <= other;
    private static bool GepFitsUInt(uint width, ulong value) => width >= 64 || value < (1UL << (int)width);
    private static bool GepSignedAtLeast(LlvmAPInt value, long other) => value.BitWidth <= 64
        ? value.GetSExtValue() >= other : value.Sge(new(value.BitWidth, unchecked((ulong)other), isSigned: true));
    private static bool GepSignedAtMost(LlvmAPInt value, long other) => value.BitWidth <= 64
        ? value.GetSExtValue() <= other : value.Sle(new(value.BitWidth, unchecked((ulong)other), isSigned: true));
}
