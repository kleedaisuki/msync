// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// directlyImpliesPoison and impliesPoison (scalar represented instructions).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool ImpliesPoison(LlvmValue valueAssumedPoison, LlvmValue value,
        Dictionary<string, ulong>? metrics = null) => ImpliesPoison(valueAssumedPoison, value, 0, metrics);

    private static bool ImpliesPoison(LlvmValue valueAssumedPoison, LlvmValue value, uint depth,
        Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "value_tracking.implies_poison.visits");
        if (IsGuaranteedNotToBePoison(valueAssumedPoison, metrics: metrics))
        { Count(metrics, "value_tracking.implies_poison.non_poison_premise"); return true; }
        if (DirectlyImpliesPoison(valueAssumedPoison, value, 0, metrics)) return true;
        if (depth >= 2)
        { Count(metrics, "value_tracking.implies_poison.depth_limit"); return false; }
        if (valueAssumedPoison is not LlvmInstruction instruction || CanCreatePoison(instruction))
        { Count(metrics, "value_tracking.implies_poison.premise_may_create_poison"); return false; }
        foreach (LlvmUse operand in instruction.OperandUses)
        {
            Count(metrics, "value_tracking.implies_poison.premise_operand");
            if (!ImpliesPoison(operand.Value!, value, depth + 1, metrics)) return false;
        }
        Count(metrics, "value_tracking.implies_poison.all_premise_operands");
        return true;
    }

    private static bool DirectlyImpliesPoison(LlvmValue valueAssumedPoison, LlvmValue value, uint depth,
        Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "value_tracking.implies_poison.direct_visits");
        if (ReferenceEquals(valueAssumedPoison, value))
        { Count(metrics, "value_tracking.implies_poison.identity"); return true; }
        if (depth >= 2)
        { Count(metrics, "value_tracking.implies_poison.direct_depth_limit"); return false; }
        if (value is not LlvmInstruction instruction) return false;
        foreach (LlvmUse operand in instruction.OperandUses)
        {
            if (!PropagatesPoison(operand)) continue;
            Count(metrics, "value_tracking.implies_poison.propagating_operand");
            if (DirectlyImpliesPoison(valueAssumedPoison, operand.Value!, depth + 1, metrics)) return true;
        }
        // The source WithOverflowInst/extractvalue shortcut requires intrinsic
        // IDs, which are not represented in this IR; no call-name inference.
        return false;
    }
}
