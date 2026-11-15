// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.h/.cpp
// CreateGEP/CreatePtrAdd/CreateScaledPtrAdd/CreateAlignedLoad/CreateAlignedStore.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmLoadInstruction CreateLoad(LlvmType type, LlvmValue pointer, string name = "",
        LlvmAlign? alignment = null, bool isVolatile = false) =>
        Insert(new LlvmLoadInstruction(type, pointer, alignment ?? Layout.GetABITypeAlign(type), isVolatile), name);
    public LlvmStoreInstruction CreateStore(LlvmValue value, LlvmValue pointer, LlvmAlign? alignment = null, bool isVolatile = false) =>
        Insert(new LlvmStoreInstruction(value, pointer, alignment ?? Layout.GetABITypeAlign(value.Type), isVolatile));

    public LlvmLoadInstruction CreateLoad(LlvmType type, LlvmValue pointer, bool isVolatile, string name = "") =>
        CreateLoad(type, pointer, name, isVolatile: isVolatile);
    public LlvmLoadInstruction CreateAlignedLoad(LlvmType type, LlvmValue pointer, LlvmAlign? alignment, string name = "", bool isVolatile = false) =>
        CreateLoad(type, pointer, name, alignment, isVolatile);
    public LlvmStoreInstruction CreateStore(LlvmValue value, LlvmValue pointer, bool isVolatile) => CreateStore(value, pointer, isVolatile: isVolatile, alignment: null);
    public LlvmStoreInstruction CreateAlignedStore(LlvmValue value, LlvmValue pointer, LlvmAlign? alignment, bool isVolatile = false) => CreateStore(value, pointer, alignment, isVolatile);

    public LlvmValue CreateGep(LlvmType type, LlvmValue pointer, IReadOnlyList<LlvmValue> indices,
        string name = "", LlvmGepNoWrapFlags noWrapFlags = default)
    {
        Count("transform_builder.gep.queries");
        if (FoldGep(type, pointer, indices, noWrapFlags) is { } folded) return Folded(folded);
        if (!CanonicalizeGeps)
        { Count("transform_builder.gep.structured"); return Insert(new LlvmGetElementPtrInstruction(type, pointer, indices, noWrapFlags), name); }
        Count("transform_builder.gep.canonicalized");
        uint indexWidth = Layout.GetIndexTypeSizeInBits(pointer.Type);
        Debug.Assert(indices.Count != 0);
        if (type.IsIntegerTy(8) && indices[0].Type.IsIntOrIntVectorTy(indexWidth)) return CreatePtrAdd(pointer, indices[0], name, noWrapFlags);
        LlvmType indexed = type;
        LlvmValue result = pointer;
        for (int index = 0; index < indices.Count; ++index)
        {
            Count("transform_builder.gep.indices");
            LlvmValue operand = indices[index];
            LlvmType indexType = LlvmIntegerType.Get(operand.Context, indexWidth);
            if (operand.Type is LlvmVectorType vectorIndex) indexType = LlvmVectorType.Get(indexType, vectorIndex.ElementCount);
            LlvmStructType? structure = index == 0 ? null : indexed as LlvmStructType;
            bool vectorElement = index != 0 && indexed is LlvmVectorType;
            LlvmType element = index == 0 ? indexed : LlvmGetElementPtrInstruction.GetTypeAtIndex(indexed, operand)!;
            indexed = element;
            if (operand is LlvmConstant constant)
            {
                bool isSplat = indexType.IsVectorTy() && !result.Type.IsVectorTy();
                if (constant.IsNullValue && !isSplat) continue;
                if (structure is not null)
                {
                    ulong offset = Layout.GetStructLayout(structure).GetElementOffset((uint)((LlvmConstantInt)constant).GetZExtValue()).GetFixedValue();
                    if (offset == 0 && !isSplat) continue;
                    result = CreatePtrAdd(result, LlvmConstantInt.Get(indexType, offset, implicitTrunc: true), name, noWrapFlags);
                    continue;
                }
            }
            if (!ReferenceEquals(operand.Type, indexType)) operand = CreateSExtOrTrunc(operand, indexType);
            LlvmTypeSize stride = vectorElement ? Layout.GetTypeStoreSize(element) : Layout.GetTypeAllocSize(element);
            if (operand is LlvmConstantInt integer && !stride.IsScalable)
            {
                var amount = integer.Value * new LlvmAPInt(indexWidth, stride.GetFixedValue(), implicitTrunc: true);
                result = CreatePtrAdd(result, LlvmConstantInt.Get(indexType, amount), name, noWrapFlags);
            }
            else result = CreateScaledPtrAdd(result, stride, operand, name, noWrapFlags);
        }
        return ReferenceEquals(result, pointer)
            ? CreatePtrAdd(pointer, LlvmConstantInt.Get(pointer.Context, new LlvmAPInt(indexWidth, 0)), name, noWrapFlags) : result;
    }
    public LlvmValue CreateGEP(LlvmType type, LlvmValue pointer, IReadOnlyList<LlvmValue> indices,
        string name = "", LlvmGepNoWrapFlags noWrapFlags = default) => CreateGep(type, pointer, indices, name, noWrapFlags);
    public LlvmValue CreateInBoundsGep(LlvmType type, LlvmValue pointer, IReadOnlyList<LlvmValue> indices, string name = "") =>
        CreateGep(type, pointer, indices, name, LlvmGepNoWrapFlags.InBounds);
    public LlvmValue CreatePtrAdd(LlvmValue pointer, LlvmValue offset, string name = "", LlvmGepNoWrapFlags noWrapFlags = default)
    {
        Debug.Assert(offset.Type.IsIntOrIntVectorTy(Layout.GetIndexTypeSizeInBits(pointer.Type)));
        LlvmType byteType = LlvmType.GetInt8Ty(pointer.Context);
        if (FoldGep(byteType, pointer, [offset], noWrapFlags) is { } folded) return Folded(folded);
        return Insert(new LlvmGetElementPtrInstruction(byteType, pointer, [offset], noWrapFlags), name);
    }
    public LlvmValue CreateScaledPtrAdd(LlvmValue pointer, LlvmTypeSize scale, LlvmValue offset,
        string name = "", LlvmGepNoWrapFlags noWrapFlags = default)
    {
        Debug.Assert(offset.Type.IsIntOrIntVectorTy(Layout.GetIndexTypeSizeInBits(pointer.Type)));
        if (scale.IsScalable)
        {
            LlvmValue vscale = CreateVScale(offset.Type.GetScalarType());
            if (offset.Type is LlvmVectorType vector) vscale = CreateVectorSplat(vector.ElementCount, vscale);
            offset = CreateMul(vscale, offset, nuw: noWrapFlags.HasNoUnsignedWrap, nsw: noWrapFlags.HasNoUnsignedSignedWrap);
        }
        if (scale.KnownMinValue == 1) return CreatePtrAdd(pointer, offset, name, noWrapFlags);
        LlvmType element = LlvmArrayType.Get(LlvmType.GetInt8Ty(pointer.Context), scale.KnownMinValue);
        if (FoldGep(element, pointer, [offset], noWrapFlags) is { } folded) return Folded(folded);
        return Insert(new LlvmGetElementPtrInstruction(element, pointer, [offset], noWrapFlags), name);
    }
    private LlvmConstant? FoldGep(LlvmType type, LlvmValue pointer, IReadOnlyList<LlvmValue> indices, LlvmGepNoWrapFlags flags)
    {
        if (FoldConstants && TargetFolder is { } folder) return folder.FoldGep(type, pointer, indices, flags);
        if (!FoldConstants || pointer is not LlvmConstant constant) return null;
        foreach (var index in indices) if (index is not LlvmConstant) return null;
        var constants = new LlvmConstant[indices.Count];
        for (int index = 0; index < constants.Length; ++index) constants[index] = (LlvmConstant)indices[index];
        return LlvmConstantFolding.ConstantFoldGetElementPtr(type, constant, constants, Metrics);
    }
}
