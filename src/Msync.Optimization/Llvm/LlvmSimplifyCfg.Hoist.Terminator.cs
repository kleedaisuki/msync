// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/SimplifyCFG.cpp hoistSuccIdenticalTerminatorToSwitchOrIf.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool HoistSuccessorIdenticalTerminator(LlvmInstruction terminator, LlvmInstruction first,
            IReadOnlyList<LlvmInstruction> others, IReadOnlyList<LlvmBasicBlock> uniqueSuccessors)
        {
            var branch = terminator as LlvmCondBrInstruction;
            LlvmBasicBlock parent = terminator.Parent!;
            LlvmBasicBlock firstBlock = first.Parent!;
            LlvmBasicBlock secondBlock = others[0].Parent!;
            if (branch is not null)
            {
                Debug.Assert(others.Count == 1);
                Debug.Assert(branch.GetSuccessor(0) == firstBlock && branch.GetSuccessor(1) == secondBlock);
            }
            // Invoke/CallBr instructions have no represented classes. Their
            // source EH legality branches are outside the represented CFG.
            foreach (LlvmBasicBlock successor in firstBlock.GetTerminator().Successors)
                for (LlvmInstruction? current = successor.FirstInstruction; current is LlvmPhiInstruction phi; current = current.Next)
                {
                    LlvmValue incoming = phi.GetIncomingValue(phi.GetBasicBlockIndex(firstBlock));
                    foreach (LlvmInstruction other in others)
                    {
                        LlvmValue otherIncoming = phi.GetIncomingValue(phi.GetBasicBlockIndex(other.Parent!));
                        if (incoming == otherIncoming) continue;
                        if (branch is null || PassingValueIsAlwaysUndefined(incoming, phi) ||
                            PassingValueIsAlwaysUndefined(otherIncoming, phi)) return HoistRetained("terminator-phi-values");
                    }
                }
            LlvmInstruction replacement = first.Clone();
            parent.InsertBefore(replacement, terminator);
            if (!replacement.Type.IsVoidTy())
            {
                first.ReplaceAllUsesWith(replacement);
                foreach (LlvmInstruction other in others) other.ReplaceAllUsesWith(replacement);
                replacement.TakeName(first);
            }
            Count("hoist.instructions", (ulong)others.Count + 1);
            Count("hoist.terminators");
            if (branch is not null) HoistTerminatorPhiSelects(branch, replacement, firstBlock, secondBlock);
            List<LlvmCfgUpdate> updates = [];
            HashSet<LlvmBasicBlock> visited = [];
            foreach (LlvmBasicBlock successor in firstBlock.GetTerminator().Successors)
            {
                AddPredecessorToBlock(successor, parent, firstBlock);
                if (DominatorUpdater is not null && visited.Add(successor))
                    updates.Add(new(LlvmCfgUpdateKind.Insert, parent, successor));
            }
            if (DominatorUpdater is not null)
                foreach (LlvmBasicBlock successor in uniqueSuccessors)
                    updates.Add(new(LlvmCfgUpdateKind.Delete, parent, successor));
            EraseTerminatorAndDceCondition(terminator);
            DominatorUpdater?.ApplyUpdates(updates);
            Count("hoist.dominator-updates", (ulong)updates.Count);
            return true;
        }

        private void AddPredecessorToBlock(LlvmBasicBlock successor, LlvmBasicBlock newPredecessor,
            LlvmBasicBlock existingPredecessor)
        {
            for (LlvmInstruction? instruction = successor.FirstInstruction;
                instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                int existing = phi.GetBasicBlockIndex(existingPredecessor);
                int logicalType = phi.GetOperandUse(existing).LogicalType;
                phi.AddIncoming(phi.GetIncomingValue(existing), newPredecessor);
                phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = logicalType;
                Count("cfg.phi-predecessors.added");
            }
        }

        private void HoistTerminatorPhiSelects(LlvmCondBrInstruction branch, LlvmInstruction replacement,
            LlvmBasicBlock firstBlock, LlvmBasicBlock secondBlock)
        {
            Dictionary<(LlvmValue, LlvmValue), LlvmSelectInstruction> inserted = [];
            foreach (LlvmBasicBlock successor in firstBlock.GetTerminator().Successors)
                for (LlvmInstruction? current = successor.FirstInstruction; current is LlvmPhiInstruction phi; current = current.Next)
                {
                    int firstIndex = phi.GetBasicBlockIndex(firstBlock);
                    int secondIndex = phi.GetBasicBlockIndex(secondBlock);
                    LlvmValue firstValue = phi.GetIncomingValue(firstIndex);
                    LlvmValue secondValue = phi.GetIncomingValue(secondIndex);
                    if (firstValue == secondValue) continue;
                    if (!inserted.TryGetValue((firstValue, secondValue), out LlvmSelectInstruction? select))
                    {
                        select = new(branch.Condition, firstValue, secondValue)
                        {
                            Source = replacement.Source,
                            LogicalType = phi.LogicalType,
                            FastMathFlags = phi.IsFloatingMathOperator ? phi.FastMathFlags : LlvmFastMathFlags.None
                        };
                        select.SetName(firstValue.Name + "." + secondValue.Name);
                        select.GetOperandUse(1).LogicalType = phi.GetOperandUse(firstIndex).LogicalType;
                        select.GetOperandUse(2).LogicalType = phi.GetOperandUse(secondIndex).LogicalType;
                        replacement.Parent!.InsertBefore(select, replacement);
                        inserted.Add((firstValue, secondValue), select);
                        Count("hoist.phi-selects.created");
                    }
                    else Count("hoist.phi-selects.reused");
                    for (int index = 0; index < phi.IncomingCount; ++index)
                        if (phi.GetIncomingBlock(index) == firstBlock || phi.GetIncomingBlock(index) == secondBlock)
                            phi.SetIncomingValue(index, select);
                }
        }
    }
}
