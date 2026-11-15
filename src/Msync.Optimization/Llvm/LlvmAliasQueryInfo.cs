// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: AliasAnalysis.h AAQueryInfo.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal readonly record struct LlvmAALocationKey(LlvmValue Pointer, LlvmLocationSize Size, bool CrossIteration);
internal readonly record struct LlvmAALocationPair(LlvmAALocationKey First, LlvmAALocationKey Second);
internal sealed class LlvmAAQueryInfo(LlvmAAResults results, LlvmCaptureAnalysis? captures = null)
{
    internal sealed class CacheEntry
    {
        internal const int Definitive = -2, AssumptionBased = -1;
        internal LlvmAliasResult Result = LlvmAliasResult.NoAlias;
        internal int NumAssumptionUses;
        internal bool IsDefinitive => NumAssumptionUses == Definitive;
        internal bool IsAssumption => NumAssumptionUses >= 0;
    }
    internal readonly LlvmAAResults Results = results;
    internal readonly LlvmCaptureAnalysis Captures = captures ?? new LlvmSimpleCaptureAnalysis(results.Metrics);
    internal readonly Dictionary<LlvmAALocationPair, CacheEntry> AliasCache = [];
    internal readonly List<LlvmAALocationPair> AssumptionBasedResults = [];
    // C++ pointer ordering is only a pair-canonicalization carrier, not an IR
    // fact. Managed reference IDs serve that same purpose within this query.
    private readonly Dictionary<LlvmValue, int> pointerOrder = new(ReferenceEqualityComparer.Instance);
    internal uint Depth;
    internal int NumAssumptionUses;
    internal bool MayBeCrossIteration;
    internal bool UseDominatorTree = true;
    internal bool AllowEphemerals = true;
    internal int Order(LlvmValue value)
    {
        if (!pointerOrder.TryGetValue(value, out int order)) pointerOrder.Add(value, order = pointerOrder.Count);
        return order;
    }
}
