// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp
// getGetElementPtr canonical ptradd conversion; ConstantFold.cpp GEP no-op folds.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantExpr
{
    public static LlvmConstant GetPtrAdd(LlvmConstant pointer, LlvmConstant offset,
        LlvmGepNoWrapFlags noWrap = default, LlvmConstantExprRange? inRange = null) =>
        GetGetElementPtr(LlvmType.GetInt8Ty(pointer.Context), pointer, [offset], noWrap, inRange)!;

    public static LlvmConstant? GetGetElementPtr(LlvmType source, LlvmConstant pointer,
        IReadOnlyList<LlvmConstant> indices, LlvmGepNoWrapFlags noWrap = default,
        LlvmConstantExprRange? inRange = null, LlvmType? onlyIfReducedType = null)
    {
        Debug.Assert(IsSupportedGetElementPtr(source));
        if (indices.Count == 0) return pointer;
        LlvmType result = LlvmGetElementPtrInstruction.GetReturnType(pointer, indices.Cast<LlvmValue>().ToArray());
        if (pointer is LlvmPoisonValue) return LlvmPoisonValue.Get(result);
        if (pointer is LlvmUndefValue) return LlvmUndefValue.Get(result);
        if (inRange is null && indices.All(index => index.IsNullValue || index is LlvmUndefValue))
            return result is LlvmVectorType vector && !pointer.Type.IsVectorTy()
                ? LlvmConstantVector.GetSplat(vector.ElementCount, pointer) : pointer;
        Debug.Assert(LlvmGetElementPtrInstruction.GetIndexedType(source, indices.Cast<LlvmValue>().ToArray()) is not null);
        if (onlyIfReducedType == result) return null;
        return Intern(result, LlvmOpcode.GetElementPtr, [pointer, .. indices], noWrap.Raw, source, inRange);
    }

    public static LlvmConstant? GetGetElementPtr(LlvmDataLayout layout, LlvmType source, LlvmConstant pointer,
        IReadOnlyList<LlvmConstant> indices, LlvmGepNoWrapFlags noWrap = default,
        LlvmConstantExprRange? inRange = null, LlvmType? onlyIfReducedType = null)
    {
        if (indices.Count == 0) return pointer;
        LlvmIntegerType indexType = layout.GetIndexType(pointer.Context, ((LlvmPointerType)pointer.Type.GetScalarType()).AddressSpace);
        if (source.IsIntegerTy(8) && indices[0].Type == indexType)
            return GetGetElementPtr(source, pointer, indices, noWrap, inRange, onlyIfReducedType);
        if (!IsSupportedGetElementPtr(source)) return null;
        LlvmType result = LlvmGetElementPtrInstruction.GetReturnType(pointer, indices.Cast<LlvmValue>().ToArray());
        LlvmType offsetType = result is LlvmVectorType vectorResult
            ? LlvmVectorType.Get(indexType, vectorResult.ElementCount) : indexType;
        LlvmConstant offset = GetNullValue(offsetType);
        LlvmType current = source;
        for (int ordinal = 0; ordinal < indices.Count; ++ordinal)
        {
            LlvmConstant index = indices[ordinal];
            if (ordinal > 0 && current is LlvmStructType structure)
            {
                if (index is not LlvmConstantInt field) return null;
                ulong fieldOffset = layout.GetStructLayout(structure).GetElementOffset((uint)field.GetZExtValue()).GetFixedValue();
                current = structure.Elements[(int)field.GetZExtValue()];
                if (fieldOffset == 0) continue;
                LlvmConstant fieldConstant = LlvmConstantInt.Get(indexType, fieldOffset, implicitTrunc: true);
                if (offsetType is LlvmVectorType fieldVector) fieldConstant = LlvmConstantVector.GetSplat(fieldVector.ElementCount, fieldConstant);
                if (FoldBinary(LlvmOpcode.Add, offset, fieldConstant) is not { } sum) return null;
                offset = sum;
                continue;
            }
            bool vectorElement = ordinal > 0 && current is LlvmVectorType;
            if (ordinal > 0) current = current switch
            {
                LlvmArrayType array => array.ElementType,
                LlvmVectorType vector => vector.ElementType,
                _ => throw new InvalidOperationException("Sequential GEP has no represented element type.")
            };
            if (index.IsNullValue) continue;
            if (offsetType is LlvmVectorType offsetVector && !index.Type.IsVectorTy())
                index = LlvmConstantVector.GetSplat(offsetVector.ElementCount, index);
            if (index.Type != offsetType)
            {
                index = FoldIndexCast(index, offsetType)!;
                if (index is null) return null;
            }
            LlvmTypeSize stride = vectorElement ? layout.GetTypeStoreSize(current) : layout.GetTypeAllocSize(current);
            if (stride.IsScalable) return null;
            if (stride.GetFixedValue() != 1)
            {
                index = FoldIndexMultiply(index, new LlvmAPInt(indexType.BitWidth,
                    stride.GetFixedValue(), isSigned: true, implicitTrunc: true))!;
                if (index is null) return null;
            }
            if (FoldBinary(LlvmOpcode.Add, offset, index) is not { } nextOffset) return null;
            offset = nextOffset;
        }
        return GetGetElementPtr(LlvmType.GetInt8Ty(pointer.Context), pointer, [offset], noWrap, inRange, onlyIfReducedType);
    }

    private static LlvmConstant? FoldIndexCast(LlvmConstant index, LlvmType destination)
    {
        if (index.Type is LlvmVectorType vector && destination is LlvmVectorType target)
        {
            if (index.GetSplatValue() is { } splat)
                return FoldIndexCast(splat, target.ElementType) is { } folded ? LlvmConstantVector.GetSplat(target.ElementCount, folded) : null;
            if (vector is not LlvmFixedVectorType fixedVector) return null;
            var elements = new LlvmConstant[fixedVector.NumElements];
            for (uint lane = 0; lane < fixedVector.NumElements; ++lane)
            {
                var element = GetExtractElement(index, LlvmConstantInt.Get(LlvmType.GetInt32Ty(index.Context), lane));
                if (FoldIndexCast(element, target.ElementType) is not { } folded) return null;
                elements[lane] = folded;
            }
            return LlvmConstantVector.Get(elements);
        }
        var integerType = (LlvmIntegerType)destination;
        if (index is LlvmPoisonValue) return LlvmPoisonValue.Get(destination);
        if (index is LlvmUndefValue) return index.Type.GetIntegerBitWidth() < integerType.BitWidth
            ? GetNullValue(destination) : LlvmUndefValue.Get(destination);
        if (index is LlvmConstantInt integer)
            return LlvmConstantInt.Get(destination.Context, integer.Value.SExtOrTrunc(integerType.BitWidth));
        var opcode = index.Type.GetIntegerBitWidth() < integerType.BitWidth ? LlvmOpcode.SExt : LlvmOpcode.Trunc;
        if (index is LlvmConstantExpr { IsCast: true } cast &&
            LlvmCastInstruction.IsEliminableCastPair(cast.Opcode, opcode, cast.GetConstantOperand(0).Type,
                cast.Type, destination) is { } combined && IsSupportedCastOp(combined))
            return GetCast(combined, cast.GetConstantOperand(0), destination);
        return null;
    }

    private static LlvmConstant? FoldIndexMultiply(LlvmConstant index, LlvmAPInt scale)
    {
        if (scale.IsOne) return index;
        if (index.Type is LlvmVectorType vector)
        {
            if (index.GetSplatValue() is { } splat)
                return FoldIndexMultiply(splat, scale) is { } folded ? LlvmConstantVector.GetSplat(vector.ElementCount, folded) : null;
            if (vector is not LlvmFixedVectorType fixedVector) return null;
            var elements = new LlvmConstant[fixedVector.NumElements];
            for (uint lane = 0; lane < fixedVector.NumElements; ++lane)
            {
                var element = GetExtractElement(index, LlvmConstantInt.Get(LlvmType.GetInt32Ty(index.Context), lane));
                if (FoldIndexMultiply(element, scale) is not { } folded) return null;
                elements[lane] = folded;
            }
            return LlvmConstantVector.Get(elements);
        }
        if (index is LlvmPoisonValue) return index;
        if (index is LlvmUndefValue) return (scale.GetWord(0) & 1) != 0 ? index : GetNullValue(index.Type);
        if (scale.IsZero) return GetNullValue(index.Type);
        return index is LlvmConstantInt integer ? LlvmConstantInt.Get(index.Context, integer.Value * scale) : null;
    }
}
