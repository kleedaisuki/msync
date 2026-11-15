// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isKnownNonZeroFromAssume, isKnownNonEqualFromContext AssumptionCache cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool IsKnownNonZeroFromAssume(LlvmValue value, LlvmInstruction? context, LlvmDominatorTree? tree,
        Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (context is null || assumptions is null) return false;
        foreach (var entry in assumptions.AssumptionsFor(value))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption) continue;
            Count(metrics, "value_facts.nonzero.assume_entries");
            if (entry.Index != LlvmAssumptionCache.ExprResultIdx)
            {
                if (LlvmAssumeBundleQueries.AssumeBundleImpliesNonNull(value, assumption.GetOperandBundleAt((int)entry.Index)) && IsValid(assumption))
                { Count(metrics, "value_facts.nonzero.assume_bundle"); return true; }
                continue;
            }
            var condition = assumption.GetArgument(0);
            if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc && ReferenceEquals(trunc.Operand, value) && IsValid(assumption))
            { Count(metrics, "value_facts.nonzero.assume_trunc"); return true; }
            if (condition is not LlvmICmpInstruction comparison) continue;
            var predicate = comparison.Predicate;
            LlvmValue other;
            if (MatchesValue(comparison.Left)) other = comparison.Right;
            else if (MatchesValue(comparison.Right)) { other = comparison.Left; predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            else continue;
            if (ComparisonExcludesZero(predicate, other) && IsValid(assumption))
            { Count(metrics, "value_facts.nonzero.assume_icmp"); return true; }
        }
        return false;
        bool MatchesValue(LlvmValue candidate) => ReferenceEquals(candidate, value) || candidate is LlvmCastInstruction
            { Opcode: LlvmOpcode.PtrToInt } cast && ReferenceEquals(cast.Operand, value);
        bool IsValid(LlvmInstruction assumption) => LlvmValueTracking.IsValidAssumeForContext(assumption, context, tree, allowEphemerals, metrics);
    }

    private static bool IsKnownNonEqualFromAssume(LlvmValue left, LlvmValue right, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo,
        LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (context is null || assumptions is null) return false;
        foreach (var entry in assumptions.AssumptionsFor(left))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption) continue;
            Count(metrics, "value_facts.nonequal.assume_entries");
            if (LlvmInstructionSimplify.IsImpliedByAssumeCondition(assumption.GetArgument(0), LlvmCmpPredicate.ICmpNe, left, right,
                context, tree, metrics, useInstructionInfo, depth, assumptions, allowEphemerals, conditions, condContext) == true &&
                LlvmValueTracking.IsValidAssumeForContext(assumption, context, tree, allowEphemerals, metrics)) return true;
        }
        return false;
    }
    private static bool IsPowerOfTwoFromAssume(LlvmValue value, bool orZero, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (context is null || assumptions is null) return false;
        foreach (var entry in assumptions.AssumptionsFor(value))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption ||
                assumption.GetArgument(0) is not LlvmICmpInstruction { Right: LlvmConstantInt bound,
                    Left: LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } pop } comparison ||
                descriptor.Kind != LlvmIntrinsicKind.CTPop || !ReferenceEquals(pop.GetArgument(0), value)) continue;
            Count(metrics, "value_facts.power.assume_ctpop");
            if (((orZero && comparison.Predicate == LlvmCmpPredicate.ICmpUlt && bound.Value == new LlvmAPInt(bound.Value.BitWidth, 2)) ||
                 (comparison.Predicate == LlvmCmpPredicate.ICmpEq && bound.IsOne)) &&
                LlvmValueTracking.IsValidAssumeForContext(assumption, context, tree, allowEphemerals, metrics)) return true;
        }
        return false;
    }

}

public static partial class LlvmInstructionSimplify
{
    internal static bool? IsImpliedByAssumeCondition(LlvmValue condition, LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right,
        LlvmInstruction context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, uint depth, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) =>
        IsImpliedCondition(condition, predicate, left, right,
            new(context.Parent!.Module!.DataLayout, context, metrics, tree, UseInstructionInfo: useInstructionInfo, Assumptions: assumptions, AllowEphemerals: allowEphemerals, DC: conditions, CondContext: condContext), true, depth);
}
