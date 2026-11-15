// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// isProfitableToSpeculate / SimplifyCFGOpt::speculativelyExecuteBB.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool IsProfitableToSpeculate(LlvmCondBrInstruction branch, bool? invert)
        {
            // No !prof / !unpredictable metadata is attached to these branch
            // carriers. This is the source's unprofiled early-success path.
            Count("speculation.unprofiled_branch");
            return true;
        }

        private bool SpeculativelyExecuteBB(LlvmCondBrInstruction branch, LlvmBasicBlock thenBlock)
        {
            if (Trace is null) return SpeculativelyExecuteCore(branch, thenBlock);
            return Trace.MeasureDetailed("optimizer.llvm-simplifycfg.speculate-block",
                (Work: this, Branch: branch, Then: thenBlock), static state => state.Work.SpeculativelyExecuteCore(state.Branch, state.Then),
                attributes: Attributes(branch), metrics: _ => Metrics!);
        }
        private bool SpeculativelyExecuteCore(LlvmCondBrInstruction branch, LlvmBasicBlock thenBlock)
        {
            if (!Options.SpeculateBlocks) return false;
            LlvmBasicBlock block = branch.Parent!;
            LlvmBasicBlock endBlock = thenBlock.GetTerminator().GetSuccessor(0);
            LlvmInstructionCost budget = new(Tuning.PhiNodeFoldingThreshold);
            bool invert = !ReferenceEquals(thenBlock, branch.GetSuccessor(0));
            if (!IsProfitableToSpeculate(branch, invert)) return false;
            Dictionary<LlvmInstruction, uint> sinkUses = [];
            List<LlvmInstruction> conditionalAccesses = [];
            uint speculated = 0;
            LlvmValue? storeValue = null;
            LlvmStoreInstruction? store = null;
            LlvmInstruction? previousStoreAccess = null;
            EphemeralValueTracker ephemeral = new(Metrics);
            for (LlvmInstruction? instruction = thenBlock.GetTerminator().Previous; instruction is not null; instruction = instruction.Previous)
            {
                Count("speculation.candidate_instructions");
                if (ephemeral.Track(instruction)) continue;
                bool conditional = Options.HoistLoadsStoresWithCondFaulting && IsSafeCheapLoadStore(instruction) &&
                    conditionalAccesses.Count < Tuning.HoistLoadsStoresWithCondFaultingThreshold;
                if (conditional) conditionalAccesses.Add(instruction);
                else ++speculated;
                if (speculated > 1) { Count("speculation.multiple_instructions"); return false; }
                if (!conditional && !LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction, branch, metrics: Metrics, assumptions: Options.AC))
                {
                    if (!Tuning.HoistCondStores || storeValue is not null ||
                        (storeValue = IsSafeToSpeculateStore(instruction, block, thenBlock, endBlock, ref previousStoreAccess)) is null)
                    { Count("speculation.not_speculatable"); return false; }
                }
                if (!conditional && storeValue is null && ComputeSpeculationCost(instruction) > budget)
                { Count("speculation.expensive_instruction"); return false; }
                if (store is null && storeValue is not null) store = (LlvmStoreInstruction)instruction;
                foreach (LlvmUse operand in instruction.OperandUses)
                {
                    if (operand.Value is not LlvmInstruction producer || !ReferenceEquals(producer.Parent, block) || producer.MayHaveSideEffects()) continue;
                    sinkUses[producer] = sinkUses.GetValueOrDefault(producer) + 1;
                }
            }
            foreach (var candidate in sinkUses)
                if (candidate.Key.HasNUses((int)candidate.Value) && ++speculated > 1)
                { Count("speculation.prevents_sinking"); return false; }
            bool convert = store is not null || conditionalAccesses.Count != 0;
            LlvmInstructionCost cost = new(0);
            convert |= ValidateAndCostRequiredSelects(block, thenBlock, endBlock, ref speculated, ref cost);
            if (!convert || cost > budget) { Count("speculation.select_budget"); return false; }
            LlvmInstruction? storeSelect = null;
            LlvmValue condition = branch.Condition;
            if (storeValue is not null)
            {
                LlvmValue whenTrue = store!.ValueOperand, whenFalse = storeValue;
                if (invert) (whenTrue, whenFalse) = (whenFalse, whenTrue);
                var select = new LlvmSelectInstruction(condition, whenTrue, whenFalse) { Source = branch.Source };
                select.SetName("spec.store.select");
                block.InsertBefore(select, branch);
                storeSelect = select;
                store.ValueOperand = select;
                Count("speculation.stores_selected");
            }
            // LLVM UB-implying call-site attributes / metadata, debug records
            // and pseudo probes are absent. SourceSpan is project diagnostics.
            for (LlvmInstruction? instruction = thenBlock.FirstInstruction; instruction is not null;)
            {
                LlvmInstruction? next = instruction.Next;
                if (ephemeral.Contains(instruction))
                {
                    instruction.ReplaceAllUsesWith(LlvmPoisonValue.Get(instruction.Type));
                    instruction.EraseFromParent();
                    Count("speculation.ephemerals_removed");
                }
                instruction = next;
            }
            for (LlvmInstruction? instruction = thenBlock.FirstInstruction; instruction is not null && !instruction.IsTerminator;)
            {
                LlvmInstruction? next = instruction.Next;
                instruction.RemoveFromParent();
                block.InsertBefore(instruction, branch);
                Count("speculation.instructions_hoisted");
                instruction = next;
            }
            if (conditionalAccesses.Count != 0) HoistConditionalLoadsStores(branch, conditionalAccesses, invert, storeSelect);
            for (LlvmInstruction? instruction = endBlock.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                int originalIndex = phi.GetBasicBlockIndex(block), thenIndex = phi.GetBasicBlockIndex(thenBlock);
                LlvmValue original = phi.GetIncomingValue(originalIndex), thenValue = phi.GetIncomingValue(thenIndex);
                if (ReferenceEquals(original, thenValue)) continue;
                LlvmValue whenTrue = thenValue, whenFalse = original;
                if (invert) (whenTrue, whenFalse) = (whenFalse, whenTrue);
                var select = new LlvmSelectInstruction(condition, whenTrue, whenFalse)
                { FastMathFlags = phi.Type.IsFPOrFPVectorTy() ? phi.FastMathFlags : LlvmFastMathFlags.None, Source = branch.Source };
                select.SetName("spec.select");
                block.InsertBefore(select, branch);
                phi.SetIncomingValue(originalIndex, select); phi.SetIncomingValue(thenIndex, select);
                Count("speculation.phis_selected");
            }
            Count("speculations");
            return true;
        }
    }
}
