// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from Type.h and Type.cpp, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public abstract partial class LlvmType
{
    public LlvmContext Context { get; }
    public LlvmTypeId Id { get; }
    protected IReadOnlyList<LlvmType> containedTypes = Array.Empty<LlvmType>();
    public IReadOnlyList<LlvmType> Subtypes => containedTypes;
    protected LlvmType(LlvmContext context, LlvmTypeId id) => (Context, Id) = (context, id);
    public bool IsVoidTy() => Id == LlvmTypeId.VoidTyID;
    public bool IsHalfTy() => Id == LlvmTypeId.HalfTyID;
    public bool IsBFloatTy() => Id == LlvmTypeId.BFloatTyID;
    public bool IsFloatTy() => Id == LlvmTypeId.FloatTyID;
    public bool IsDoubleTy() => Id == LlvmTypeId.DoubleTyID;
    public bool IsX86_FP80Ty() => Id == LlvmTypeId.X86_FP80TyID;
    public bool IsFP128Ty() => Id == LlvmTypeId.FP128TyID;
    public bool IsPPC_FP128Ty() => Id == LlvmTypeId.PPC_FP128TyID;
    public bool IsX86_AMXTy() => Id == LlvmTypeId.X86_AMXTyID;
    public bool IsLabelTy() => Id == LlvmTypeId.LabelTyID;
    public bool IsMetadataTy() => Id == LlvmTypeId.MetadataTyID;
    public bool IsTokenTy() => Id == LlvmTypeId.TokenTyID;
    public bool IsIntegerTy() => Id == LlvmTypeId.IntegerTyID;
    public bool IsIntegerTy(uint bits) => this is LlvmIntegerType i && i.BitWidth == bits;
    public bool IsByteTy() => Id == LlvmTypeId.ByteTyID;
    public bool IsByteTy(uint bits) => this is LlvmByteType b && b.BitWidth == bits;
    public bool IsPointerTy() => Id == LlvmTypeId.PointerTyID;
    public bool IsFunctionTy() => Id == LlvmTypeId.FunctionTyID;
    public bool IsStructTy() => Id == LlvmTypeId.StructTyID;
    public bool IsArrayTy() => Id == LlvmTypeId.ArrayTyID;
    public bool IsVectorTy() => this is LlvmVectorType;
    public bool IsTargetExtTy() => Id == LlvmTypeId.TargetExtTyID;
    public bool IsIEEELikeFPTy() => Id is LlvmTypeId.HalfTyID or LlvmTypeId.BFloatTyID or
        LlvmTypeId.FloatTyID or LlvmTypeId.DoubleTyID or LlvmTypeId.FP128TyID;
    public bool IsFloatingPointTy() => IsIEEELikeFPTy() || IsX86_FP80Ty() || IsPPC_FP128Ty();
    public bool IsMultiUnitFPType() => IsPPC_FP128Ty();
    public LlvmType GetScalarType() => this is LlvmVectorType v ? v.ElementType : this;
    public bool IsIntOrIntVectorTy() => GetScalarType().IsIntegerTy();
    public bool IsIntOrIntVectorTy(uint bits) => GetScalarType().IsIntegerTy(bits);
    public bool IsByteOrByteVectorTy() => GetScalarType().IsByteTy();
    public bool IsByteOrByteVectorTy(uint bits) => GetScalarType().IsByteTy(bits);
    public bool IsFPOrFPVectorTy() => GetScalarType().IsFloatingPointTy();
    public bool IsPtrOrPtrVectorTy() => GetScalarType().IsPointerTy();
    public bool IsIntOrPtrTy() => IsIntegerTy() || IsPointerTy();
    public bool IsAggregateType() => IsStructTy() || IsArrayTy();
    public bool IsSingleValueType() => IsFloatingPointTy() || IsIntegerTy() || IsPointerTy() ||
        IsVectorTy() || IsX86_AMXTy() || IsTargetExtTy() || IsByteTy();
    public bool IsFirstClassType() => !IsFunctionTy() && !IsVoidTy() && (this is not LlvmStructType s || !s.IsOpaque);
    public virtual bool IsScalableTy() => this switch
    {
        LlvmScalableVectorType => true,
        LlvmTargetExtensionType t => t.IsScalableTargetExtTy(),
        LlvmArrayType a => a.ElementType.IsScalableTy(),
        _ => false
    };
    public bool IsScalableTargetExtTy() => this is LlvmTargetExtensionType t && t.GetLayoutType() is LlvmScalableVectorType;
    public virtual bool ContainsNonGlobalTargetExtType() => this switch
    {
        LlvmArrayType a => a.ElementType.ContainsNonGlobalTargetExtType(),
        LlvmTargetExtensionType t => !t.HasProperty(LlvmTargetExtensionProperty.CanBeGlobal),
        _ => false
    };
    public virtual bool ContainsNonLocalTargetExtType() => this switch
    {
        LlvmArrayType a => a.ElementType.ContainsNonLocalTargetExtType(),
        LlvmTargetExtensionType t => !t.HasProperty(LlvmTargetExtensionProperty.CanBeLocal),
        _ => false
    };
    public bool IsTokenLikeTy() => IsTokenTy() || this is LlvmTargetExtensionType t && t.HasProperty(LlvmTargetExtensionProperty.IsTokenLike);
    public bool IsRISCVVectorTupleTy() => this is LlvmTargetExtensionType t && t.Name == "riscv.vector.tuple";
    public virtual bool IsSized() => IsIntegerTy() || IsByteTy() || IsFloatingPointTy() || IsPointerTy() || IsX86_AMXTy() || (this switch
    {
        LlvmArrayType a => a.ElementType.IsSized(),
        LlvmVectorType v => v.ElementType.IsSized(),
        LlvmTargetExtensionType t => t.GetLayoutType().IsSized(),
        _ => false
    });
    public bool IsEmptyTy() => this switch
    {
        LlvmArrayType a => (uint)a.NumElements == 0 || a.ElementType.IsEmptyTy(),
        LlvmStructType s => s.Elements.All(t => t.IsEmptyTy()),
        _ => false
    };
    public LlvmTypeSize GetPrimitiveSizeInBits()
    {
        if (this is LlvmVectorType vector)
        {
            var elementSize = vector.ElementType.GetPrimitiveSizeInBits();
            Debug.Assert(!elementSize.IsScalable, "Vector type should have fixed-width elements");
            return new LlvmTypeSize(elementSize.GetFixedValue() * vector.ElementCount.KnownMinValue, vector.ElementCount.IsScalable);
        }
        return LlvmTypeSize.Fixed(Id switch
        {
            LlvmTypeId.HalfTyID or LlvmTypeId.BFloatTyID => 16,
            LlvmTypeId.FloatTyID => 32,
            LlvmTypeId.DoubleTyID => 64,
            LlvmTypeId.X86_FP80TyID => 80,
            LlvmTypeId.FP128TyID or LlvmTypeId.PPC_FP128TyID => 128,
            LlvmTypeId.X86_AMXTyID => 8192,
            LlvmTypeId.IntegerTyID => ((LlvmIntegerType)this).BitWidth,
            LlvmTypeId.ByteTyID => ((LlvmByteType)this).BitWidth,
            _ => 0
        });
    }
    public uint GetScalarSizeInBits() => (uint)GetScalarType().GetPrimitiveSizeInBits().GetFixedValue();
    public uint GetIntegerBitWidth() => ((LlvmIntegerType)this).BitWidth;
    public uint GetPointerAddressSpace() => ((LlvmPointerType)this).AddressSpace;
    public int GetFPMantissaWidth()
    {
        if (this is LlvmVectorType v) return v.ElementType.GetFPMantissaWidth();
        Debug.Assert(IsFloatingPointTy(), "Not a floating point type!");
        return Id switch
        {
            LlvmTypeId.HalfTyID => 11, LlvmTypeId.BFloatTyID => 8, LlvmTypeId.FloatTyID => 24,
            LlvmTypeId.DoubleTyID => 53, LlvmTypeId.X86_FP80TyID => 64, LlvmTypeId.FP128TyID => 113, _ => -1
        };
    }
    public bool CanLosslesslyBitCastTo(LlvmType type)
    {
        if (ReferenceEquals(this, type)) return true;
        if (!IsFirstClassType() || !type.IsFirstClassType()) return false;
        if (this is LlvmVectorType && type is LlvmVectorType) return GetPrimitiveSizeInBits().Equals(type.GetPrimitiveSizeInBits());
        if (this is LlvmFixedVectorType && type.IsX86_AMXTy() && GetPrimitiveSizeInBits().GetFixedValue() == 8192) return true;
        return IsX86_AMXTy() && type is LlvmFixedVectorType && type.GetPrimitiveSizeInBits().GetFixedValue() == 8192;
    }
    public static LlvmType? GetPrimitiveType(LlvmContext c, LlvmTypeId id) => c.GetPrimitiveType(id);
    public static LlvmType GetVoidTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.VoidTyID)!;
    public static LlvmType GetLabelTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.LabelTyID)!;
    public static LlvmType GetHalfTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.HalfTyID)!;
    public static LlvmType GetBFloatTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.BFloatTyID)!;
    public static LlvmType GetFloatTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.FloatTyID)!;
    public static LlvmType GetDoubleTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.DoubleTyID)!;
    public static LlvmType GetMetadataTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.MetadataTyID)!;
    public static LlvmType GetTokenTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.TokenTyID)!;
    public static LlvmType GetX86_FP80Ty(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.X86_FP80TyID)!;
    public static LlvmType GetFP128Ty(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.FP128TyID)!;
    public static LlvmType GetPPC_FP128Ty(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.PPC_FP128TyID)!;
    public static LlvmType GetX86_AMXTy(LlvmContext c) => c.GetPrimitiveType(LlvmTypeId.X86_AMXTyID)!;
    public static LlvmIntegerType GetInt1Ty(LlvmContext c) => c.BuiltinIntegers[1];
    public static LlvmByteType GetByte1Ty(LlvmContext c) => c.BuiltinBytes[1];
    public static LlvmIntegerType GetInt8Ty(LlvmContext c) => c.BuiltinIntegers[8];
    public static LlvmByteType GetByte8Ty(LlvmContext c) => c.BuiltinBytes[8];
    public static LlvmIntegerType GetInt16Ty(LlvmContext c) => c.BuiltinIntegers[16];
    public static LlvmByteType GetByte16Ty(LlvmContext c) => c.BuiltinBytes[16];
    public static LlvmIntegerType GetInt32Ty(LlvmContext c) => c.BuiltinIntegers[32];
    public static LlvmByteType GetByte32Ty(LlvmContext c) => c.BuiltinBytes[32];
    public static LlvmIntegerType GetInt64Ty(LlvmContext c) => c.BuiltinIntegers[64];
    public static LlvmByteType GetByte64Ty(LlvmContext c) => c.BuiltinBytes[64];
    public static LlvmIntegerType GetInt128Ty(LlvmContext c) => c.BuiltinIntegers[128];
    public static LlvmByteType GetByte128Ty(LlvmContext c) => c.BuiltinBytes[128];
    public static LlvmIntegerType GetIntNTy(LlvmContext c, uint n) => LlvmIntegerType.Get(c, n);
    public static LlvmByteType GetByteNTy(LlvmContext c, uint n) => LlvmByteType.Get(c, n);
    public static LlvmPointerType GetPointerTy(LlvmContext c, uint addressSpace = 0) => LlvmPointerType.Get(c, addressSpace);
    public LlvmPointerType GetPointerTo(uint addressSpace = 0) => LlvmPointerType.Get(Context, addressSpace);
    public static LlvmType GetIntFromByteType(LlvmType type)
    {
        Debug.Assert(type.IsByteOrByteVectorTy(), "Expected a byte or byte vector type.");
        var element = GetIntNTy(type.Context, type.GetScalarSizeInBits());
        return type is LlvmVectorType v ? LlvmVectorType.Get(element, v.ElementCount) : element;
    }
    public static LlvmType GetByteFromIntType(LlvmType type)
    {
        Debug.Assert(!type.IsPtrOrPtrVectorTy(), "Expected a non-pointer or non-pointer vector type.");
        var element = GetByteNTy(type.Context, type.GetScalarSizeInBits());
        return type is LlvmVectorType v ? LlvmVectorType.Get(element, v.ElementCount) : element;
    }
    public static LlvmType GetWasm_ExternrefTy(LlvmContext c) => LlvmTargetExtensionType.Get(c, "wasm.externref", [], []);
    public static LlvmType GetWasm_FuncrefTy(LlvmContext c) => LlvmTargetExtensionType.Get(c, "wasm.funcref", [], []);
}

internal sealed class LlvmPrimitiveType(LlvmContext context, LlvmTypeId id) : LlvmType(context, id);
