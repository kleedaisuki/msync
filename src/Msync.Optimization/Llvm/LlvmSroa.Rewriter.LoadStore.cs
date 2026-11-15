// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SROA.cpp rewrite load/store accesses.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class AllocaSliceRewriter
    {
        private LlvmValue VectorLoad(LlvmLoadInstruction load)
        {
            uint first = VectorIndex(newBegin), last = VectorIndex(newEnd);
            Debug.Assert(last > first);
            var value = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "load");
            Count("rewriter.loads.vector");
            return ExtractVector(builder, value, first, last, "vec");
        }

        private LlvmValue IntegerLoad(LlvmLoadInstruction load)
        {
            Debug.Assert(integerType is not null && !load.IsVolatile);
            LlvmValue value = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "load");
            value = builder.CreateBitPreservingCastChain(layout, value, integerType!);
            ulong offset = newBegin - allocaBegin;
            if (offset > 0 || newEnd < allocaEnd)
                value = ExtractInteger(layout, builder, value, LlvmIntegerType.Get(load.Context, (uint)(size * 8)), offset, "extract");
            Debug.Assert(load.Type.GetIntegerBitWidth() >= size * 8);
            Count("rewriter.loads.integer");
            return load.Type.GetIntegerBitWidth() > size * 8 ? builder.CreateZExt(value, load.Type) : value;
        }

        private bool RewriteLoad(LlvmLoadInstruction load)
        {
            LlvmValue previousPointer = load.PointerOperand;
            Debug.Assert(previousPointer == oldPointer);
            LlvmType target = split ? LlvmIntegerType.Get(load.Context, (uint)(size * 8)) : load.Type;
            bool adjusted = false;
            LlvmValue value;
            if (vectorType is not null) value = VectorLoad(load);
            else if (integerType is not null && load.Type.IsIntegerTy()) value = IntegerLoad(load);
            else if (newBegin == allocaBegin && newEnd == allocaEnd &&
                (CanConvertValue(layout, allocaType, target) ||
                 allocaType.IsIntegerTy() && target.IsIntegerTy() && layout.GetTypeStoreSize(target).GetFixedValue() > size && !load.IsVolatile))
            {
                var replacement = builder.CreateAlignedLoad(allocaType,
                    PointerToNewAlloca(((LlvmPointerType)load.PointerOperand.Type).AddressSpace, load.IsVolatile),
                    newAlloca.Alignment, load.Name, load.IsVolatile);
                PreserveLoadShape(load, replacement);
                value = replacement;
                if (allocaType is LlvmIntegerType from && target is LlvmIntegerType to && from.BitWidth < to.BitWidth)
                {
                    value = builder.CreateZExt(value, to, "load.ext");
                    if (layout.IsBigEndian) value = builder.CreateBinary(LlvmOpcode.Shl, value,
                        LlvmConstantInt.Get(to, to.BitWidth - from.BitWidth), "endian_shift");
                }
                Count("rewriter.loads.whole");
            }
            else
            {
                var replacement = builder.CreateAlignedLoad(target, SlicePointer(builder, load.PointerOperand.Type),
                    SliceAlignment(), load.Name, load.IsVolatile);
                PreserveLoadShape(load, replacement);
                value = replacement; adjusted = true;
                Count("rewriter.loads.adjusted");
            }
            value = builder.CreateBitPreservingCastChain(layout, value, target);
            if (split)
            {
                Debug.Assert(!load.IsVolatile && load.Type.IsIntegerTy() && size < layout.GetTypeStoreSize(load.Type).GetFixedValue());
                Debug.Assert(layout.TypeSizeEqualsStoreSize(load.Type));
                builder.SetInsertPoint(load.Parent!, load.Next);
                var placeholder = new LlvmLoadInstruction(load.Type, LlvmPoisonValue.Get(load.PointerOperand.Type), new(1));
                value = InsertInteger(layout, builder, placeholder, value, newBegin - begin, "insert");
                load.ReplaceAllUsesWith(value);
                placeholder.ReplaceAllUsesWith(load);
                placeholder.DeleteValue();
                Count("rewriter.loads.split_insert");
            }
            else load.ReplaceAllUsesWith(value);
            pass.AddDead(load); DeleteIfTriviallyDead(previousPointer);
            Count("rewriter.loads.rewritten");
            return !load.IsVolatile && !adjusted;
        }

        private static void PreserveLoadShape(LlvmLoadInstruction original, LlvmLoadInstruction replacement)
        {
            if (original.Type == replacement.Type) replacement.LogicalType = original.LogicalType;
            if (original.IsVolatile) { replacement.Ordering = original.Ordering; replacement.SyncScopeId = original.SyncScopeId; }
            if (replacement.IsAtomic) replacement.Alignment = original.Alignment;
        }

        private bool VectorStore(LlvmValue value, LlvmStoreInstruction store)
        {
            if (value.Type != vectorType)
            {
                uint first = VectorIndex(newBegin), last = VectorIndex(newEnd), elements = last - first;
                Debug.Assert(last > first && elements <= ((LlvmFixedVectorType)vectorType!).NumElements);
                LlvmType target = elements == 1 ? elementType! : LlvmFixedVectorType.Get(elementType!, elements);
                if (value.Type != target) value = builder.CreateBitPreservingCastChain(layout, value, target);
                LlvmValue previous = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "load");
                value = InsertVector(builder, previous, value, first, "vec");
            }
            builder.CreateAlignedStore(value, newAlloca, newAlloca.Alignment);
            pass.AddDead(store); Count("rewriter.stores.vector");
            return true;
        }

        private bool IntegerStore(LlvmValue value, LlvmStoreInstruction store)
        {
            Debug.Assert(integerType is not null && !store.IsVolatile);
            if (layout.GetTypeSizeInBits(value.Type).GetFixedValue() != integerType!.BitWidth)
            {
                LlvmValue previous = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "oldload");
                previous = builder.CreateBitPreservingCastChain(layout, previous, integerType);
                value = InsertInteger(layout, builder, previous, store.ValueOperand, begin - allocaBegin, "insert");
            }
            value = builder.CreateBitPreservingCastChain(layout, value, allocaType);
            builder.CreateAlignedStore(value, newAlloca, newAlloca.Alignment);
            pass.AddDead(store); Count("rewriter.stores.integer");
            return true;
        }

        private bool RewriteStore(LlvmStoreInstruction store)
        {
            LlvmValue previousPointer = store.PointerOperand, value = store.ValueOperand;
            Debug.Assert(previousPointer == oldPointer);
            if (value.Type.IsPointerTy() && LlvmValueTracking.StripInBoundsOffsets(value) is LlvmAllocaInstruction referenced)
                pass.PostPromotionWorklist.Add(referenced);
            var storedSize = layout.GetTypeStoreSize(value.Type);
            if (storedSize.IsFixed && size < storedSize.GetFixedValue())
            {
                Debug.Assert(!store.IsVolatile && value.Type.IsIntegerTy() && layout.TypeSizeEqualsStoreSize(value.Type));
                value = ExtractInteger(layout, builder, value, LlvmIntegerType.Get(store.Context, (uint)(size * 8)), newBegin - begin, "extract");
            }
            if (vectorType is not null) return VectorStore(value, store);
            if (integerType is not null && value.Type.IsIntegerTy()) return IntegerStore(value, store);
            LlvmStoreInstruction replacement;
            if (newBegin == allocaBegin && newEnd == allocaEnd && CanConvertValue(layout, value.Type, allocaType))
            {
                value = builder.CreateBitPreservingCastChain(layout, value, allocaType);
                replacement = builder.CreateAlignedStore(value,
                    PointerToNewAlloca(((LlvmPointerType)store.PointerOperand.Type).AddressSpace, store.IsVolatile),
                    newAlloca.Alignment, store.IsVolatile);
            }
            else replacement = builder.CreateAlignedStore(value, SlicePointer(builder, store.PointerOperand.Type), SliceAlignment(), store.IsVolatile);
            if (value.Type == store.ValueOperand.Type)
                replacement.GetOperandUse(0).LogicalType = store.GetOperandUse(0).LogicalType;
            if (store.IsVolatile) { replacement.Ordering = store.Ordering; replacement.SyncScopeId = store.SyncScopeId; }
            if (replacement.IsAtomic) replacement.Alignment = store.Alignment;
            pass.AddDead(store); DeleteIfTriviallyDead(previousPointer);
            Count("rewriter.stores.rewritten");
            return replacement.PointerOperand == newAlloca && replacement.ValueOperand.Type == allocaType && !store.IsVolatile;
        }
    }
}
