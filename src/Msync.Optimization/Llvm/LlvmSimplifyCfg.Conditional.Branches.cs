// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// mergeConditionalStores and SimplifyCondBranchToCondBranch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool MergeConditionalStores(LlvmCondBrInstruction prior, LlvmCondBrInstruction branch)
        {
            var pt = prior.GetSuccessor(0); var pf = prior.GetSuccessor(1);
            var qt = branch.GetSuccessor(0); var qf = branch.GetSuccessor(1);
            var post = ConditionalSingleSuccessor(qf);
            if (ConditionalSingleSuccessor(qt) == qf) post = qf;
            if (post is null) return false;
            bool invertP = false, invertQ = false;
            if (pf == branch.Parent) { (pf, pt) = (pt, pf); invertP = true; }
            if (qf == post) { (qf, qt) = (qt, qf); invertQ = true; }
            LlvmBasicBlock? ptrue = pt == branch.Parent ? null : pt;
            LlvmBasicBlock? qtrue = qt == post ? null : qt;
            if (!ConditionalOnePredOneSucc(pf, prior.Parent!, branch.Parent!) || !ConditionalOnePredOneSucc(qf, branch.Parent!, post)) return false;
            if (ptrue is not null && !ConditionalOnePredOneSucc(ptrue, prior.Parent!, branch.Parent!) ||
                qtrue is not null && !ConditionalOnePredOneSucc(qtrue, branch.Parent!, post)) return false;
            if (!branch.Parent!.HasNUses(2)) return false;
            HashSet<LlvmValue> pAddresses = new(ReferenceEqualityComparer.Instance), qAddresses = new(ReferenceEqualityComparer.Instance);
            ConditionalCollectStoreAddresses(ptrue, pAddresses); ConditionalCollectStoreAddresses(pf, pAddresses);
            ConditionalCollectStoreAddresses(qtrue, qAddresses); ConditionalCollectStoreAddresses(qf, qAddresses);
            pAddresses.IntersectWith(qAddresses);
            bool changed = false;
            foreach (var address in pAddresses)
            {
                Count("conditional.stores.common_addresses");
                changed |= MergeConditionalStoreToAddress(ptrue, pf, qtrue, qf, post, address, invertP, invertQ);
            }
            return changed;
        }

        private static LlvmBasicBlock? ConditionalSingleSuccessor(LlvmBasicBlock block) =>
            block.GetTerminator().SuccessorCount == 1 ? block.GetTerminator().GetSuccessor(0) : null;
        private static bool ConditionalOnePredOneSucc(LlvmBasicBlock block, LlvmBasicBlock pred, LlvmBasicBlock succ) =>
            LlvmBasicBlockUtils.GetSinglePredecessor(block) == pred && ConditionalSingleSuccessor(block) == succ;
        private static void ConditionalCollectStoreAddresses(LlvmBasicBlock? block, HashSet<LlvmValue> addresses)
        {
            if (block is null) return;
            foreach (var instruction in block.Instructions)
                if (instruction is LlvmStoreInstruction store) addresses.Add(store.PointerOperand);
        }

        private bool SimplifyCondBranchToCondBranch(LlvmCondBrInstruction prior, LlvmCondBrInstruction branch)
        {
            var block = branch.Parent!;
            Count("conditional.branches.queries");
            if (prior.Condition == branch.Condition && prior.GetSuccessor(0) != prior.GetSuccessor(1) &&
                LlvmBasicBlockUtils.GetSinglePredecessor(block) is not null)
            {
                branch.Condition = LlvmConstantInt.GetBool(block.Context, prior.GetSuccessor(0) == block);
                Count("conditional.branches.same_condition");
                return true;
            }
            // Widenable-branch/deopt intrinsics and profile metadata have no
            // representation in this scalar slice; no function-name matching.
            if (Tuning.MergeCondStores && MergeConditionalStores(prior, branch)) return true;
            if (block.FirstInstruction != branch) return false;
            int pOp, bOp;
            if (prior.GetSuccessor(0) == branch.GetSuccessor(0)) { pOp = 0; bOp = 0; }
            else if (prior.GetSuccessor(0) == branch.GetSuccessor(1)) { pOp = 0; bOp = 1; }
            else if (prior.GetSuccessor(1) == branch.GetSuccessor(0)) { pOp = 1; bOp = 0; }
            else if (prior.GetSuccessor(1) == branch.GetSuccessor(1)) { pOp = 1; bOp = 1; }
            else return false;
            if (prior.GetSuccessor(pOp) == block) return false;
            var common = prior.GetSuccessor(pOp);
            var removed = prior.GetSuccessor(pOp ^ 1);
            uint phis = 0;
            for (var i = common.FirstInstruction; i is LlvmPhiInstruction; i = i.Next, ++phis)
                if (phis > 2) return false;
            var other = branch.GetSuccessor(bOp ^ 1);
            List<LlvmCfgUpdate> updates = [];
            if (other == block)
            {
                var infinite = new LlvmBasicBlock(block.Context, "infloop", block.Parent);
                infinite.Append(new LlvmUncondBrInstruction(infinite));
                updates.Add(new(LlvmCfgUpdateKind.Insert, infinite, infinite));
                other = infinite;
            }
            var builder = new LlvmTransformBuilder(prior, Metrics);
            var pCondition = pOp != 0 ? builder.CreateNot(prior.Condition, prior.Condition.Name + ".not") : prior.Condition;
            var bCondition = bOp != 0 ? builder.CreateNot(branch.Condition, branch.Condition.Name + ".not") : branch.Condition;
            var condition = CreateLogicalOp(builder, LlvmOpcode.Or, pCondition, bCondition, "brmerge");
            prior.Condition = condition;
            prior.SetSuccessor(0, common); prior.SetSuccessor(1, other);
            updates.Add(new(LlvmCfgUpdateKind.Insert, prior.Parent!, other));
            updates.Add(new(LlvmCfgUpdateKind.Delete, prior.Parent!, removed));
            DominatorUpdater?.ApplyUpdates(updates);
            AddPredecessorToBlock(other, prior.Parent!, block);
            for (var instruction = common.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                int branchIndex = phi.GetBasicBlockIndex(block);
                var bValue = phi.GetIncomingValue(branchIndex);
                int index = phi.GetBasicBlockIndex(prior.Parent!);
                var pValue = phi.GetIncomingValue(index);
                if (bValue == pValue) continue;
                var selected = builder.CreateSelect(pCondition, pValue, bValue, pValue.Name + ".mux");
                if (selected is LlvmSelectInstruction mux)
                {
                    // Project ABI expectations belong to uses, not uniqued constants.
                    mux.LogicalType = phi.LogicalType;
                    mux.GetOperandUse(0).LogicalType = prior.GetOperandUse(0).LogicalType;
                    int pType = phi.GetOperandUse(index).LogicalType, bType = phi.GetOperandUse(branchIndex).LogicalType;
                    mux.GetOperandUse(1).LogicalType = pType < 0 ? phi.LogicalType : pType;
                    mux.GetOperandUse(2).LogicalType = bType < 0 ? phi.LogicalType : bType;
                }
                phi.SetIncomingValue(index, selected);
            }
            Count("conditional.branches.merged");
            return true;
        }
    }
}
