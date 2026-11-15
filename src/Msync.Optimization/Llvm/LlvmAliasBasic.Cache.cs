// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp aliasCheck cache protocol.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    private LlvmAliasResult CheckWithCache(LlvmValue a, LlvmLocationSize aSize, LlvmValue b, LlvmLocationSize bSize,
        LlvmValue objectA, LlvmValue objectB, LlvmAAQueryInfo query)
    {
        if (aSize.MayBeBeforePointer || bSize.MayBeBeforePointer)
            aSize = bSize = LlvmLocationSize.AfterPointer();
        if (query.Depth >= 512) { Count("aa.depth_limit"); return LlvmAliasResult.MayAlias; }
        var first = new LlvmAALocationKey(a, aSize, query.MayBeCrossIteration);
        var second = new LlvmAALocationKey(b, bSize, query.MayBeCrossIteration);
        bool swapped = query.Order(a) > query.Order(b);
        var pair = swapped ? new LlvmAALocationPair(second, first) : new(first, second);
        if (query.AliasCache.TryGetValue(pair, out var entry))
        {
            Count("aa.cache.hit");
            if (!entry.IsDefinitive)
            {
                ++query.NumAssumptionUses;
                if (entry.IsAssumption) ++entry.NumAssumptionUses;
                Count("aa.cache.assumption_use");
            }
            return entry.Result.Swap(swapped);
        }
        Count("aa.cache.miss");
        entry = new();
        query.AliasCache.Add(pair, entry);
        int originalUses = query.NumAssumptionUses;
        int originalResults = query.AssumptionBasedResults.Count;
        var result = CheckRecursive(a, aSize, b, bSize, query, objectA, objectB);
        Debug.Assert(query.AliasCache.ContainsKey(pair));
        bool disproven = entry.NumAssumptionUses > 0 && result != LlvmAliasKind.NoAlias;
        if (disproven) { result = LlvmAliasResult.MayAlias; Count("aa.cache.assumption_disproven"); }
        query.NumAssumptionUses -= entry.NumAssumptionUses;
        entry.Result = result.Swap(swapped);
        if (disproven)
        {
            while (query.AssumptionBasedResults.Count > originalResults)
            {
                var invalid = query.AssumptionBasedResults[^1];
                query.AssumptionBasedResults.RemoveAt(query.AssumptionBasedResults.Count - 1);
                query.AliasCache.Remove(invalid);
                Count("aa.cache.assumption_result_invalidated");
            }
        }
        if (originalUses != query.NumAssumptionUses && result != LlvmAliasKind.MayAlias)
        {
            query.AssumptionBasedResults.Add(pair);
            entry.NumAssumptionUses = LlvmAAQueryInfo.CacheEntry.AssumptionBased;
        }
        else entry.NumAssumptionUses = LlvmAAQueryInfo.CacheEntry.Definitive;
        if (query.Depth == 1)
        {
            foreach (var assumed in query.AssumptionBasedResults)
                if (query.AliasCache.TryGetValue(assumed, out var proven))
                    proven.NumAssumptionUses = LlvmAAQueryInfo.CacheEntry.Definitive;
            query.AssumptionBasedResults.Clear();
            query.NumAssumptionUses = 0;
        }
        return result;
    }
}
