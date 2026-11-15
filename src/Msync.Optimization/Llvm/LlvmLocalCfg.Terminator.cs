// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/Local.cpp ConstantFoldTerminator and recursive dead-instruction deletion.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocalCfg
{
    public static bool ConstantFoldTerminator(LlvmBasicBlock block, bool deleteDeadConditions = false,
        LlvmDomTreeUpdater? updater = null, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "local_cfg.fold_terminator.queries");
        switch (block.GetTerminator())
        {
            case LlvmCondBrInstruction branch:
                return FoldConditionalBranch(block, branch, deleteDeadConditions, updater, metrics);
            case LlvmSwitchInstruction instruction:
                return FoldSwitch(block, instruction, deleteDeadConditions, updater, metrics);
            default:
                return false;
        }
    }

    private static bool FoldConditionalBranch(LlvmBasicBlock block, LlvmCondBrInstruction branch,
        bool deleteDeadConditions, LlvmDomTreeUpdater? updater, Dictionary<string, ulong>? metrics)
    {
        var whenTrue = branch.GetSuccessor(0);
        var whenFalse = branch.GetSuccessor(1);
        if (ReferenceEquals(whenTrue, whenFalse))
        {
            LlvmBasicBlockUtils.RemovePredecessor(whenTrue, block, metrics: metrics);
            block.InsertBefore(new LlvmUncondBrInstruction(whenTrue) { Source = branch.Source }, branch);
            var condition = branch.Condition;
            branch.EraseFromParent();
            if (deleteDeadConditions) RecursivelyDeleteTriviallyDeadInstructions(condition, metrics: metrics);
            Count(metrics, "local_cfg.fold_terminator.duplicate_branch");
            return true;
        }
        if (branch.Condition is not LlvmConstantInt constant) return false;
        bool takeTrue = !constant.Value.IsZero;
        var destination = takeTrue ? whenTrue : whenFalse;
        var oldDestination = takeTrue ? whenFalse : whenTrue;
        LlvmBasicBlockUtils.RemovePredecessor(oldDestination, block, metrics: metrics);
        block.InsertBefore(new LlvmUncondBrInstruction(destination) { Source = branch.Source }, branch);
        branch.EraseFromParent();
        updater?.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, block, oldDestination)]);
        Count(metrics, "local_cfg.fold_terminator.constant_branch");
        return true;
    }

    private static bool FoldSwitch(LlvmBasicBlock block, LlvmSwitchInstruction instruction,
        bool deleteDeadConditions, LlvmDomTreeUpdater? updater, Dictionary<string, ulong>? metrics)
    {
        var constant = instruction.Condition as LlvmConstantInt;
        var defaultDestination = instruction.DefaultDestination;
        LlvmBasicBlock? onlyDestination = defaultDestination;
        if (FirstNonPhi(defaultDestination) is LlvmUnreachableInstruction && instruction.CaseCount > 0)
            onlyDestination = instruction.GetCaseDestination(0);
        bool changed = false;
        for (int index = 0; index < instruction.CaseCount;)
        {
            var destination = instruction.GetCaseDestination(index);
            if (ReferenceEquals(instruction.GetCaseValue(index), constant))
            {
                onlyDestination = destination;
                break;
            }
            if (ReferenceEquals(destination, defaultDestination))
            {
                LlvmBasicBlockUtils.RemovePredecessor(defaultDestination, block, metrics: metrics);
                instruction.RemoveCase(index);
                if (instruction.Condition is LlvmConstantInt newConstant)
                {
                    constant = newConstant;
                    index = 0;
                }
                changed = true;
                Count(metrics, "local_cfg.fold_terminator.default_cases_removed");
                continue;
            }
            if (!ReferenceEquals(destination, onlyDestination)) onlyDestination = null;
            ++index;
        }
        if (constant is not null && onlyDestination is null) onlyDestination = instruction.DefaultDestination;
        if (onlyDestination is not null)
        {
            block.InsertBefore(new LlvmUncondBrInstruction(onlyDestination) { Source = instruction.Source }, instruction);
            HashSet<LlvmBasicBlock>? removed = updater is null ? null : [];
            LlvmBasicBlock? successorToKeep = onlyDestination;
            foreach (var successor in instruction.Successors)
            {
                if (!ReferenceEquals(successor, onlyDestination)) removed?.Add(successor);
                if (ReferenceEquals(successor, successorToKeep)) successorToKeep = null;
                else LlvmBasicBlockUtils.RemovePredecessor(successor, block, metrics: metrics);
            }
            var condition = instruction.Condition;
            instruction.EraseFromParent();
            if (deleteDeadConditions) RecursivelyDeleteTriviallyDeadInstructions(condition, metrics: metrics);
            if (updater is not null)
                updater.ApplyUpdates(removed!.Select(successor => new LlvmCfgUpdate(LlvmCfgUpdateKind.Delete, block, successor)).ToArray());
            Count(metrics, "local_cfg.fold_terminator.single_destination_switch");
            return true;
        }
        if (instruction.CaseCount != 1) return changed;
        var left = instruction.Condition;
        var right = instruction.GetCaseValue(0);
        LlvmValue comparison;
        if (left is LlvmConstant leftConstant &&
            LlvmConstantFolding.ConstantFoldCompareInstruction(LlvmCmpPredicate.ICmpEq, leftConstant, right) is { } folded)
            comparison = folded;
        else
        {
            var compare = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, left, right) { Source = instruction.Source };
            compare.SetName("cond");
            block.InsertBefore(compare, instruction);
            comparison = compare;
        }
        block.InsertBefore(new LlvmCondBrInstruction(comparison, instruction.GetCaseDestination(0), instruction.DefaultDestination)
        { Source = instruction.Source }, instruction);
        instruction.EraseFromParent();
        Count(metrics, "local_cfg.fold_terminator.single_case_switch");
        return true;
    }

    public static bool RecursivelyDeleteTriviallyDeadInstructions(LlvmValue value,
        LlvmMemorySsaUpdater? memoryUpdater = null, Action<LlvmValue>? aboutToDelete = null,
        Dictionary<string, ulong>? metrics = null)
    {
        if (value is not LlvmInstruction instruction || !LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, metrics))
            return false;
        List<LlvmInstruction> dead = [instruction];
        DeleteTriviallyDeadInstructions(dead, memoryUpdater, aboutToDelete, metrics);
        return true;
    }

    public static void DeleteTriviallyDeadInstructions(List<LlvmInstruction> dead,
        LlvmMemorySsaUpdater? memoryUpdater = null, Action<LlvmValue>? aboutToDelete = null,
        Dictionary<string, ulong>? metrics = null)
    {
        while (dead.Count != 0)
        {
            var instruction = dead[^1];
            dead.RemoveAt(dead.Count - 1);
            // No RAUW occurs in this routine. Parent=null is the erased entry
            // of the source WeakTrackingVH worklist, including duplicate entries.
            if (instruction.Parent is null) continue;
            Debug.Assert(LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction));
            Debug.Assert(instruction.UseEmpty);
            aboutToDelete?.Invoke(instruction);
            foreach (var use in instruction.OperandUses)
            {
                var operand = use.Value!;
                use.Set(null);
                if (!operand.UseEmpty) continue;
                if (operand is LlvmInstruction producer && LlvmDeadCodeElimination.IsInstructionTriviallyDead(producer, metrics))
                    dead.Add(producer);
            }
            memoryUpdater?.RemoveMemoryAccess(instruction);
            instruction.EraseFromParent();
            Count(metrics, "local_cfg.dead_instructions_removed");
        }
    }

    private static IEnumerable<LlvmPhiInstruction> Phis(LlvmBasicBlock block)
    {
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            yield return phi;
    }

    private static LlvmInstruction FirstNonPhi(LlvmBasicBlock block)
    {
        var instruction = block.FirstInstruction!;
        while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
        return instruction;
    }

    private static void Count(Dictionary<string, ulong>? metrics, string name, ulong amount = 1)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + amount;
    }
}
