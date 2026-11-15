// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// computeSpeculationCost / dominatesMergePoint / validateAndCostRequiredSelects.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private LlvmInstructionCost ComputeSpeculationCost(LlvmUser instruction)
        {
            Count("speculation.cost_queries");
            return TargetInfo.GetInstructionCost(instruction, LlvmTargetCostKind.SizeAndLatency);
        }
        private bool DominatesMergePoint(LlvmValue value, LlvmBasicBlock block, LlvmInstruction insertPoint,
            HashSet<LlvmInstruction> aggressive, ref LlvmInstructionCost cost, LlvmInstructionCost budget,
            HashSet<LlvmInstruction> zeroCost, uint depth = 0)
        {
            Count("speculation.merge.visited");
            if (depth == Tuning.MaxSpeculationDepth)
            { Count("speculation.merge.depth_limit"); return false; }
            if (value is not LlvmInstruction instruction) return true;
            if (ReferenceEquals(instruction.Parent, block)) return false;
            if (instruction.Parent!.GetTerminator() is not LlvmUncondBrInstruction branch || !ReferenceEquals(branch.Destination, block)) return true;
            if (aggressive.Contains(instruction)) return true;
            if (!LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction, insertPoint, metrics: Metrics, assumptions: Options.AC))
            { Count("speculation.merge.not_speculatable"); return false; }
            // With-overflow intrinsics are not among represented intrinsic IDs.
            if (!zeroCost.Contains(instruction)) cost += ComputeSpeculationCost(instruction);
            if (cost > budget && (!Tuning.SpeculateOneExpensiveInst || aggressive.Count != 0 || depth > 0 || !cost.IsValid))
            { Count("speculation.merge.budget"); return false; }
            foreach (LlvmUse use in instruction.OperandUses)
                if (!DominatesMergePoint(use.Value!, block, insertPoint, aggressive, ref cost, budget, zeroCost, depth + 1)) return false;
            aggressive.Add(instruction);
            return true;
        }
        private bool ValidateAndCostRequiredSelects(LlvmBasicBlock block, LlvmBasicBlock thenBlock,
            LlvmBasicBlock endBlock, ref uint speculatedInstructions, ref LlvmInstructionCost cost)
        {
            LlvmTargetCostKind costKind = block.Parent!.HasMinSize
                ? LlvmTargetCostKind.CodeSize : LlvmTargetCostKind.SizeAndLatency;
            Count(costKind == LlvmTargetCostKind.CodeSize ? "speculation.select_cost.code_size" : "speculation.select_cost.size_latency");
            bool rewritable = false;
            for (var instruction = endBlock.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                Count("speculation.phi_cost_queries");
                LlvmValue original = phi.GetIncomingValue(phi.GetBasicBlockIndex(block));
                LlvmValue next = phi.GetIncomingValue(phi.GetBasicBlockIndex(thenBlock));
                if (ReferenceEquals(original, next)) continue;
                cost += TargetInfo.GetCmpSelInstrCost(LlvmOpcode.Select, phi.Type, LlvmType.GetInt1Ty(phi.Context),
                    costKind);
                if (PassingValueIsAlwaysUndefined(original, phi) || PassingValueIsAlwaysUndefined(next, phi)) return false;
                rewritable = true;
                // ConstantExpr is not a represented scalar constant class.
            }
            return rewritable;
        }
    }
}
