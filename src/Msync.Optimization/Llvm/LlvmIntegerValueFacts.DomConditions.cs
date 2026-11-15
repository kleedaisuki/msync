// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp,
// isKnownToBeAPowerOfTwo and isKnownNonEqualFromContext dominating-condition loops.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool IsPowerOfTwoFromDomConditions(LlvmValue value, bool orZero, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, LlvmDomConditionCache? conditions, LlvmCondContext? condContext)
    {
        if (conditions is null || context is null || tree is null) return false;
        foreach (var branch in conditions.ConditionsFor(value))
        {
            Count(metrics, "value_facts.power.dom_conditions.visited");
            if (IsImpliedPowerOfTwoFromCondition(value, orZero, branch.Condition, true) &&
                tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(0), context.Parent!))
            { Count(metrics, "value_facts.power.dom_conditions.true_edge"); return true; }
            if (IsImpliedPowerOfTwoFromCondition(value, orZero, branch.Condition, false) &&
                tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(1), context.Parent!))
            { Count(metrics, "value_facts.power.dom_conditions.false_edge"); return true; }
        }
        return false;
    }

    private static bool IsImpliedPowerOfTwoFromCondition(LlvmValue value, bool orZero, LlvmValue condition, bool isTrue)
    {
        if (condition is not LlvmICmpInstruction { Right: LlvmConstantInt bound,
            Left: LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } pop } comparison ||
            descriptor.Kind != LlvmIntrinsicKind.CTPop || !ReferenceEquals(pop.GetArgument(0), value)) return false;
        var predicate = isTrue ? comparison.Predicate : LlvmCmpInstruction.GetInversePredicate(comparison.Predicate);
        return orZero && predicate == LlvmCmpPredicate.ICmpUlt && bound.Value == new LlvmAPInt(bound.Value.BitWidth, 2)
            || predicate == LlvmCmpPredicate.ICmpEq && bound.IsOne;
    }

    private static bool IsKnownNonEqualFromDomConditions(LlvmValue left, LlvmValue right, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, LlvmDomConditionCache? conditions, LlvmCondContext? condContext)
    {
        if (conditions is null || context is null || tree is null) return false;
        var query = new LlvmInstructionSimplify.BinaryQuery(context.Parent!.Module!.DataLayout, null, metrics);
        return FromValue(left) || FromValue(right);

        bool FromValue(LlvmValue value)
        {
            foreach (var branch in conditions.ConditionsFor(value))
            {
                Count(metrics, "value_facts.nonequal.dom_conditions.visited");
                if (tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(0), context.Parent!) &&
                    LlvmInstructionSimplify.IsImpliedCondition(branch.Condition, LlvmCmpPredicate.ICmpNe,
                        left, right, query, true, depth) == true)
                { Count(metrics, "value_facts.nonequal.dom_conditions.true_edge"); return true; }
                if (tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(1), context.Parent!) &&
                    LlvmInstructionSimplify.IsImpliedCondition(branch.Condition, LlvmCmpPredicate.ICmpNe,
                        left, right, query, false, depth) == true)
                { Count(metrics, "value_facts.nonequal.dom_conditions.false_edge"); return true; }
            }
            return false;
        }
    }
}
