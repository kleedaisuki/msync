// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp mergeStoreIntoSuccessor.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool MergeStoreIntoSuccessor(LlvmStoreInstruction store)
        {
            if (!store.IsUnordered) return false;
            var block = store.Parent!;
            var destination = block.GetTerminator().GetSuccessor(0);
            var predecessors = LlvmDominatorTree.Predecessors(destination).Take(3).ToArray();
            if (predecessors.Length != 2) return false;
            var other = predecessors[0] == block ? predecessors[1] : predecessors[0];
            if (block == destination || other == destination) return false;
            var cursor = other.GetTerminator();
            if (cursor.Previous is null) return false;
            bool IsMergeable(LlvmStoreInstruction? otherStore)
            {
                if (otherStore is null || otherStore.PointerOperand != store.PointerOperand) return false;
                var firstType = store.ValueOperand.Type;
                var secondType = otherStore.ValueOperand.Type;
                if (!LlvmCastInstruction.IsBitOrNoopPointerCastable(secondType, firstType, DL) ||
                    !store.HasSameSpecialState(otherStore)) return false;
                return !store.IsElementwise || DL.GetTypeStoreSize(firstType.GetScalarType()) == DL.GetTypeStoreSize(secondType.GetScalarType());
            }
            LlvmStoreInstruction? otherStore;
            if (cursor is LlvmUncondBrInstruction)
            {
                otherStore = cursor.Previous as LlvmStoreInstruction;
                if (!IsMergeable(otherStore)) return false;
            }
            else if (cursor is LlvmCondBrInstruction branch)
            {
                if (branch.GetSuccessor(0) != block && branch.GetSuccessor(1) != block) return false;
                while (true)
                {
                    Count("instcombine.memory.store_merge.backward_scan");
                    otherStore = cursor as LlvmStoreInstruction;
                    if (IsMergeable(otherStore)) break;
                    if (cursor.MayReadFromMemory() || cursor.MayThrow() || cursor.MayWriteToMemory() || cursor.Previous is null) return false;
                    cursor = cursor.Previous;
                }
                for (var instruction = block.FirstInstruction; instruction != store; instruction = instruction!.Next)
                {
                    Count("instcombine.memory.store_merge.forward_scan");
                    if (instruction!.MayReadFromMemory() || instruction.MayThrow() || instruction.MayWriteToMemory()) return false;
                }
            }
            else return false;
            LlvmValue merged = otherStore!.ValueOperand;
            if (merged != store.ValueOperand)
            {
                var phi = new LlvmPhiInstruction(store.ValueOperand.Type, 2);
                phi.SetName("storemerge");
                phi.AddIncoming(store.ValueOperand, block);
                phi.GetOperandUse(0).LogicalType = store.GetOperandUse(0).LogicalType;
                phi.LogicalType = store.GetOperandUse(0).LogicalType >= 0
                    ? store.GetOperandUse(0).LogicalType : store.ValueOperand.LogicalType;
                Builder.SetInsertPoint(otherStore);
                phi.AddIncoming(Builder.CreateBitOrPointerCast(merged, phi.Type), other);
                if (otherStore.ValueOperand.Type == phi.Type)
                    phi.GetOperandUse(1).LogicalType = otherStore.GetOperandUse(0).LogicalType;
                merged = InsertNewInstBefore(phi, destination.FirstInstruction!);
                Count("instcombine.memory.store_merge.phis");
            }
            var firstInsertion = destination.Instructions.First(instruction => instruction is not LlvmPhiInstruction);
            var replacement = new LlvmStoreInstruction(merged, store.PointerOperand, store.Alignment,
                store.IsVolatile, store.Ordering, store.SyncScopeId) { IsElementwise = store.IsElementwise };
            replacement.GetOperandUse(0).LogicalType = store.GetOperandUse(0).LogicalType;
            replacement.GetOperandUse(1).LogicalType = store.GetOperandUse(1).LogicalType;
            InsertNewInstBefore(replacement, firstInsertion);
            EraseInstFromFunction(store);
            EraseInstFromFunction(otherStore);
            Count("instcombine.memory.store_merge.rewrites");
            return true;
        }
    }
}
