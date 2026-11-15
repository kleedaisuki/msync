// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// computeConstantRange's final AssumptionCache range intersections.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantRangeAnalysis
{
    private static LlvmConstantRange IntersectAssumedRanges(LlvmValue value, LlvmConstantRange range, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics,
        bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (context is null || assumptions is null) return range;
        foreach (var entry in assumptions.AssumptionsFor(value))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption) continue;
            Debug.Assert(ReferenceEquals(assumption.Parent!.Parent, context.Parent!.Parent));
            Debug.Assert(assumption.CalledFunction?.IntrinsicDescriptor?.IsAssume == true);
            Count(metrics, "constant_range.assume.entries");
            if (entry.Index != LlvmAssumptionCache.ExprResultIdx)
            {
                var bundle = assumption.GetOperandBundleAt((int)entry.Index);
                if (bundle.Tag == "range")
                {
                    var fact = LlvmAssumeBundleQueries.GetAssumeRangeInfo(bundle);
                    if (ReferenceEquals(fact.Value, value) && IsValid(assumption))
                    { range = range.IntersectWith(fact.Range); Count(metrics, "constant_range.assume.range_bundle"); }
                }
                continue;
            }
            if (!IsValid(assumption) || assumption.GetArgument(0) is not LlvmICmpInstruction comparison ||
                !ReferenceEquals(comparison.Left, value)) continue;
            var right = Compute(comparison.Right, false, depth + 1, assumption, tree, metrics,
                useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            range = range.IntersectWith(LlvmConstantRange.MakeAllowedICmpRegion(comparison.Predicate, right));
            Count(metrics, "constant_range.assume.icmp");
        }
        return range;
        bool IsValid(LlvmInstruction assumption) => LlvmValueTracking.IsValidAssumeForContext(assumption, context, tree, allowEphemerals, metrics);
    }
}
