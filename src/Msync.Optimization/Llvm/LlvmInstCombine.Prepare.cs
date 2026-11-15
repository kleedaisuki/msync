// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp prepareWorklist.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        internal bool PrepareWorklist()
        {
            if (Trace is null) return PrepareWorklistCore();
            Dictionary<string, ulong> before = MetricsSnapshot();
            return Trace.MeasureDetailed("optimizer.llvm-instcombine.prepare-worklist", this,
                static work => work.PrepareWorklistCore(), attributes: Attributes(), metrics: _ => MetricsDifference(before));
        }
        private bool PrepareWorklistCore()
        {
            bool changed = false;
            HashSet<LlvmBasicBlock> liveBlocks = [];
            List<LlvmInstruction> initial = [];
            Dictionary<LlvmConstant, LlvmConstant>? foldedConstants = null;
            foreach (LlvmBasicBlock block in RPOT)
            {
                Count("prepare.blocks.visited");
                if (block != F.BasicBlocks[0] && LlvmDominatorTree.Predecessors(block).All(predecessor =>
                    DeadEdges.Contains((predecessor, block)) || DT.Dominates(block, predecessor)))
                { HandleOnlyLiveSuccessor(block, null); continue; }
                liveBlocks.Add(block);
                for (LlvmInstruction? instruction = block.FirstInstruction; instruction is not null;)
                {
                    LlvmInstruction current = instruction;
                    instruction = current.Next;
                    Count("prepare.instructions.visited");
                    if (!current.UseEmpty && (current.OperandCount == 0 || current.GetOperand(0) is LlvmConstant) &&
                        LlvmConstantFolding.ConstantFoldInstruction(current, DL, Metrics) is { } constant)
                    {
                        current.ReplaceAllUsesWith(constant);
                        Count("prepare.instructions.constant-folded");
                        if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(current, Metrics)) current.EraseFromParent();
                        changed = true;
                        continue;
                    }
                    foreach (LlvmUse operand in current.OperandUses)
                    {
                        if (operand.Value is not LlvmConstant expression || expression is not (LlvmConstantExpr or LlvmConstantVector)) continue;
                        foldedConstants ??= [];
                        if (!foldedConstants.TryGetValue(expression, out LlvmConstant? folded))
                        {
                            folded = LlvmConstantFolding.ConstantFoldConstant(expression, DL, Metrics);
                            foldedConstants.Add(expression, folded);
                            Count("prepare.constant-expressions.cache-misses");
                        }
                        else Count("prepare.constant-expressions.cache-hits");
                        if (folded == expression) continue;
                        operand.Set(folded);
                        Count("prepare.constant-expressions.operands-rewritten");
                        changed = true;
                    }
                    // Pseudo probes, alias-scope declarations and LLVM metadata
                    // graphs have no represented carriers.
                    initial.Add(current);
                }
                LlvmInstruction terminator = block.GetTerminator();
                if (terminator is LlvmCondBrInstruction branch)
                {
                    if (branch.Condition is LlvmUndefValue) { HandleOnlyLiveSuccessor(block, null); continue; }
                    if (branch.Condition is LlvmConstantInt condition)
                    { HandleOnlyLiveSuccessor(block, branch.GetSuccessor(condition.IsZero ? 1 : 0)); continue; }
                }
                else if (terminator is LlvmSwitchInstruction @switch)
                {
                    if (@switch.Condition is LlvmUndefValue) { HandleOnlyLiveSuccessor(block, null); continue; }
                    if (@switch.Condition is LlvmConstantInt condition)
                    {
                        LlvmBasicBlock live = @switch.DefaultDestination;
                        for (int index = 0; index < @switch.CaseCount; ++index)
                            if (@switch.GetCaseValue(index) == condition) { live = @switch.GetCaseDestination(index); break; }
                        HandleOnlyLiveSuccessor(block, live);
                    }
                }
            }
            foreach (LlvmBasicBlock block in F.BasicBlocks)
            {
                if (liveBlocks.Contains(block)) continue;
                uint erased = LlvmLocalCfg.RemoveAllNonTerminatorAndEHPadInstructions(block, Metrics);
                changed |= erased != 0;
                Count("prepare.dead-instructions", erased);
            }
            Worklist.Reserve(initial.Count);
            for (int index = initial.Count - 1; index >= 0; --index)
            {
                LlvmInstruction instruction = initial[index];
                if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
                { instruction.EraseFromParent(); changed = true; Count("prepare.dead-instructions"); continue; }
                Worklist.Push(instruction);
            }
            return changed;

            void HandleOnlyLiveSuccessor(LlvmBasicBlock block, LlvmBasicBlock? live)
            {
                foreach (LlvmBasicBlock successor in block.GetTerminator().Successors)
                {
                    if (successor == live || !DeadEdges.Add((block, successor))) continue;
                    Count("prepare.dead-edges");
                    for (LlvmInstruction? cursor = successor.FirstInstruction; cursor is LlvmPhiInstruction phi; cursor = cursor.Next)
                        for (int index = 0; index < phi.IncomingCount; ++index)
                            if (phi.GetIncomingBlock(index) == block && phi.GetIncomingValue(index) is not LlvmPoisonValue)
                            { phi.SetOperand(index, LlvmPoisonValue.Get(phi.Type)); changed = true; Count("prepare.phi-inputs-poisoned"); }
                }
            }
        }
        private Dictionary<string, string> Attributes() => new() { ["function.name"] = F.Name };
    }
}
