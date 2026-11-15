// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// foldTwoEntryPHINode.
using Msync.Core.Llvm;
using P = Msync.Core.Llvm.LlvmCmpPredicate;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool FoldTwoEntryPhi(LlvmPhiInstruction phi)
        {
            if (Trace is null) return FoldTwoEntryPhiCore(phi);
            return Trace.MeasureDetailed("optimizer.llvm-simplifycfg.fold-two-entry-phi",
                (Work: this, Phi: phi), static state => state.Work.FoldTwoEntryPhiCore(state.Phi),
                attributes: Attributes(phi), metrics: _ => Metrics!);
        }
        private bool FoldTwoEntryPhiCore(LlvmPhiInstruction phi)
        {
            LlvmBasicBlock block = phi.Parent!;
            LlvmCondBrInstruction? branch = GetIfCondition(block, out LlvmBasicBlock ifTrue, out LlvmBasicBlock ifFalse);
            if (branch is null) { Count("speculation.two_entry.no_if_condition"); return false; }
            if (branch.Condition is LlvmConstantInt) { Count("speculation.two_entry.constant_condition"); return false; }
            LlvmValue condition = branch.Condition;
            LlvmBasicBlock dominator = branch.Parent!;
            List<LlvmBasicBlock> ifBlocks = new(2);
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                LlvmBasicBlock incoming = phi.GetIncomingBlock(index);
                if (incoming.GetTerminator() is LlvmUncondBrInstruction) ifBlocks.Add(incoming);
            }
            // This family has its own profile test, different from the generic
            // speculation helper. No !prof / !unpredictable metadata is attached.
            Count("speculation.two_entry.unprofiled_branch");
            if (condition is LlvmPhiInstruction conditionPhi && ReferenceEquals(conditionPhi.Parent, block))
            { Count("speculation.two_entry.condition_cycle"); return false; }
            uint phis = 0;
            for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction; instruction = instruction.Next, ++phis)
                if (phis > 2) { Count("speculation.two_entry.phi_count"); return false; }
            HashSet<LlvmInstruction> aggressive = [], zeroCost = [];
            LlvmInstructionCost cost = new(0), budget = new(Tuning.TwoEntryPhiNodeFoldingThreshold);
            bool changed = false;
            for (LlvmInstruction? instruction = block.FirstInstruction; instruction is LlvmPhiInstruction current;)
            {
                LlvmInstruction? next = instruction.Next;
                Count("speculation.two_entry.phis_visited");
                // Source query {DL, PN} has no dominator tree.
                if (LlvmInstructionSimplify.SimplifyInstruction(current, metrics: Metrics) is { } simplified)
                {
                    current.ReplaceAllUsesWith(simplified); current.EraseFromParent();
                    changed = true; Count("speculation.two_entry.phis_simplified");
                    instruction = next; continue;
                }
                if (!DominatesMergePoint(current.GetIncomingValue(0), block, branch, aggressive, ref cost, budget, zeroCost) ||
                    !DominatesMergePoint(current.GetIncomingValue(1), block, branch, aggressive, ref cost, budget, zeroCost)) return changed;
                instruction = next;
            }
            if (block.FirstInstruction is not LlvmPhiInstruction remainingPhi) return true;
            if (remainingPhi.Type.IsIntegerTy(1) && (IsBinaryOrAndEquality(remainingPhi.GetIncomingValue(0)) ||
                IsBinaryOrAndEquality(remainingPhi.GetIncomingValue(1)) || IsBinaryOrAndEquality(condition)))
            { Count("speculation.two_entry.equality_pattern"); return changed; }
            foreach (LlvmBasicBlock ifBlock in ifBlocks)
                for (var instruction = ifBlock.FirstInstruction; instruction is not null && !instruction.IsTerminator; instruction = instruction.Next)
                    if (!aggressive.Contains(instruction))
                    { Count("speculation.two_entry.unhoistable_instruction"); return changed; }
            foreach (LlvmBasicBlock ifBlock in ifBlocks)
                foreach (LlvmUser user in ifBlock.Users)
                    if (user.SubclassId == (byte)LlvmValueKind.BlockAddressVal) return changed;
            foreach (LlvmBasicBlock ifBlock in ifBlocks) HoistAllInstructionsInto(dominator, branch, ifBlock);
            while (block.FirstInstruction is LlvmPhiInstruction current)
            {
                var select = new LlvmSelectInstruction(condition,
                    current.GetIncomingValue(current.GetBasicBlockIndex(ifTrue)),
                    current.GetIncomingValue(current.GetBasicBlockIndex(ifFalse)))
                { FastMathFlags = current.Type.IsFPOrFPVectorTy() ? current.FastMathFlags : LlvmFastMathFlags.None, Source = branch.Source };
                dominator.InsertBefore(select, branch);
                current.ReplaceAllUsesWith(select); select.TakeName(current); current.EraseFromParent();
                Count("speculation.two_entry.selects_created");
            }
            dominator.InsertBefore(new LlvmUncondBrInstruction(block) { Source = branch.Source }, branch);
            List<LlvmCfgUpdate>? updates = null;
            if (DominatorUpdater is not null)
            {
                updates = [new(LlvmCfgUpdateKind.Insert, dominator, block)];
                foreach (LlvmBasicBlock successor in branch.Successors) updates.Add(new(LlvmCfgUpdateKind.Delete, dominator, successor));
            }
            branch.EraseFromParent();
            if (updates is not null) DominatorUpdater!.ApplyUpdates(updates);
            Count("speculation.two_entry.folded");
            return true;
        }
        private static bool IsBinaryOrAndEquality(LlvmValue value)
        {
            if (value is LlvmBinaryOperator binary)
            {
                if (binary.Left is LlvmCmpInstruction left) return IsSpeculationEquality(left.Predicate);
                if (binary.Right is LlvmCmpInstruction right) return IsSpeculationEquality(right.Predicate);
            }
            if (value is LlvmSelectInstruction select)
            {
                if (select.TrueValue is LlvmConstant && select.FalseValue is LlvmCmpInstruction whenFalse) return IsSpeculationEquality(whenFalse.Predicate);
                if (select.FalseValue is LlvmConstant && select.TrueValue is LlvmCmpInstruction whenTrue) return IsSpeculationEquality(whenTrue.Predicate);
            }
            return false;
        }
        private static bool IsSpeculationEquality(P predicate) => predicate is P.ICmpEq or P.ICmpNe or P.FCmpOeq or P.FCmpOne or P.FCmpUeq or P.FCmpUne;
    }
}
