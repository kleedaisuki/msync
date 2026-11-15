// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// findUniqueStoreInBlocks, ensureValueAvailableInSuccessor, mergeConditionalStoreToAddress.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private static LlvmStoreInstruction? FindUniqueStoreInBlocks(LlvmBasicBlock? first, LlvmBasicBlock? second)
        {
            LlvmStoreInstruction? store = null;
            foreach (LlvmBasicBlock? block in new[] { first, second })
            {
                if (block is null) continue;
                foreach (LlvmInstruction instruction in block.Instructions)
                {
                    if (instruction is not LlvmStoreInstruction candidate) continue;
                    if (store is not null) return null;
                    store = candidate;
                }
            }
            return store;
        }

        private LlvmValue EnsureValueAvailableInSuccessor(LlvmValue value, LlvmBasicBlock block,
            int logicalType, LlvmValue? alternative = null)
        {
            LlvmPhiInstruction? found = null;
            LlvmBasicBlock successor = block.GetTerminator().GetSuccessor(0);
            for (LlvmInstruction? instruction = successor.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                if (phi.GetIncomingValue(phi.GetBasicBlockIndex(block)) != value) continue;
                found = phi;
                if (alternative is null) break;
                LlvmBasicBlock[] predecessors = LlvmDominatorTree.Predecessors(successor).ToArray();
                Debug.Assert(predecessors.Length == 2);
                LlvmBasicBlock other = predecessors[0] == block ? predecessors[1] : predecessors[0];
                if (phi.GetIncomingValue(phi.GetBasicBlockIndex(other)) == alternative) break;
                found = null;
            }
            if (found is not null) return found;
            if (alternative is null && (value is not LlvmInstruction producer || producer.Parent != block)) return value;
            int expected = logicalType >= 0 ? logicalType : value.LogicalType;
            var merged = new LlvmPhiInstruction(value.Type, 2) { LogicalType = expected };
            merged.SetName("simplifycfg.merge");
            successor.InsertBefore(merged, successor.FirstInstruction);
            merged.AddIncoming(value, block);
            merged.GetOperandUse(0).LogicalType = expected;
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(successor))
            {
                if (predecessor == block) continue;
                merged.AddIncoming(alternative ?? LlvmPoisonValue.Get(value.Type), predecessor);
                merged.GetOperandUse(merged.IncomingCount - 1).LogicalType = expected;
            }
            Count("conditional_stores.payload_phi");
            return merged;
        }

        private bool MergeConditionalStoreToAddress(LlvmBasicBlock? pTrue, LlvmBasicBlock pFalse,
            LlvmBasicBlock? qTrue, LlvmBasicBlock qFalse, LlvmBasicBlock postBlock, LlvmValue address,
            bool invertPCondition, bool invertQCondition)
        {
            Count("conditional_stores.queries");
            LlvmStoreInstruction? pStore = FindUniqueStoreInBlocks(pTrue, pFalse);
            LlvmStoreInstruction? qStore = FindUniqueStoreInBlocks(qTrue, qFalse);
            if (pStore is null || qStore is null) return RetainConditionalStore("nonunique_store");
            if (!qStore.IsUnordered || !pStore.IsUnordered || pStore.Ordering != qStore.Ordering ||
                pStore.SyncScopeId != qStore.SyncScopeId || pStore.ValueOperand.Type != qStore.ValueOperand.Type)
                return RetainConditionalStore("incompatible_stores");
            LlvmBasicBlock qParent = LlvmDominatorTree.Predecessors(qFalse).Single();
            foreach (LlvmInstruction instruction in qParent.Instructions)
                if ((instruction.MayReadFromMemory() || instruction.MayWriteToMemory())) return RetainConditionalStore("intervening_branch_memory");
            foreach (LlvmInstruction instruction in qFalse.Instructions)
                if (instruction != qStore && (instruction.MayReadFromMemory() || instruction.MayWriteToMemory())) return RetainConditionalStore("qfalse_memory");
            if (qTrue is not null)
                foreach (LlvmInstruction instruction in qTrue.Instructions)
                    if (instruction != qStore && (instruction.MayReadFromMemory() || instruction.MayWriteToMemory())) return RetainConditionalStore("qtrue_memory");
            for (LlvmInstruction? instruction = pStore; instruction is not null; instruction = instruction.Next)
                if (instruction != pStore && (instruction.MayReadFromMemory() || instruction.MayWriteToMemory())) return RetainConditionalStore("pstore_tail_memory");
            LlvmStoreInstruction[] freeStores = [pStore, qStore];
            if (!Tuning.MergeCondStoresAggressively &&
                (!IsConditionalStoreBlockWorthwhile(pTrue, freeStores) || !IsConditionalStoreBlockWorthwhile(pFalse, freeStores) ||
                 !IsConditionalStoreBlockWorthwhile(qTrue, freeStores) || !IsConditionalStoreBlockWorthwhile(qFalse, freeStores)))
                return RetainConditionalStore("if_conversion_cost");
            if (LlvmDominatorTree.Predecessors(postBlock).Skip(2).Any())
            {
                LlvmBasicBlock truePredecessor = qTrue ?? qParent;
                LlvmBasicBlock? split = LlvmBasicBlockUtils.SplitBlockPredecessors(postBlock,
                    [qFalse, truePredecessor], "condstore.split", DominatorUpdater);
                if (split is null) return RetainConditionalStore("cannot_split_postblock");
                postBlock = split;
                Count("conditional_stores.split_postblock");
            }
            var pBranch = (LlvmCondBrInstruction)LlvmDominatorTree.Predecessors(pFalse).Single().GetTerminator();
            var qBranch = (LlvmCondBrInstruction)qParent.GetTerminator();
            LlvmValue pPhi = EnsureValueAvailableInSuccessor(pStore.ValueOperand, pStore.Parent!, pStore.GetOperandUse(0).LogicalType);
            LlvmValue qPhi = EnsureValueAvailableInSuccessor(qStore.ValueOperand, qStore.Parent!, qStore.GetOperandUse(0).LogicalType, pPhi);
            LlvmInstruction splitBefore = FirstNonPhi(postBlock);
            var builder = new LlvmTransformBuilder(splitBefore, Metrics);
            invertPCondition ^= pStore.Parent != pTrue;
            invertQCondition ^= qStore.Parent != qTrue;
            LlvmValue pPredicate = invertPCondition ? builder.CreateNot(pBranch.Condition) : pBranch.Condition;
            LlvmValue qPredicate = invertQCondition ? builder.CreateNot(qBranch.Condition) : qBranch.Condition;
            LlvmValue combined = builder.CreateOr(pPredicate, qPredicate);
            LlvmInstruction thenTerminator = LlvmBasicBlockUtils.SplitBlockAndInsertIfThen(combined, splitBefore,
                unreachable: false, updater: DominatorUpdater, metrics: Metrics);
            builder.SetInsertPoint(thenTerminator);
            LlvmStoreInstruction merged = builder.CreateStore(qPhi, address);
            merged.Source = qStore.Source;
            merged.GetOperandUse(0).LogicalType = qStore.GetOperandUse(0).LogicalType;
            merged.GetOperandUse(1).LogicalType = qStore.GetOperandUse(1).LogicalType;
            merged.Alignment = pStore.Alignment < qStore.Alignment ? pStore.Alignment : qStore.Alignment;
            if (qStore.IsAtomic) { merged.Ordering = qStore.Ordering; merged.SyncScopeId = qStore.SyncScopeId; }
            qStore.EraseFromParent(); pStore.EraseFromParent();
            Count("conditional_stores.merged");
            return true;
        }

        private bool IsConditionalStoreBlockWorthwhile(LlvmBasicBlock? block, IReadOnlyList<LlvmStoreInstruction> freeStores)
        {
            if (block is null) return true;
            LlvmInstructionCost cost = LlvmInstructionCost.Free;
            LlvmInstructionCost budget = Tuning.PhiNodeFoldingThreshold * LlvmInstructionCost.Basic;
            foreach (LlvmInstruction instruction in block.Instructions)
            {
                if (instruction.IsTerminator || instruction is LlvmStoreInstruction store && freeStores.Contains(store)) continue;
                if (instruction is not (LlvmBinaryOperator or LlvmGetElementPtrInstruction)) return false;
                cost += TargetInfo.GetInstructionCost(instruction, LlvmTargetCostKind.SizeAndLatency);
                if (cost > budget) return false;
            }
            return true;
        }

        private bool RetainConditionalStore(string cause)
        { Count("conditional_stores.retained." + cause); return false; }
    }
}
