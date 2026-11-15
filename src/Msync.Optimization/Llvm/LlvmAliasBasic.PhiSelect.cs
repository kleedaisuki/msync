// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp aliasPHI/aliasSelect.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    private static LlvmAliasResult Merge(LlvmAliasResult a, LlvmAliasResult b)
    {
        if (a == b) return a;
        if (a == LlvmAliasKind.PartialAlias && b == LlvmAliasKind.MustAlias
            || b == LlvmAliasKind.PartialAlias && a == LlvmAliasKind.MustAlias) return LlvmAliasResult.PartialAlias;
        return LlvmAliasResult.MayAlias;
    }
    private LlvmAliasResult AliasSelect(LlvmSelectInstruction select, LlvmLocationSize size,
        LlvmValue other, LlvmLocationSize otherSize, LlvmAAQueryInfo query)
    {
        if (other is LlvmSelectInstruction second && EqualInPotentialCycles(select.Condition, second.Condition, query))
        {
            var first = query.Results.Alias(new(select.TrueValue, size), new(second.TrueValue, otherSize), query);
            if (first == LlvmAliasKind.MayAlias) return first;
            var last = query.Results.Alias(new(select.FalseValue, size), new(second.FalseValue, otherSize), query);
            return Merge(last, first);
        }
        var result = query.Results.Alias(new(select.TrueValue, size), new(other, otherSize), query);
        if (result == LlvmAliasKind.MayAlias) return result;
        var secondResult = query.Results.Alias(new(select.FalseValue, size), new(other, otherSize), query);
        return Merge(secondResult, result);
    }
    private LlvmAliasResult AliasPhi(LlvmPhiInstruction phi, LlvmLocationSize size,
        LlvmValue other, LlvmLocationSize otherSize, LlvmAAQueryInfo query)
    {
        if (phi.IncomingCount == 0) return LlvmAliasResult.NoAlias;
        if (other is LlvmPhiInstruction second && second.Parent == phi.Parent && !query.MayBeCrossIteration)
        {
            LlvmAliasResult? result = null;
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                int secondIndex = second.GetBasicBlockIndex(phi.GetIncomingBlock(index));
                var current = query.Results.Alias(new(phi.GetIncomingValue(index), size),
                    new(second.GetIncomingValue(secondIndex), otherSize), query);
                result = result is { } previous ? Merge(previous, current) : current;
                if (result.Value == LlvmAliasKind.MayAlias) break;
            }
            return result!.Value;
        }
        var sources = new List<LlvmValue>();
        var unique = new HashSet<LlvmValue>(ReferenceEqualityComparer.Instance);
        bool recursive = false;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            var value = phi.GetIncomingValue(index);
            if (ReferenceEquals(value, phi)) continue;
            if (ReferenceEquals(LlvmValueTracking.GetUnderlyingObject(value, metrics: Metrics), phi))
            {
                recursive = true;
                continue;
            }
            if (unique.Add(value)) sources.Add(value);
        }
        if (sources.Count > 1 && sources.Any(value => value is LlvmPhiInstruction))
        { Count("aa.phi.multiple_phi_sources"); return LlvmAliasResult.MayAlias; }
        if (sources.Count == 0) return LlvmAliasResult.MayAlias;
        if (recursive) size = LlvmLocationSize.BeforeOrAfterPointer();
        bool savedCrossIteration = query.MayBeCrossIteration;
        query.MayBeCrossIteration = true;
        try
        {
            var result = query.Results.Alias(new(sources[0], size), new(other, otherSize), query);
            if (result == LlvmAliasKind.MayAlias || recursive && result != LlvmAliasKind.NoAlias)
                return LlvmAliasResult.MayAlias;
            for (int index = 1; index < sources.Count; ++index)
            {
                result = Merge(query.Results.Alias(new(sources[index], size), new(other, otherSize), query), result);
                if (result == LlvmAliasKind.MayAlias) break;
            }
            return result;
        }
        finally { query.MayBeCrossIteration = savedCrossIteration; }
    }
}
