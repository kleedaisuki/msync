// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// simplifyTerminatorOnSelect, simplifySwitchOnSelectRemap/simplifySwitchOnSelect.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool SimplifyTerminatorOnSelect(LlvmInstruction oldTerm, LlvmValue condition,
            LlvmBasicBlock whenTrue, LlvmBasicBlock whenFalse, uint trueWeight = 0, uint falseWeight = 0)
        {
            Count("conditional.select_terminator.queries");
            var block = oldTerm.Parent!;
            LlvmBasicBlock? keepFirst = whenTrue, keepSecond = whenTrue != whenFalse ? whenFalse : null;
            HashSet<LlvmBasicBlock> removed = new(ReferenceEqualityComparer.Instance);
            foreach (var successor in oldTerm.Successors.ToArray())
            {
                if (successor == keepFirst) keepFirst = null;
                else if (successor == keepSecond) keepSecond = null;
                else
                {
                    LlvmBasicBlockUtils.RemovePredecessor(successor, block, keepOneInputPhis: true, metrics: Metrics);
                    if (successor != whenTrue && successor != whenFalse) removed.Add(successor);
                }
            }
            LlvmInstruction replacement;
            if (keepFirst is null && keepSecond is null)
                replacement = whenTrue == whenFalse ? new LlvmUncondBrInstruction(whenTrue) : new LlvmCondBrInstruction(condition, whenTrue, whenFalse);
            else if (keepFirst is not null && (keepSecond is not null || whenTrue == whenFalse))
                replacement = new LlvmUnreachableInstruction(block.Context);
            else replacement = new LlvmUncondBrInstruction(keepFirst is null ? whenTrue : whenFalse);
            replacement.Source = oldTerm.Source;
            block.InsertBefore(replacement, oldTerm);
            EraseTerminatorAndDceCondition(oldTerm);
            if (DominatorUpdater is not null)
            {
                List<LlvmCfgUpdate> updates = [];
                foreach (var successor in removed) updates.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
                DominatorUpdater.ApplyUpdates(updates);
            }
            Count("conditional.select_terminator.rewritten");
            return true;
        }

        private bool SimplifySwitchOnSelectRemap(LlvmSwitchInstruction instruction, LlvmSelectInstruction select,
            LlvmValue value, LlvmConstantInt constant, bool negate)
        {
            var whenTrue = negate ? select.FalseValue : select.TrueValue;
            var whenFalse = negate ? select.TrueValue : select.FalseValue;
            if (whenFalse != value || whenTrue is not LlvmConstantInt replacement) return false;
            Count("conditional.switch_select_remap.queries");
            var fork = ConditionalSwitchDestination(instruction, replacement);
            int caseIndex = instruction.FindCaseValue(constant);
            bool isDefault = caseIndex < 0;
            var oldDestination = isDefault ? instruction.DefaultDestination : instruction.GetCaseDestination(caseIndex);
            var block = instruction.Parent!;
            instruction.Condition = value;
            if (oldDestination != fork)
            {
                if (!isDefault) LlvmBasicBlockUtils.RemovePredecessor(oldDestination, block, metrics: Metrics);
                if (isDefault) instruction.AddCase(constant, fork);
                else instruction.SetSuccessor(caseIndex + 1, fork);
                AddPredecessorToBlock(fork, block, block);
                if (!isDefault && !instruction.Successors.Contains(oldDestination))
                    DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, block, oldDestination)]);
                // Branch-weight metadata is not represented; source no-profile
                // path leaves no profile payload to update or invalidate.
            }
            LlvmLocalCfg.RecursivelyDeleteTriviallyDeadInstructions(select, metrics: Metrics);
            Count("conditional.switch_select_remap.rewritten");
            return true;
        }

        private bool SimplifySwitchOnSelect(LlvmSwitchInstruction instruction, LlvmSelectInstruction select)
        {
            if (select.HasOneUse && select.Condition is LlvmICmpInstruction compare &&
                compare.Right is LlvmConstantInt constant && compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe &&
                SimplifySwitchOnSelectRemap(instruction, select, compare.Left, constant, compare.Predicate == LlvmCmpPredicate.ICmpNe)) return true;
            if (select.TrueValue is not LlvmConstantInt whenTrue || select.FalseValue is not LlvmConstantInt whenFalse) return false;
            return SimplifyTerminatorOnSelect(instruction, select.Condition,
                ConditionalSwitchDestination(instruction, whenTrue), ConditionalSwitchDestination(instruction, whenFalse));
        }

        private static LlvmBasicBlock ConditionalSwitchDestination(LlvmSwitchInstruction instruction, LlvmConstantInt value)
        {
            int index = instruction.FindCaseValue(value);
            return index < 0 ? instruction.DefaultDestination : instruction.GetCaseDestination(index);
        }
    }
}
