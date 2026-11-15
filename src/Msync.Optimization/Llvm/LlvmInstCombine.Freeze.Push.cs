// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp pushFreezeToPreventPoisonFromPropagating / foldFreezeIntoRecurrence.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? PushFreezeToPreventPoisonFromPropagating(LlvmFreezeInstruction freeze)
        {
            if (freeze.Operand is not LlvmInstruction { HasOneUse: true } operand || operand is LlvmPhiInstruction) return null;
            if (LlvmValueTracking.CanCreateUndefOrPoison(operand, considerFlagsAndMetadata: false)) return null;
            LlvmValue? maybePoison = null;
            foreach (LlvmValue? value in operand.Operands)
            {
                Count("freeze.push.operands-examined");
                if (LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(value!, metrics: Metrics) || value == maybePoison) continue;
                if (maybePoison is not null) return null;
                maybePoison = value;
            }
            operand.DropPoisonGeneratingAnnotations();
            if (maybePoison is null) return operand;
            Builder.SetInsertPoint(operand);
            var frozen = Builder.CreateFreeze(maybePoison, maybePoison.Name + ".fr");
            LlvmUse originalUse = operand.OperandUses.First(use => use.Value == maybePoison);
            frozen.LogicalType = maybePoison.LogicalType >= 0 ? maybePoison.LogicalType : originalUse.LogicalType;
            frozen.GetOperandUse(0).LogicalType = originalUse.LogicalType;
            operand.ReplaceUsesOfWith(maybePoison, frozen);
            Count("freeze.pushed-through-operation");
            return operand;
        }

        private LlvmInstruction? FoldFreezeIntoRecurrence(LlvmFreezeInstruction freeze, LlvmPhiInstruction phi)
        {
            LlvmUse? start = null;
            List<LlvmValue> work = [];
            foreach (LlvmUse use in phi.OperandUses)
            {
                if (DT.Dominates(phi.Parent!, phi.GetIncomingBlock(use.OperandNo)))
                { work.Add(use.Value!); continue; }
                if (start is not null) return null;
                start = use;
            }
            if (start is null || work.Count == 0) return null;
            LlvmValue startValue = start.Value!;
            LlvmBasicBlock startBlock = phi.GetIncomingBlock(start.OperandNo);
            bool startNeedsFreeze = !LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(startValue, metrics: Metrics);
            if (startNeedsFreeze && startBlock.GetTerminator() == startValue) return null;
            HashSet<LlvmValue> visited = [];
            List<LlvmInstruction> dropFlags = [];
            while (work.Count != 0)
            {
                LlvmValue value = work[^1];
                work.RemoveAt(work.Count - 1);
                if (!visited.Add(value)) continue;
                Count("freeze.recurrence.values-examined");
                if (visited.Count > 32) { Count("freeze.recurrence.limit"); return null; }
                if (value == phi || LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(value, metrics: Metrics)) continue;
                if (value is not LlvmInstruction instruction ||
                    LlvmValueTracking.CanCreateUndefOrPoison(instruction, considerFlagsAndMetadata: false)) return null;
                dropFlags.Add(instruction);
                foreach (LlvmValue? argument in instruction.Operands) work.Add(argument!);
            }
            foreach (LlvmInstruction instruction in dropFlags) instruction.DropPoisonGeneratingAnnotations();
            if (startNeedsFreeze)
            {
                Builder.SetInsertPoint(startBlock.GetTerminator());
                var frozenStart = Builder.CreateFreeze(startValue, startValue.Name + ".fr");
                frozenStart.LogicalType = phi.LogicalType;
                frozenStart.GetOperandUse(0).LogicalType = start.LogicalType;
                ReplaceUse(start, frozenStart);
            }
            Count("freeze.recurrence.folded");
            return ReplaceInstUsesWith(freeze, phi);
        }
    }
}
