// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineLoadStoreAlloca.cpp combineLoadToNewType/combineStoreToNewValue,
// unpackLoadToAggregate/unpackStoreToAggregate.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool IsSupportedAtomicType(LlvmType type) =>
            type.IsIntegerTy() || type.IsPointerTy() || type.IsFloatingPointTy();

        private LlvmLoadInstruction CombineLoadToNewType(LlvmLoadInstruction load, LlvmType type, string suffix = "")
        {
            Count("instcombine.memory.load.retype");
            var replacement = new LlvmLoadInstruction(type, load.PointerOperand, load.Alignment,
                load.IsVolatile, load.Ordering, load.SyncScopeId) { IsElementwise = load.IsElementwise };
            replacement.GetOperandUse(0).LogicalType = load.GetOperandUse(0).LogicalType;
            PreserveMemoryStorageView(load.GetOperandUse(0), replacement.GetOperandUse(0));
            if (type == load.Type) replacement.LogicalType = load.LogicalType;
            return Builder.Insert(replacement, load.Name + suffix);
        }

        private LlvmStoreInstruction CombineStoreToNewValue(LlvmStoreInstruction store, LlvmValue value)
        {
            Count("instcombine.memory.store.retype");
            var replacement = new LlvmStoreInstruction(value, store.PointerOperand, store.Alignment,
                store.IsVolatile, store.Ordering, store.SyncScopeId) { IsElementwise = store.IsElementwise };
            replacement.GetOperandUse(1).LogicalType = store.GetOperandUse(1).LogicalType;
            PreserveMemoryStorageView(store.GetOperandUse(1), replacement.GetOperandUse(1));
            if (value.Type == store.ValueOperand.Type) replacement.GetOperandUse(0).LogicalType = store.GetOperandUse(0).LogicalType;
            return Builder.Insert(replacement);
        }

        private static LlvmAlign MemoryCommonAlignment(LlvmAlign alignment, ulong offset) => offset == 0
            ? alignment : new(Math.Min(alignment.Value, 1UL << BitOperations.TrailingZeroCount(offset)));

        private void PreserveMemoryStorageView(LlvmUse source, LlvmUse target)
        {
            if (ReferenceEquals(source.Value, target.Value))
            {
                target.TargetStorageType = source.TargetStorageType;
                if (target.TargetStorageType is not null) Count("instcombine.memory.storage_view.use");
                return;
            }
            if (target.Value is not LlvmGetElementPtrInstruction address) return;
            var storage = source.TargetStorageType ?? (source.Value as LlvmGetElementPtrInstruction)?.GetTargetStorageType();
            if (storage is null) return;
            // The declaration describes the source root, not the scalar leaf.
            // Keep it on the GEP so byte offsets continue to select the field.
            address.TargetStorageType ??= storage;
            Count("instcombine.memory.storage_view.gep");
        }

        private LlvmInstruction? UnpackLoadToAggregate(LlvmLoadInstruction load)
        {
            if (!load.IsSimple) return null;
            LlvmType type = load.Type;
            LlvmValue result = LlvmPoisonValue.Get(type);
            if (type is LlvmStructType structure)
            {
                int count = structure.Elements.Count;
                if (count == 1)
                    return ReplaceInstUsesWith(load, Builder.CreateInsertValue(result,
                        CombineLoadToNewType(load, structure.Elements[0], ".unpack"), 0, load.Name));
                var layout = DL.GetStructLayout(structure);
                if (layout.HasPadding) { Count("instcombine.memory.unpack.load.padding"); return null; }
                var indexType = DL.GetIndexType(load.Context, ((LlvmPointerType)load.PointerOperand.Type).AddressSpace);
                for (int i = 0; i < count; ++i)
                {
                    var elementOffset = layout.GetElementOffset((uint)i);
                    ulong offset = elementOffset.GetKnownMinValue();
                    var pointer = Builder.CreatePtrAdd(load.PointerOperand, Builder.CreateTypeSize(indexType, elementOffset),
                        load.Name + ".elt", LlvmGepNoWrapFlags.InBounds);
                    var element = Builder.CreateAlignedLoad(structure.Elements[i], pointer,
                        MemoryCommonAlignment(load.Alignment, offset), load.Name + ".unpack");
                    PreserveMemoryStorageView(load.GetOperandUse(0), element.GetOperandUse(0));
                    result = Builder.CreateInsertValue(result, element, (uint)i);
                    Count("instcombine.memory.unpack.load.elements");
                }
            }
            else if (type is LlvmArrayType array)
            {
                if (array.NumElements == 1)
                    return ReplaceInstUsesWith(load, Builder.CreateInsertValue(result,
                        CombineLoadToNewType(load, array.ElementType, ".unpack"), 0, load.Name));
                if (array.NumElements > CLOpts.MaxArraySize)
                { Count("instcombine.memory.unpack.load.array_limit"); return null; }
                var indexType = LlvmType.GetInt64Ty(load.Context);
                var zero = LlvmConstantInt.Get(indexType, 0);
                var elementSize = DL.GetTypeAllocSize(array.ElementType);
                ulong offset = 0;
                for (ulong i = 0; i < array.NumElements; ++i)
                {
                    var pointer = Builder.CreateInBoundsGep(array, load.PointerOperand,
                        [zero, LlvmConstantInt.Get(indexType, i)], load.Name + ".elt");
                    var element = Builder.CreateAlignedLoad(array.ElementType, pointer,
                        MemoryCommonAlignment(load.Alignment, offset), load.Name + ".unpack");
                    PreserveMemoryStorageView(load.GetOperandUse(0), element.GetOperandUse(0));
                    result = Builder.CreateInsertValue(result, element, (uint)i);
                    offset += elementSize.GetKnownMinValue();
                    Count("instcombine.memory.unpack.load.elements");
                }
            }
            else return null;
            if (result is LlvmInstruction instruction) instruction.SetName(load.Name);
            Count("instcombine.memory.unpack.loads");
            return ReplaceInstUsesWith(load, result);
        }

        private bool UnpackStoreToAggregate(LlvmStoreInstruction store)
        {
            if (!store.IsSimple) return false;
            LlvmValue value = store.ValueOperand;
            LlvmType type = value.Type;
            if (type is LlvmStructType structure)
            {
                int count = structure.Elements.Count;
                if (count == 1)
                { CombineStoreToNewValue(store, Builder.CreateExtractValue(value, 0)); return true; }
                var layout = DL.GetStructLayout(structure);
                if (layout.HasPadding) { Count("instcombine.memory.unpack.store.padding"); return false; }
                var indexType = DL.GetIndexType(store.Context, ((LlvmPointerType)store.PointerOperand.Type).AddressSpace);
                for (int i = 0; i < count; ++i)
                {
                    var elementOffset = layout.GetElementOffset((uint)i);
                    ulong offset = elementOffset.GetKnownMinValue();
                    var pointer = Builder.CreatePtrAdd(store.PointerOperand, Builder.CreateTypeSize(indexType, elementOffset),
                        store.PointerOperand.Name + ".repack", LlvmGepNoWrapFlags.InBounds);
                    var element = Builder.CreateExtractValue(value, (uint)i, value.Name + ".elt");
                    var member = Builder.CreateAlignedStore(element, pointer, MemoryCommonAlignment(store.Alignment, offset));
                    PreserveMemoryStorageView(store.GetOperandUse(1), member.GetOperandUse(1));
                    Count("instcombine.memory.unpack.store.elements");
                }
            }
            else if (type is LlvmArrayType array)
            {
                if (array.NumElements == 1)
                { CombineStoreToNewValue(store, Builder.CreateExtractValue(value, 0)); return true; }
                if (array.NumElements > CLOpts.MaxArraySize)
                { Count("instcombine.memory.unpack.store.array_limit"); return false; }
                var indexType = LlvmType.GetInt64Ty(store.Context);
                var zero = LlvmConstantInt.Get(indexType, 0);
                var elementSize = DL.GetTypeAllocSize(array.ElementType);
                ulong offset = 0;
                for (ulong i = 0; i < array.NumElements; ++i)
                {
                    var pointer = Builder.CreateInBoundsGep(array, store.PointerOperand,
                        [zero, LlvmConstantInt.Get(indexType, i)], store.PointerOperand.Name + ".repack");
                    var element = Builder.CreateExtractValue(value, (uint)i, value.Name + ".elt");
                    var member = Builder.CreateAlignedStore(element, pointer, MemoryCommonAlignment(store.Alignment, offset));
                    PreserveMemoryStorageView(store.GetOperandUse(1), member.GetOperandUse(1));
                    offset += elementSize.GetKnownMinValue();
                    Count("instcombine.memory.unpack.store.elements");
                }
            }
            else return false;
            Count("instcombine.memory.unpack.stores");
            return true;
        }
    }
}
