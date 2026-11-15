// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isGuaranteedNotToBeUndefOrPoison(UndefOnly), canCreateUndefOrPoison,
// programUndefinedIfUndefOrPoison(false), handleGuaranteedWellDefinedOps.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownFPClasses
{
    public static bool IsGuaranteedNotToBeUndef(LlvmValue value, LlvmInstruction? contextInstruction = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, int depth = 0, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null) =>
        LlvmValueTracking.IsGuaranteedNotToBeUndef(value, tree, contextInstruction, metrics, (uint)depth, assumptions, allowEphemerals, conditions);
}

public static partial class LlvmValueTracking
{
    public static bool IsGuaranteedNotToBeUndef(LlvmValue value, LlvmDominatorTree? tree = null,
        LlvmInstruction? contextInstruction = null, Dictionary<string, ulong>? metrics = null, uint depth = 0, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        Count(metrics, "value_tracking.undef.visited");
        if (depth >= MaxAnalysisRecursionDepth)
        { Count(metrics, "value_tracking.undef.depth_limit"); return false; }
        if (value is LlvmConstant constant)
        {
            if (constant is LlvmPoisonValue) return true;
            if (constant is LlvmUndefValue) return false;
            if (constant is LlvmConstantInt or LlvmGlobalVariable or LlvmConstantFP or
                LlvmConstantPointerNull or LlvmFunction or LlvmConstantData or LlvmConstantAggregateZero) return true;
            if (constant is LlvmConstantAggregate aggregate)
            {
                foreach (LlvmUse use in aggregate.OperandUses)
                    if (!IsGuaranteedNotToBeUndef(use.Value!, tree, contextInstruction, metrics, depth + 1, assumptions, allowEphemerals, conditions)) return false;
                return true;
            }
        }
        if (value is LlvmHeapAllocInst)
        { Count(metrics, "value_tracking.undef.heap_result"); return true; }
        if (StripPointerCastsSameRepresentation(value, metrics) is LlvmAllocaInstruction or LlvmGlobalVariable or LlvmFunction or LlvmConstantPointerNull) return true;
        if (value is LlvmInstruction instruction)
        {
            if (instruction is LlvmFreezeInstruction) return true;
            if (!CanCreateUndef(instruction))
            {
                bool defined = true;
                if (instruction is LlvmPhiInstruction phi)
                {
                    for (int n = 0; n < phi.IncomingCount; ++n)
                    {
                        LlvmValue incoming = phi.GetIncomingValue(n);
                        if (ReferenceEquals(phi, incoming)) continue;
                        if (IsGuaranteedNotToBeUndef(incoming, tree, phi.GetIncomingBlock(n).GetTerminator(), metrics, depth + 1, assumptions, allowEphemerals, conditions)) continue;
                        defined = false;
                        break;
                    }
                }
                else
                    foreach (LlvmUse use in instruction.OperandUses)
                    {
                        if (IsGuaranteedNotToBeUndef(use.Value!, tree, contextInstruction, metrics, depth + 1, assumptions, allowEphemerals, conditions)) continue;
                        defined = false;
                        break;
                    }
                if (defined) { Count(metrics, "value_tracking.undef.operands"); return true; }
            }
        }
        if (ProgramUndefinedIfUndef(value, metrics)) return true;
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
            if (ReferenceEquals(condition, value)) return true;
        }
        return HasNoUndefKnowledge(value, contextInstruction, tree, metrics, assumptions, allowEphemerals, conditions);
    }


    private static bool ProgramUndefinedIfUndef(LlvmValue value, Dictionary<string, ulong>? metrics)
    {
        LlvmInstruction? begin = value switch
        {
            LlvmInstruction instruction => instruction.Next,
            LlvmArgument { Parent: { IsDeclaration: false } parent } => parent.BasicBlocks[0].FirstInstruction,
            _ => null
        };
        uint limit = 32;
        for (var current = begin; current is not null && --limit != 0; current = current.Next)
        {
            Count(metrics, "value_tracking.undef.ub_scan_instructions");
            LlvmValue? defined = current switch
            {
                LlvmStoreInstruction store => store.PointerOperand,
                LlvmLoadInstruction load => load.PointerOperand,
                LlvmCallBase indirectCall when indirectCall.CalledFunction is null => indirectCall.CalledOperand,
                LlvmSwitchInstruction dispatch => dispatch.Condition,
                LlvmCondBrInstruction branch => branch.Condition,
                _ => null
            };
            if (ReferenceEquals(defined, value)) return true;
            if (current is LlvmCallBase call)
                for (int arg = 0; arg < call.ArgumentCount; arg++)
                    if (ReferenceEquals(call.GetArgument(arg), value) && call.IsPassingUndefUB(arg)) return true;
            if (!IsGuaranteedToTransferExecutionToSuccessor(current)) break;
        }
        return false;
    }
}
