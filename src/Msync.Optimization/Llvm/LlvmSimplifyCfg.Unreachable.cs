// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFGOpt::simplifyUnreachable.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool SimplifyUnreachable(LlvmUnreachableInstruction unreachable)
        {
            LlvmBasicBlock block = unreachable.Parent!;
            bool changed = false;
            while (unreachable.Previous is { } previous)
            {
                if (!LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(previous, Metrics)) break;
                previous.ReplaceAllUsesWith(LlvmPoisonValue.Get(previous.Type));
                previous.EraseFromParent();
                Count("unreachable.instructions-erased");
                changed = true;
            }
            if (block.FirstInstruction != unreachable) return changed;
            List<LlvmCfgUpdate> updates = [];
            List<LlvmBasicBlock> predecessors = [];
            HashSet<LlvmBasicBlock> seen = [];
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block))
                if (seen.Add(predecessor)) predecessors.Add(predecessor);
            foreach (LlvmBasicBlock predecessor in predecessors)
            {
                LlvmInstruction terminator = predecessor.GetTerminator();
                var builder = new LlvmTransformBuilder(terminator, Metrics) { CurrentSource = terminator.Source };
                if (terminator is LlvmUncondBrInstruction)
                {
                    builder.CreateUnreachable();
                    terminator.EraseFromParent();
                    changed = true;
                    updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
                }
                else if (terminator is LlvmCondBrInstruction branch)
                {
                    if (branch.GetSuccessor(0) == branch.GetSuccessor(1))
                    { builder.CreateUnreachable(); terminator.EraseFromParent(); }
                    else
                    {
                        CreateAssumption(builder, branch.GetSuccessor(0) == block ? builder.CreateNot(branch.Condition) : branch.Condition);
                        builder.CreateBr(branch.GetSuccessor(0) == block ? branch.GetSuccessor(1) : branch.GetSuccessor(0));
                        EraseTerminatorAndDceCondition(branch);
                    }
                    changed = true;
                    updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
                }
                else if (terminator is LlvmSwitchInstruction @switch)
                {
                    for (int index = 0; index < @switch.CaseCount;)
                    {
                        if (@switch.GetCaseDestination(index) != block) { ++index; continue; }
                        RemovePredecessor(block, predecessor);
                        @switch.RemoveCase(index);
                        Count("unreachable.switch-cases-erased");
                        changed = true;
                    }
                    if (@switch.DefaultDestination != block) updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
                }
            }
            DominatorUpdater?.ApplyUpdates(updates);
            if (!LlvmDominatorTree.Predecessors(block).Any() && block != block.Parent!.BasicBlocks[0])
            {
                LlvmBasicBlockUtils.DeleteDeadBlock(block, DominatorUpdater, trace: Trace, metrics: Metrics);
                Count("unreachable.blocks-erased");
                return true;
            }
            return changed;
        }
    }
}
