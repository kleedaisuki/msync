// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SROA.cpp memset and memory-transfer rewriting.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class AllocaSliceRewriter
    {
        private LlvmValue IntegerSplat(LlvmValue value, uint bytes)
        {
            Debug.Assert(bytes > 0 && value.Type.IsIntegerTy(8));
            if (bytes == 1) return value;
            var target = LlvmIntegerType.Get(value.Context, bytes * 8);
            var all = LlvmConstantInt.Get(target, LlvmAPInt.GetAllOnes(target.BitWidth));
            var byteMask = LlvmConstantInt.Get((LlvmIntegerType)value.Type, LlvmAPInt.GetAllOnes(8));
            var mask = builder.CreateZExt(byteMask, target);
            return builder.CreateMul(builder.CreateZExt(value, target, "zext"),
                builder.CreateBinary(LlvmOpcode.UDiv, all, mask), "isplat");
        }

        private bool RewriteMemSet(LlvmMemIntrinsic memory)
        {
            Debug.Assert(memory.RawDest == oldPointer);
            if (memory.Length is not LlvmConstantInt length)
            {
                Debug.Assert(!split && newBegin == begin);
                memory.SetDest(SlicePointer(builder, oldPointer.Type));
                memory.SetDestAlignment(SliceAlignment());
                DeleteIfTriviallyDead(oldPointer);
                Count("rewriter.memset.variable_adjusted");
                return false;
            }
            pass.AddDead(memory.Call);
            var scalar = allocaType.GetScalarType();
            bool convert = vectorType is not null || integerType is not null;
            if (!convert && begin <= allocaBegin && end >= allocaEnd && length.Value.GetLimitedValue() <= uint.MaxValue)
            {
                var bytes = LlvmFixedVectorType.Get(LlvmType.GetInt8Ty(newAlloca.Context), (uint)length.Value.GetLimitedValue());
                convert = CanConvertValue(layout, bytes, allocaType) && layout.IsLegalInteger(layout.GetTypeSizeInBits(scalar).GetFixedValue());
            }
            if (!convert)
            {
                var count = LlvmConstantInt.Get((LlvmIntegerType)memory.Length.Type, newEnd - newBegin);
                builder.CreateMemSet(SlicePointer(builder, oldPointer.Type), memory.Value, count, SliceAlignment(), memory.IsVolatile);
                Count("rewriter.memset.resliced");
                return false;
            }
            LlvmValue value;
            if (vectorType is not null)
            {
                Debug.Assert(elementType == scalar);
                uint first = VectorIndex(newBegin), last = VectorIndex(newEnd), count = last - first;
                Debug.Assert(last > first && count <= ((LlvmFixedVectorType)vectorType).NumElements);
                value = IntegerSplat(memory.Value, (uint)(layout.GetTypeSizeInBits(elementType!).GetFixedValue() / 8));
                value = builder.CreateBitPreservingCastChain(layout, value, elementType!);
                if (count > 1) value = builder.CreateVectorSplat(count, value, "vsplat");
                LlvmValue previous = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "oldload");
                value = InsertVector(builder, previous, value, first, "vec");
            }
            else if (integerType is not null)
            {
                Debug.Assert(!memory.IsVolatile);
                value = IntegerSplat(memory.Value, (uint)(newEnd - newBegin));
                if (newBegin != allocaBegin || newEnd != allocaEnd)
                {
                    LlvmValue previous = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "oldload");
                    previous = builder.CreateBitPreservingCastChain(layout, previous, integerType);
                    value = InsertInteger(layout, builder, previous, value, newBegin - allocaBegin, "insert");
                }
                else Debug.Assert(value.Type == integerType);
                value = builder.CreateBitPreservingCastChain(layout, value, allocaType);
            }
            else
            {
                Debug.Assert(newBegin == allocaBegin && newEnd == allocaEnd);
                value = IntegerSplat(memory.Value, (uint)(layout.GetTypeSizeInBits(scalar).GetFixedValue() / 8));
                if (allocaType is LlvmFixedVectorType vector) value = builder.CreateVectorSplat(vector.NumElements, value, "vsplat");
                value = builder.CreateBitPreservingCastChain(layout, value, allocaType);
            }
            builder.CreateAlignedStore(value,
                PointerToNewAlloca(((LlvmPointerType)memory.RawDest.Type).AddressSpace, memory.IsVolatile),
                newAlloca.Alignment, memory.IsVolatile);
            Count("rewriter.memset.scalarized");
            return !memory.IsVolatile;
        }

        private bool RewriteMemTransfer(LlvmMemIntrinsic memory)
        {
            bool destination = ReferenceEquals(memory.RawDestUse, oldUse);
            Debug.Assert(destination ? memory.RawDest == oldPointer : memory.RawSource == oldPointer);
            LlvmAlign sliceAlign = SliceAlignment();
            if (!splittable)
            {
                var pointer = SlicePointer(builder, oldPointer.Type);
                if (destination) { memory.SetDest(pointer); memory.SetDestAlignment(sliceAlign); }
                else { memory.SetSource(pointer); memory.SetSourceAlignment(sliceAlign); }
                DeleteIfTriviallyDead(oldPointer);
                Count("rewriter.transfer.unsplit_adjusted");
                return false;
            }
            bool copy = vectorType is null && integerType is null &&
                (begin > allocaBegin || end < allocaEnd || size != layout.GetTypeStoreSize(allocaType).GetFixedValue()
                    || !layout.TypeSizeEqualsStoreSize(allocaType) || !allocaType.IsSingleValueType());
            if (copy && ReferenceEquals(oldAlloca, newAlloca))
            {
                Debug.Assert(newBegin == begin);
                if (newEnd != end) memory.SetLength(LlvmConstantInt.Get((LlvmIntegerType)memory.Length.Type, newEnd - newBegin));
                Count("rewriter.transfer.unchanged_alloca");
                return false;
            }
            pass.AddDead(memory.Call);
            LlvmValue other = destination ? memory.RawSource : memory.RawDest;
            if (LlvmValueTracking.StripInBoundsOffsets(other) is LlvmAllocaInstruction referenced)
            {
                Debug.Assert(referenced != oldAlloca && referenced != newAlloca);
                pass.Worklist.Add(referenced);
            }
            var otherOffset = new LlvmAPInt(layout.GetIndexSizeInBits(((LlvmPointerType)other.Type).AddressSpace), newBegin - begin);
            LlvmAlign otherAlign = LlvmAlign.CommonAlignment((destination ? memory.SourceAlign : memory.DestAlign) ?? new(1),
                otherOffset.ZExtOrTrunc(64).GetZExtValue());
            if (copy)
            {
                other = GetAdjustedPtr(builder, layout, other, otherOffset, other.Type, other.Name + ".");
                LlvmValue ours = SlicePointer(builder, oldPointer.Type);
                var count = LlvmConstantInt.Get((LlvmIntegerType)memory.Length.Type, newEnd - newBegin);
                builder.CreateMemCpy(destination ? ours : other, destination ? sliceAlign : otherAlign,
                    destination ? other : ours, destination ? otherAlign : sliceAlign, count, memory.IsVolatile);
                Count("rewriter.transfer.memcpy_resliced");
                return false;
            }
            bool whole = newBegin == allocaBegin && newEnd == allocaEnd;
            uint first = vectorType is null ? 0 : VectorIndex(newBegin);
            uint last = vectorType is null ? 0 : VectorIndex(newEnd);
            var narrow = integerType is null ? null : LlvmIntegerType.Get(integerType.Context, (uint)((newEnd - newBegin) * 8));
            LlvmType otherType = vectorType is not null && !whole ? last - first == 1 ? elementType! :
                    LlvmFixedVectorType.Get(elementType!, last - first) : integerType is not null && !whole ? narrow! : allocaType;
            LlvmValue adjusted = GetAdjustedPtr(builder, layout, other, otherOffset, other.Type, other.Name + ".");
            LlvmValue source = destination ? adjusted : PointerToNewAlloca(((LlvmPointerType)memory.RawSource.Type).AddressSpace, memory.IsVolatile);
            LlvmValue target = destination ? PointerToNewAlloca(((LlvmPointerType)memory.RawDest.Type).AddressSpace, memory.IsVolatile) : adjusted;
            LlvmAlign sourceAlign = destination ? otherAlign : sliceAlign, targetAlign = destination ? sliceAlign : otherAlign;
            LlvmValue value;
            if (vectorType is not null && !whole && !destination)
            {
                value = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "load");
                value = ExtractVector(builder, value, first, last, "vec");
            }
            else if (integerType is not null && !whole && !destination)
            {
                value = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "load");
                value = builder.CreateBitPreservingCastChain(layout, value, integerType);
                value = ExtractInteger(layout, builder, value, narrow!, newBegin - allocaBegin, "extract");
            }
            else value = builder.CreateAlignedLoad(otherType, source, sourceAlign, "copyload", memory.IsVolatile);
            if (vectorType is not null && !whole && destination)
            {
                LlvmValue previous = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "oldload");
                value = InsertVector(builder, previous, value, first, "vec");
            }
            else if (integerType is not null && !whole && destination)
            {
                LlvmValue previous = builder.CreateAlignedLoad(allocaType, newAlloca, newAlloca.Alignment, "oldload");
                previous = builder.CreateBitPreservingCastChain(layout, previous, integerType);
                value = InsertInteger(layout, builder, previous, value, newBegin - allocaBegin, "insert");
                value = builder.CreateBitPreservingCastChain(layout, value, allocaType);
            }
            builder.CreateAlignedStore(value, target, targetAlign, memory.IsVolatile);
            Count("rewriter.transfer.scalarized");
            return !memory.IsVolatile;
        }
    }
}
