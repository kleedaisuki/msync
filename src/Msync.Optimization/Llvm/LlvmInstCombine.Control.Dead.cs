// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp unreachable visitors and dead-edge propagation.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool RemoveInstructionsBeforeUnreachable(LlvmInstruction instruction)
        {
            bool changed = false;
            while (instruction.Previous is { } previous)
            {
                if (previous.IsEHPad || !LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(previous, Metrics)) break;
                ReplaceInstUsesWith(previous, LlvmPoisonValue.Get(previous.Type));
                EraseInstFromFunction(previous);
                Count("control.unreachable.prefix-erased");
                changed = true;
            }
            return changed;
        }

        private LlvmInstruction? VisitUnreachableInst(LlvmUnreachableInstruction instruction)
        {
            RemoveInstructionsBeforeUnreachable(instruction);
            return null;
        }

        private LlvmInstruction? VisitUncondBrInst(LlvmUncondBrInstruction branch)
        {
            // Pseudo probes/debug records have no represented instruction class.
            if (branch.Previous is LlvmStoreInstruction store && MergeStoreIntoSuccessor(store)) return branch;
            return null;
        }

        private void AddDeadEdge(LlvmBasicBlock from, LlvmBasicBlock to, List<LlvmBasicBlock> worklist)
        {
            if (!DeadEdges.Add((from, to))) return;
            Count("control.dead-edges.added");
            for (LlvmInstruction? instruction = to.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
                foreach (LlvmUse use in phi.OperandUses)
                {
                    if (phi.GetIncomingBlock(use.OperandNo) != from || use.Value is LlvmPoisonValue) continue;
                    ReplaceUse(use, LlvmPoisonValue.Get(phi.Type));
                    Worklist.Push(phi);
                    MadeIRChange = true;
                    Count("control.dead-edges.phi-operands-poisoned");
                }
            worklist.Add(to);
        }

        private void HandleUnreachableFrom(LlvmInstruction first, List<LlvmBasicBlock> worklist)
        {
            LlvmBasicBlock block = first.Parent!;
            LlvmInstruction? before = first.Previous;
            for (LlvmInstruction? instruction = block.GetTerminator().Previous; instruction != before;)
            {
                LlvmInstruction current = instruction!;
                instruction = current.Previous;
                if (!current.UseEmpty && !current.Type.IsTokenTy())
                {
                    ReplaceInstUsesWith(current, LlvmPoisonValue.Get(current.Type));
                    MadeIRChange = true;
                }
                if (current.IsEHPad || current.Type.IsTokenTy()) continue;
                EraseInstFromFunction(current);
                MadeIRChange = true;
                Count("control.unreachable.suffix-erased");
            }
            List<LlvmValue> changed = [];
            if (LlvmLocalCfg.HandleUnreachableTerminator(block.GetTerminator(), changed, Metrics))
            {
                MadeIRChange = true;
                foreach (LlvmValue value in changed) Worklist.Push((LlvmInstruction)value);
            }
            foreach (LlvmBasicBlock successor in block.GetTerminator().Successors) AddDeadEdge(block, successor, worklist);
        }

        private void HandlePotentiallyDeadBlocks(List<LlvmBasicBlock> worklist)
        {
            while (worklist.Count != 0)
            {
                LlvmBasicBlock block = worklist[^1];
                worklist.RemoveAt(worklist.Count - 1);
                Count("control.dead-blocks.examined");
                if (LlvmDominatorTree.Predecessors(block).Any(predecessor =>
                    !DeadEdges.Contains((predecessor, block)) && !DT.Dominates(block, predecessor))) continue;
                HandleUnreachableFrom(block.FirstInstruction!, worklist);
            }
        }

        private void HandlePotentiallyDeadSuccessors(LlvmBasicBlock block, LlvmBasicBlock? liveSuccessor)
        {
            List<LlvmBasicBlock> worklist = [];
            foreach (LlvmBasicBlock successor in block.GetTerminator().Successors)
                if (successor != liveSuccessor) AddDeadEdge(block, successor, worklist);
            HandlePotentiallyDeadBlocks(worklist);
        }
    }
}
