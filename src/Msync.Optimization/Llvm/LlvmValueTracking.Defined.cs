// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// isGuaranteedNotToBeUndefOrPoison, UndefPoisonKind::UndefOrPoison.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsGuaranteedNotToBeUndefOrPoison(LlvmValue value, LlvmDominatorTree? tree = null,
        LlvmInstruction? contextInstruction = null, Dictionary<string, ulong>? metrics = null, uint depth = 0,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        Count(metrics, "value_tracking.defined.visited");
        if (depth >= MaxAnalysisRecursionDepth)
        { Count(metrics, "value_tracking.defined.depth_limit"); return false; }
        if (value is LlvmConstant constant)
        {
            if (constant is LlvmPoisonValue) return false;
            if (constant is LlvmUndefValue) return false;
            if (constant is LlvmConstantInt or LlvmGlobalVariable or LlvmConstantFP or
                LlvmConstantPointerNull or LlvmFunction or LlvmConstantData or LlvmConstantAggregateZero) return true;
            if (constant is LlvmConstantAggregate aggregate)
            {
                foreach (LlvmUse use in aggregate.OperandUses)
                    if (!IsGuaranteedNotToBeUndefOrPoison(use.Value!, tree, contextInstruction, metrics, depth + 1, assumptions, allowEphemerals, conditions)) return false;
                return true;
            }
        }
        if (value is LlvmHeapAllocInst)
        { Count(metrics, "value_tracking.defined.heap_result"); return true; }
        if (StripPointerCastsSameRepresentation(value, metrics) is LlvmAllocaInstruction or LlvmGlobalVariable or LlvmFunction or LlvmConstantPointerNull) return true;
        if (value is LlvmInstruction instruction)
        {
            if (instruction is LlvmFreezeInstruction) return true;
            if (!CanCreatePoison(instruction))
            {
                bool defined = true;
                if (instruction is LlvmPhiInstruction phi)
                {
                    for (int n = 0; n < phi.IncomingCount; ++n)
                    {
                        LlvmValue incoming = phi.GetIncomingValue(n);
                        if (ReferenceEquals(phi, incoming)) continue;
                        if (IsGuaranteedNotToBeUndefOrPoison(incoming, tree, phi.GetIncomingBlock(n).GetTerminator(), metrics, depth + 1, assumptions, allowEphemerals, conditions)) continue;
                        defined = false;
                        break;
                    }
                }
                else
                    foreach (LlvmUse use in instruction.OperandUses)
                    {
                        if (IsGuaranteedNotToBeUndefOrPoison(use.Value!, tree, contextInstruction, metrics, depth + 1, assumptions, allowEphemerals, conditions)) continue;
                        defined = false;
                        break;
                    }
                if (defined) { Count(metrics, "value_tracking.defined.operands"); return true; }
            }
        }
        if (ProgramUndefinedIfUndef(value, metrics))
        { Count(metrics, "value_tracking.defined.direct_ub_use"); return true; }
        if (contextInstruction?.Parent is null || tree is null) return false;
        var contextNode = tree.GetNode(contextInstruction.Parent);
        if (contextNode is null) return false;
        if (value.Type.IsIntegerTy())
            for (var node = contextNode.IDom; node is not null; node = node.IDom)
            {
                LlvmValue? condition = node.Block?.GetTerminatorOrNull() switch
                {
                    LlvmCondBrInstruction branch => branch.Condition,
                    LlvmSwitchInstruction dispatch => dispatch.Condition,
                    _ => null
                };
                Count(metrics, "value_tracking.defined.idom_steps");
                if (ReferenceEquals(condition, value)) return true;
            }
        return HasNoUndefKnowledge(value, contextInstruction, tree, metrics, assumptions, false);
    }
}
