// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ConstantFolding.cpp
// ConstantFoldLoadFromConstPtr/Const/UniformValue/ThroughBitcast and
// FoldReinterpretLoadFromConst, ReadDataFromGlobal (represented constant kinds).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldLoadFromConstPtr(LlvmConstant pointer, LlvmType type,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null) =>
        ConstantFoldLoadFromConstPtr(pointer, type, new LlvmAPInt(layout.GetIndexTypeSizeInBits(pointer.Type), 0), layout, metrics);

    public static LlvmConstant? ConstantFoldLoadFromConstPtr(LlvmConstant pointer, LlvmType type,
        LlvmAPInt offset, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.load.queries");
        if (LlvmValueTracking.GetUnderlyingObject(pointer, metrics: metrics) is not LlvmGlobalVariable global ||
            !global.IsConstant || !global.HasDefinitiveInitializer)
        { Count(metrics, "constant_fold.load.no_definitive_constant_global"); return null; }
        var baseValue = LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, layout, ref offset, metrics: metrics);
        LlvmConstant? result = ReferenceEquals(baseValue, global)
            ? ConstantFoldLoadFromConst(global.Initializer!, type, offset, layout, metrics) : null;
        if (result is not null) { Count(metrics, "constant_fold.load.constant_offset_folded"); return result; }
        result = ConstantFoldLoadFromUniformValue(global.Initializer!, type, layout);
        Count(metrics, result is null ? "constant_fold.load.unfolded" : "constant_fold.load.uniform_folded");
        return result;
    }

    public static LlvmConstant? ConstantFoldLoadFromConst(LlvmConstant constant, LlvmType type,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null) =>
        ConstantFoldLoadFromConst(constant, type, new LlvmAPInt(64, 0), layout, metrics);

    public static LlvmConstant? ConstantFoldLoadFromConst(LlvmConstant constant, LlvmType type,
        LlvmAPInt offset, LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.load.constant_offset_queries");
        if (GetConstantAtOffset(constant, offset, layout) is { } atOffset &&
            ConstantFoldLoadThroughBitcast(atOffset, type, layout) is { } direct) return direct;
        LlvmTypeSize size = layout.GetTypeAllocSize(constant.Type);
        uint width = Math.Max(64U, offset.BitWidth);
        if (!size.IsScalable && offset.SExtOrTrunc(width).Sge(new LlvmAPInt(width, size.GetFixedValue(), isSigned: true)))
        { Count(metrics, "constant_fold.load.out_of_bounds_poison"); return LlvmPoisonValue.Get(type); }
        LlvmConstant? uniform = ConstantFoldLoadFromUniformValue(constant, type, layout);
        if (uniform is not null) return uniform;
        if (offset.GetSignificantBits() > 64)
        { Count(metrics, "constant_fold.load.offset_exceeds_int64"); return null; }
        return FoldReinterpretLoad(constant, type, type, offset.GetSExtValue(), layout, metrics);
    }

    // ConstantFolding.cpp getConstantAtOffset and DataLayout.cpp's index
    // decomposition. Vector indices are deliberately not generated upstream.
    private static LlvmConstant? GetConstantAtOffset(LlvmConstant constant, LlvmAPInt offset, LlvmDataLayout layout)
    {
        if (offset.IsZero) return constant;
        if (constant is not (LlvmConstantStruct or LlvmConstantArray or LlvmConstantDataArray)) return null;
        if (!GetElementIndex(layout.GetTypeAllocSize(constant.Type), ref offset).IsZero) return null;
        LlvmConstant current = constant;
        while (!offset.IsZero)
        {
            LlvmAPInt index;
            if (current.Type is LlvmArrayType array) index = GetElementIndex(layout.GetTypeAllocSize(array.ElementType), ref offset);
            else if (current.Type is LlvmStructType structure)
            {
                LlvmStructLayout shape = layout.GetStructLayout(structure);
                ulong intOffset = offset.GetZExtValue();
                if (intOffset >= shape.GetSizeInBytes().GetFixedValue()) return null;
                uint field = shape.GetElementContainingOffset(intOffset);
                offset -= new LlvmAPInt(offset.BitWidth, shape.GetElementOffset(field).GetFixedValue(), implicitTrunc: true);
                index = new LlvmAPInt(32, field);
            }
            else return null;
            if (index.IsNegative || index.GetActiveBits() >= 32) return null;
            if (current.GetAggregateElement((uint)index.GetZExtValue()) is not { } next) return null;
            current = next;
        }
        return current;
    }

    private static LlvmAPInt GetElementIndex(LlvmTypeSize elementSize, ref LlvmAPInt offset)
    {
        uint width = offset.BitWidth;
        if (elementSize.IsScalable || elementSize.IsZero ||
            (width <= 64 && elementSize.GetFixedValue() >> (int)(width - 1) != 0)) return new(width, 0);
        LlvmAPInt size = new(width, elementSize.GetFixedValue());
        LlvmAPInt index = offset.SDiv(size);
        offset -= index * size;
        if (offset.IsNegative)
        {
            index -= new LlvmAPInt(width, 1);
            offset += size;
        }
        return index;
    }
    public static LlvmConstant? ConstantFoldLoadFromUniformValue(LlvmConstant constant, LlvmType type, LlvmDataLayout layout)
    {
        if (constant is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
        if (constant is LlvmUndefValue) return LlvmUndefValue.Get(type);
        if (layout.GetTypeSizeInBits(constant.Type) != layout.GetTypeStoreSizeInBits(constant.Type)) return null;
        if (constant.IsNullValue && !type.IsX86_AMXTy()) return Null(type);
        if (IsAllOnes(constant) && (type.IsIntegerTy() || type.IsByteTy() || type.IsFloatingPointTy())) return AllOnes(type);
        return null;
    }
    public static LlvmConstant? ConstantFoldLoadThroughBitcast(LlvmConstant constant, LlvmType destination, LlvmDataLayout layout)
    {
        do
        {
            LlvmType source = constant.Type;
            if (ReferenceEquals(source, destination)) return constant;
            LlvmTypeSize sourceSize = layout.GetTypeSizeInBits(source), destinationSize = layout.GetTypeSizeInBits(destination);
            if (!LlvmTypeSize.IsKnownGE(sourceSize, destinationSize)) return null;
            LlvmConstant? uniform = ConstantFoldLoadFromUniformValue(constant, destination, layout);
            if (uniform is not null) return uniform;
            if (sourceSize == destinationSize)
            {
                // All currently parsed DataLayouts use the upstream default
                // integral-pointer specifications; non-integral specs are absent.
                LlvmOpcode cast = source.IsIntegerTy() && destination.IsPointerTy() ? LlvmOpcode.IntToPtr :
                    source.IsPointerTy() && destination.IsIntegerTy() ? LlvmOpcode.PtrToInt : LlvmOpcode.BitCast;
                if (ScalarCastValid(cast, source, destination)) return ConstantFoldCastOperand(cast, constant, destination, layout);
            }
            if (!source.IsAggregateType() && !source.IsVectorTy()) return null;
            if (source is LlvmStructType)
            {
                uint index = 0;
                LlvmConstant? element;
                do { element = constant.GetAggregateElement(index++); }
                while (element is not null && layout.GetTypeSizeInBits(element.Type).IsZero);
                if (element is null) return null;
                constant = element;
            }
            else
            {
                if (source is LlvmVectorType vector && layout.GetTypeSizeInBits(vector.ElementType) != layout.GetTypeStoreSizeInBits(vector.ElementType)) return null;
                LlvmConstant? element = constant.GetAggregateElement(0);
                if (element is null) return null;
                constant = element;
            }
        } while (true);
    }
    private static bool ScalarCastValid(LlvmOpcode opcode, LlvmType source, LlvmType destination)
    {
        // Instructions.cpp::CastInst::castIsValid: the three scalar cast opcodes
        // requested by ConstantFoldLoadThroughBitcast, not an inferred cast rule.
        if (!source.IsFirstClassType() || !destination.IsFirstClassType() ||
            source.IsAggregateType() || destination.IsAggregateType()) return false;
        if (source.IsVectorTy() || destination.IsVectorTy()) return false;
        if (opcode == LlvmOpcode.IntToPtr) return source.IsIntegerTy() && destination.IsPointerTy();
        if (opcode == LlvmOpcode.PtrToInt) return source.IsPointerTy() && destination.IsIntegerTy();
        if (opcode != LlvmOpcode.BitCast) return false;
        if (source.IsTargetExtTy() || !source.IsSingleValueType() ||
            destination.IsTargetExtTy() || !destination.IsSingleValueType()) return false;
        LlvmPointerType? sourcePointer = source as LlvmPointerType, destinationPointer = destination as LlvmPointerType;
        if ((sourcePointer is not null && destination.IsByteTy()) || (source.IsByteTy() && destinationPointer is not null)) return true;
        if ((sourcePointer is null) != (destinationPointer is null)) return false;
        if (sourcePointer is null) return source.GetPrimitiveSizeInBits() == destination.GetPrimitiveSizeInBits();
        return sourcePointer.AddressSpace == destinationPointer!.AddressSpace;
    }
    private static LlvmConstant? FoldReinterpretLoad(LlvmConstant constant, LlvmType type, LlvmType original,
        long offset, LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        if (type is LlvmScalableVectorType) return null;
        if (type is not LlvmIntegerType integer)
        {
            if (!type.IsFloatingPointTy() && !type.IsPointerTy() && !type.IsByteTy() && !type.IsVectorTy()) return null;
            if (type.IsVectorTy()) { Count(metrics, "constant_fold.load.unported_vector_reinterpret"); return null; }
            LlvmIntegerType mapped = LlvmIntegerType.Get(type.Context, (uint)layout.GetTypeSizeInBits(type).GetFixedValue());
            LlvmConstant? result = FoldReinterpretLoad(constant, mapped, original, offset, layout, metrics);
            if (result is null) return null;
            if (result.IsNullValue && !type.IsX86_AMXTy()) return Null(type);
            if (type.IsPointerTy()) return null; // Nonzero IntToPtr ConstantExpr is not represented.
            return FoldBitCast(result, type);
        }
        uint bytes = (integer.BitWidth + 7) / 8;
        if (bytes > 128 || bytes == 0 || (bytes > 32 && original.IsIntegerTy())) return null;
        if (offset <= -(long)bytes) return LlvmPoisonValue.Get(type);
        LlvmTypeSize size = layout.GetTypeAllocSize(constant.Type);
        if (size.IsScalable) return null;
        if (offset >= unchecked((long)size.GetFixedValue())) return LlvmPoisonValue.Get(type);
        byte[] raw = new byte[bytes];
        int skip = offset < 0 ? (int)-offset : 0;
        if (!ReadDataFromGlobal(constant, offset < 0 ? 0 : (ulong)offset, raw.AsSpan(skip), layout, metrics)) return null;
        LlvmAPInt value = new(integer.BitWidth, layout.IsLittleEndian ? raw[^1] : raw[0]);
        for (int i = 1; i < raw.Length; ++i)
        {
            value = value.Shl(8);
            value |= new LlvmAPInt(integer.BitWidth, layout.IsLittleEndian ? raw[raw.Length - 1 - i] : raw[i], implicitTrunc: true);
        }
        return LlvmConstantInt.Get(type.Context, value);
    }
    private static bool ReadDataFromGlobal(LlvmConstant constant, ulong offset, Span<byte> output,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "constant_fold.load.read_constants");
        Debug.Assert(offset <= layout.GetTypeAllocSize(constant.Type).GetFixedValue());
        if (offset >= layout.GetTypeStoreSize(constant.Type).GetFixedValue()) return true;
        if (constant is LlvmConstantAggregateZero or LlvmUndefValue) return true;
        LlvmAPInt? bits = constant switch
        {
            LlvmConstantInt integer => integer.Value, LlvmConstantByte bytes => bytes.Value,
            LlvmConstantFP floating when floating.Type.IsDoubleTy() => floating.Value.BitcastToAPInt(), _ => null
        };
        if (bits is { } value)
        {
            if (value.BitWidth % 8 != 0) return false;
            uint size = value.BitWidth / 8;
            for (int i = 0; i < output.Length && offset != size; ++i, ++offset)
            {
                uint n = layout.IsLittleEndian ? (uint)offset : size - (uint)offset - 1;
                output[i] = (byte)value.LShr(n * 8).Trunc(8).GetZExtValue();
            }
            return true;
        }
        if (constant is LlvmConstantStruct structure)
        {
            LlvmStructLayout shape = layout.GetStructLayout((LlvmStructType)structure.Type);
            uint index = shape.GetElementContainingOffset(offset);
            ulong currentOffset = shape.GetElementOffset(index).GetFixedValue();
            offset -= currentOffset;
            while (true)
            {
                LlvmConstant element = structure.GetElement((int)index);
                ulong elementSize = layout.GetTypeAllocSize(element.Type).GetFixedValue();
                if (offset < elementSize && !ReadDataFromGlobal(element, offset, output, layout, metrics)) return false;
                if (++index == ((LlvmStructType)structure.Type).Elements.Count) return true;
                ulong nextOffset = shape.GetElementOffset(index).GetFixedValue(), written = nextOffset - currentOffset - offset;
                if ((ulong)output.Length <= written) return true;
                output = output[(int)written..];
                offset = 0; currentOffset = nextOffset;
            }
        }
        if (constant is LlvmConstantArray or LlvmConstantDataArray)
        {
            LlvmArrayType array = (LlvmArrayType)constant.Type;
            ulong size = layout.GetTypeAllocSize(array.ElementType).GetFixedValue();
            ulong index = offset / size, elementOffset = offset % size;
            for (; index != array.NumElements; ++index)
            {
                LlvmConstant element = constant.GetAggregateElement((uint)index)!;
                if (!ReadDataFromGlobal(element, elementOffset, output, layout, metrics)) return false;
                ulong written = size - elementOffset;
                if (written >= (ulong)output.Length) return true;
                elementOffset = 0;
                output = output[(int)written..];
            }
            return true;
        }
        return false;
    }
    private static bool IsAllOnes(LlvmConstant value) => value switch
    {
        LlvmConstantInt integer => integer.Value.IsAllOnes, LlvmConstantByte bytes => bytes.Value.IsAllOnes,
        LlvmConstantFP floating => floating.Value.BitcastToAPInt().IsAllOnes, _ => false
    };
}
