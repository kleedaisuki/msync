// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueTracking.cpp isGuaranteedNotToBeUndefOrPoison (PoisonOnly),
// canCreateUndefOrPoison, shiftAmountKnownInRange; IR/Operator.cpp flags;
// IR/Value.cpp stripPointerCastsAndOffsets<PSK_ZeroIndicesSameRepresentation>.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    private const uint MaxAnalysisRecursionDepth = 6;

    public static bool IsGuaranteedNotToBePoison(LlvmValue value, LlvmDominatorTree? tree = null,
        LlvmInstruction? contextInstruction = null, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null) =>
        IsGuaranteedNotToBePoison(value, tree, contextInstruction, 0, metrics, assumptions, allowEphemerals, conditions);

    internal static bool IsGuaranteedNotToBePoison(LlvmValue value, LlvmDominatorTree? tree,
        LlvmInstruction? context, uint depth, Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        Count(metrics, "value_tracking.poison.visited");
        if (depth >= MaxAnalysisRecursionDepth)
        { Count(metrics, "value_tracking.poison.depth_limit"); return false; }
        // Arguments and CallBase currently have no attributes; no attributes are inferred.
        if (value is LlvmConstant constant)
        {
            if (constant is LlvmPoisonValue) return false;
            if (constant is LlvmUndefValue) return true;
            if (constant is LlvmConstantInt or LlvmGlobalVariable or LlvmConstantFP or
                LlvmConstantPointerNull or LlvmFunction or LlvmConstantData)
            { Count(metrics, "value_tracking.poison.constant"); return true; }
            if (constant is LlvmConstantAggregate aggregate)
            {
                foreach (LlvmUse operand in aggregate.OperandUses)
                    if (!IsGuaranteedNotToBePoison(operand.Value!, tree, context, depth + 1, metrics, assumptions, allowEphemerals, conditions)) return false;
                return true;
            }
            throw new NotSupportedException("ConstantExpr poison tracking has not been ported.");
        }
        LlvmValue stripped = StripPointerCastsSameRepresentation(value, metrics);
        if (stripped is LlvmHeapAllocInst)
        { Count(metrics, "value_tracking.poison.heap_result"); return true; }
        if (stripped is LlvmAllocaInstruction or LlvmGlobalVariable or LlvmFunction or LlvmConstantPointerNull)
        { Count(metrics, "value_tracking.poison.allocated_pointer"); return true; }
        if (value is LlvmInstruction instruction)
        {
            if (instruction is LlvmFreezeInstruction) return true;
            if (!CanCreatePoison(instruction))
            {
                bool wellDefined = true;
                if (instruction is LlvmPhiInstruction phi)
                {
                    for (int index = 0; index < phi.IncomingCount; ++index)
                    {
                        LlvmValue incoming = phi.GetIncomingValue(index);
                        if (ReferenceEquals(phi, incoming)) continue;
                        if (IsGuaranteedNotToBePoison(incoming, tree,
                            phi.GetIncomingBlock(index).GetTerminator(), depth + 1, metrics, assumptions, allowEphemerals, conditions)) continue;
                        wellDefined = false;
                        break;
                    }
                }
                else
                {
                    foreach (LlvmUse operand in instruction.OperandUses)
                    {
                        if (IsGuaranteedNotToBePoison(operand.Value!, tree, context, depth + 1, metrics, assumptions, allowEphemerals, conditions)) continue;
                        wellDefined = false;
                        break;
                    }
                }
                if (wellDefined) { Count(metrics, "value_tracking.poison.operands"); return true; }
            }
        }
        if (ProgramUndefinedIfPoison(value, metrics))
        { Count(metrics, "value_tracking.poison.must_trigger_ub"); return true; }
        if (context?.Parent is null || tree is null) return false;
        var node = tree.GetNode(context.Parent);
        if (node is null) return false;
        for (var dominator = node.IDom; dominator is not null; dominator = dominator.IDom)
        {
            Count(metrics, "value_tracking.poison.idom_steps");
            LlvmValue? condition = dominator.Block!.GetTerminatorOrNull() switch
            {
                LlvmCondBrInstruction branch => branch.Condition,
                LlvmSwitchInstruction dispatch => dispatch.Condition,
                _ => null
            };
            if (ReferenceEquals(condition, value)) return true;
            if (condition is not LlvmInstruction op) continue;
            foreach (LlvmUse operand in op.OperandUses)
                if (ReferenceEquals(operand.Value, value) && PropagatesPoison(operand)) return true;
        }
        return HasNoUndefKnowledge(value, context, tree, metrics, assumptions, allowEphemerals, conditions);
    }

    private static bool ShiftAmountKnownInRange(LlvmValue amount)
    {
        if (amount is not LlvmConstant constant) return false;
        if (amount.Type is LlvmFixedVectorType vector)
        {
            for (uint element = 0; element < vector.NumElements; ++element)
                if (constant.GetAggregateElement(element) is not LlvmConstantInt value ||
                    value.Value.GetLimitedValue() >= value.BitWidth) return false;
            return true;
        }
        if (amount.Type is LlvmScalableVectorType) return false;
        return amount is LlvmConstantInt integer && integer.Value.GetLimitedValue() < integer.BitWidth;
    }

    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
