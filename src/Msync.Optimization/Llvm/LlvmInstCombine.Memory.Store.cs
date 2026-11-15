// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp equivalentAddressValues/visitStoreInst.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool EquivalentAddressValues(LlvmValue left, LlvmValue right)
        {
            if (left == right) return true;
            return left is LlvmBinaryOperator or LlvmCastInstruction or LlvmPhiInstruction or LlvmGetElementPtrInstruction &&
                right is LlvmInstruction instruction && ((LlvmInstruction)left).IsIdenticalToWhenDefined(instruction);
        }

        private LlvmInstruction? VisitStoreInst(LlvmStoreInstruction store)
        {
            Count("instcombine.memory.store.visits");
            var value = store.ValueOperand;
            var pointer = store.PointerOperand;
            if (CombineStoreToValueType(store) || UnpackStoreToAggregate(store)) return EraseInstFromFunction(store);
            if (ReplaceGepIdxWithZero(pointer, store) is { } gep) return ReplaceOperand(store, 1, gep);
            if (!store.IsUnordered) return null;
            if (pointer.HasOneUse && (pointer is LlvmAllocaInstruction ||
                pointer is LlvmGetElementPtrInstruction { PointerOperand: LlvmAllocaInstruction alloca } && alloca.HasOneUse))
            { Count("instcombine.memory.store.dead_alloca"); return EraseInstFromFunction(store); }
            if (!AA.GetModRefInfoMask(LlvmMemoryLocation.GetBeforeOrAfter(pointer)).IsModSet())
            { Count("instcombine.memory.store.constant_memory"); return EraseInstFromFunction(store); }

            var previous = store.Previous;
            for (uint remaining = 6; previous is not null && remaining != 0; --remaining)
            {
                Count("instcombine.memory.store.backward_scan");
                if (previous is LlvmStoreInstruction previousStore)
                {
                    if (previousStore.IsUnordered && EquivalentAddressValues(previousStore.PointerOperand, pointer) &&
                        previousStore.ValueOperand.Type == value.Type)
                    {
                        Worklist.Push(store);
                        EraseInstFromFunction(previousStore);
                        Count("instcombine.memory.store.local_dse");
                        return null;
                    }
                    break;
                }
                if (previous is LlvmLoadInstruction load)
                {
                    if (load == value && EquivalentAddressValues(load.PointerOperand, pointer))
                    { Count("instcombine.memory.store.load_back"); return EraseInstFromFunction(store); }
                    break;
                }
                if (previous.MayWriteToMemory() || previous.MayReadFromMemory() || previous.MayThrow()) break;
                previous = previous.Previous;
            }
            if (CanSimplifyNullStoreOrGep(store))
            {
                if (value is LlvmPoisonValue) return null;
                Count("instcombine.memory.store.null_poison");
                return ReplaceOperand(store, 0, LlvmPoisonValue.Get(value.Type));
            }
            if (pointer is LlvmUndefValue)
            {
                RemoveInstructionsBeforeUnreachable(store);
                var deadBlocks = new List<LlvmBasicBlock>();
                HandleUnreachableFrom(store.Next!, deadBlocks);
                HandlePotentiallyDeadBlocks(deadBlocks);
                Count("instcombine.memory.store.unreachable_marker");
                return null;
            }
            if (value is LlvmPoisonValue || value is LlvmUndefValue && DL.GetTypeSizeInBits(value.Type) == DL.GetTypeStoreSizeInBits(value.Type))
            { Count("instcombine.memory.store.undef"); return EraseInstFromFunction(store); }
            if (value.Type.IsByteOrByteVectorTy() && value is LlvmConstant constant && constant is not LlvmConstantExpr)
            {
                Count("instcombine.memory.store.byte_constant");
                return ReplaceOperand(store, 0, LlvmConstantExpr.GetBitCast(constant, LlvmType.GetIntFromByteType(value.Type)));
            }
            if (!LlvmFunction.NullPointerIsDefined(F, MemoryAddressSpace(pointer)) &&
                SimplifyNonNullOperand(pointer, true) is { } nonNull) return ReplaceOperand(store, 1, nonNull);
            // protected_field_ptr/ptrauth_sign have no registered intrinsic IDs.
            return null;
        }
    }
}
