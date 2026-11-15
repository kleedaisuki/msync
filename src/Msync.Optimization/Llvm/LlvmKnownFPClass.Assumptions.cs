// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeKnownFPClassFromContext, assumption-cache loop.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownFPClasses
{
    private static LlvmKnownFPClass FromContext(LlvmValue value, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics,
        LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        var known = new LlvmKnownFPClass();
        if (condContext is not null && condContext.AffectedValues.Contains(value))
        {
            FromCondition(value, condContext.Cond, !condContext.Invert, ref known, metrics, 0);
            Count(metrics, "known_fp.injected_condition.applied");
        }
        if (context is null) return known;
        if (conditions is not null && tree is not null)
        {
            foreach (var branch in conditions.ConditionsFor(value))
            {
                Count(metrics, "known_fp.dom_conditions.visited");
                if (tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(0), context.Parent!))
                {
                    FromCondition(value, branch.Condition, true, ref known, metrics, 0);
                    Count(metrics, "known_fp.dom_conditions.true_edge");
                }
                if (tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(1), context.Parent!))
                {
                    FromCondition(value, branch.Condition, false, ref known, metrics, 0);
                    Count(metrics, "known_fp.dom_conditions.false_edge");
                }
            }
        }
        if (assumptions is null) return known;
        foreach (var entry in assumptions.AssumptionsFor(value))
        {
            Count(metrics, "known_fp.assumptions.visited");
            if (entry.Assume.Value is not { } cached)
            { Count(metrics, "known_fp.assumptions.expired"); continue; }
            var assumption = (LlvmCallInstruction)cached;
            if (!LlvmValueTracking.IsValidAssumeForContext(assumption, context, tree, allowEphemerals, metrics))
            { Count(metrics, "known_fp.assumptions.invalid_context"); continue; }
            // This source loop consumes the condition, including for entries
            // indexed by a retained bundle; it does not infer FP bundle facts.
            var before = known;
            FromCondition(value, assumption.GetArgument(0), true, ref known, metrics, 0);
            Count(metrics, before.GetKnownFPClasses() != known.GetKnownFPClasses() || before.SignBit != known.SignBit
                ? "known_fp.assumptions.refined" : "known_fp.assumptions.no_class_fact");
        }
        return known;
    }
}
