// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instructions.h,
// IR/GEPNoWrapFlags.h, Support/AtomicOrdering.h and lib/IR/Instructions.cpp.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public enum LlvmAtomicOrdering : byte
{
    NotAtomic = 0, Unordered = 1, Monotonic = 2, Acquire = 4, Release = 5,
    AcquireRelease = 6, SequentiallyConsistent = 7
}

public sealed partial class LlvmAllocaInstruction : LlvmInstruction
{
    public LlvmAllocaInstruction(LlvmType allocatedType, LlvmValue arraySize, uint addressSpace = 0, LlvmAlign? alignment = null)
        : base(LlvmPointerType.Get(allocatedType.Context, addressSpace), LlvmOpcode.Alloca, [arraySize])
    {
        Debug.Assert(arraySize.Type.IsIntegerTy());
        AllocatedType = allocatedType;
        Alignment = alignment ?? new(1);
    }
    public LlvmType AllocatedType { get; set; }
    public int AllocatedLogicalType { get; set; } = -1;
    public LlvmStorageView? StorageView { get; set; }
    public LlvmValue ArraySize { get => GetOperand(0)!; set => SetOperand(0, value); }
    public uint AddressSpace => ((LlvmPointerType)Type).AddressSpace;
    public LlvmAlign Alignment { get; set; }
    public bool InAlloca { get; set; }
    public bool SwiftError { get; set; }
    public bool IsStaticAlloca => ArraySize is LlvmConstantInt && !InAlloca &&
        Parent is { Parent: { } function } block && ReferenceEquals(function.BasicBlocks[0], block);
    protected override LlvmInstruction CloneImpl() => new LlvmAllocaInstruction(AllocatedType, ArraySize, AddressSpace, Alignment)
    { InAlloca = InAlloca, SwiftError = SwiftError, AllocatedLogicalType = AllocatedLogicalType, StorageView = StorageView };
}

public sealed class LlvmLoadInstruction : LlvmInstruction
{
    public LlvmLoadInstruction(LlvmType valueType, LlvmValue pointer, LlvmAlign? alignment = null,
        bool isVolatile = false, LlvmAtomicOrdering ordering = LlvmAtomicOrdering.NotAtomic, uint syncScopeId = 1)
        : base(valueType, LlvmOpcode.Load, [pointer])
    {
        Debug.Assert(pointer.Type.IsPointerTy());
        Alignment = alignment ?? new(1); IsVolatile = isVolatile; Ordering = ordering; SyncScopeId = syncScopeId;
    }
    public LlvmValue PointerOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmAlign Alignment { get; set; }
    public bool IsVolatile { get; set; }
    public bool IsElementwise { get; set; }
    public bool HasInvariantLoadMetadata { get; set; }
    public LlvmAtomicOrdering Ordering { get; set; }
    public uint SyncScopeId { get; set; }
    public bool IsAtomic => Ordering != LlvmAtomicOrdering.NotAtomic;
    public bool IsSimple => !IsAtomic && !IsVolatile;
    public bool IsUnordered => !IsVolatile && Ordering is LlvmAtomicOrdering.NotAtomic or LlvmAtomicOrdering.Unordered;
    protected override LlvmInstruction CloneImpl() => new LlvmLoadInstruction(Type, PointerOperand, Alignment, IsVolatile, Ordering, SyncScopeId)
    { IsElementwise = IsElementwise, HasInvariantLoadMetadata = HasInvariantLoadMetadata };
}

public sealed class LlvmStoreInstruction : LlvmInstruction
{
    public LlvmStoreInstruction(LlvmValue value, LlvmValue pointer, LlvmAlign? alignment = null,
        bool isVolatile = false, LlvmAtomicOrdering ordering = LlvmAtomicOrdering.NotAtomic, uint syncScopeId = 1)
        : base(VoidType(value.Context), LlvmOpcode.Store, [value, pointer])
    {
        Debug.Assert(pointer.Type.IsPointerTy());
        Alignment = alignment ?? new(1); IsVolatile = isVolatile; Ordering = ordering; SyncScopeId = syncScopeId;
    }
    public LlvmValue ValueOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue PointerOperand { get => GetOperand(1)!; set => SetOperand(1, value); }
    public LlvmAlign Alignment { get; set; }
    public bool IsVolatile { get; set; }
    public bool IsElementwise { get; set; }
    public LlvmAtomicOrdering Ordering { get; set; }
    public uint SyncScopeId { get; set; }
    public bool IsAtomic => Ordering != LlvmAtomicOrdering.NotAtomic;
    public bool IsSimple => !IsAtomic && !IsVolatile;
    public bool IsUnordered => !IsVolatile && Ordering is LlvmAtomicOrdering.NotAtomic or LlvmAtomicOrdering.Unordered;
    protected override LlvmInstruction CloneImpl() => new LlvmStoreInstruction(ValueOperand, PointerOperand, Alignment, IsVolatile, Ordering, SyncScopeId)
    { IsElementwise = IsElementwise };
}

public readonly record struct LlvmGepNoWrapFlags
{
    private const byte InBoundsBit = 1, NoUnsignedSignedWrapBit = 2, NoUnsignedWrapBit = 4;
    private LlvmGepNoWrapFlags(byte bits)
    {
        Debug.Assert((bits & InBoundsBit) == 0 || (bits & NoUnsignedSignedWrapBit) != 0);
        Raw = bits;
    }
    public byte Raw { get; }
    public bool IsInBounds => (Raw & InBoundsBit) != 0;
    public bool HasNoUnsignedSignedWrap => (Raw & NoUnsignedSignedWrapBit) != 0;
    public bool HasNoUnsignedWrap => (Raw & NoUnsignedWrapBit) != 0;
    public static LlvmGepNoWrapFlags None => default;
    public static LlvmGepNoWrapFlags All => new(7);
    public static LlvmGepNoWrapFlags InBounds => new(InBoundsBit | NoUnsignedSignedWrapBit);
    public static LlvmGepNoWrapFlags NoUnsignedSignedWrap => new(NoUnsignedSignedWrapBit);
    public static LlvmGepNoWrapFlags NoUnsignedWrap => new(NoUnsignedWrapBit);
    public static LlvmGepNoWrapFlags FromRaw(byte bits) => new(bits);
    public LlvmGepNoWrapFlags WithoutInBounds() => new((byte)(Raw & ~InBoundsBit));
    public LlvmGepNoWrapFlags WithoutNoUnsignedSignedWrap() => new((byte)(Raw & ~(InBoundsBit | NoUnsignedSignedWrapBit)));
    public LlvmGepNoWrapFlags WithoutNoUnsignedWrap() => new((byte)(Raw & ~NoUnsignedWrapBit));
    public LlvmGepNoWrapFlags IntersectForOffsetAdd(LlvmGepNoWrapFlags other)
    {
        LlvmGepNoWrapFlags result = this & other;
        return !result.IsInBounds && result.HasNoUnsignedSignedWrap ? result.WithoutNoUnsignedSignedWrap() : result;
    }
    public LlvmGepNoWrapFlags IntersectForReassociate(LlvmGepNoWrapFlags other)
    {
        LlvmGepNoWrapFlags result = this & other;
        return result.HasNoUnsignedWrap ? result : None;
    }
    public static LlvmGepNoWrapFlags operator &(LlvmGepNoWrapFlags first, LlvmGepNoWrapFlags second) => new((byte)(first.Raw & second.Raw));
    public static LlvmGepNoWrapFlags operator |(LlvmGepNoWrapFlags first, LlvmGepNoWrapFlags second) => new((byte)(first.Raw | second.Raw));
}

public sealed class LlvmGetElementPtrInstruction : LlvmInstruction
{
    public LlvmGetElementPtrInstruction(LlvmType sourceElementType, LlvmValue pointer,
        IReadOnlyList<LlvmValue> indices, LlvmGepNoWrapFlags noWrapFlags = default)
        : base(GetReturnType(pointer, indices), LlvmOpcode.GetElementPtr, BuildOperands(pointer, indices))
    {
        SourceElementType = sourceElementType;
        ResultElementType = GetIndexedType(sourceElementType, indices)!;
        Debug.Assert(ResultElementType is not null);
        NoWrapFlags = noWrapFlags;
    }
    public LlvmType SourceElementType { get; }
    // Source-owned target geometry; LLVM arithmetic still uses SourceElementType.
    public LlvmStructType? TargetStorageType { get; set; }
    internal LlvmStructType? GetTargetStorageType()
    {
        for (var gep = this; ;)
        {
            if ((gep.TargetStorageType ?? gep.GetOperandUse(0).TargetStorageType) is { } storage) return storage;
            if (gep.PointerOperand is not LlvmGetElementPtrInstruction parent) return null;
            gep = parent;
        }
    }
    public LlvmType ResultElementType { get; }
    public LlvmValue PointerOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public int IndexCount => OperandCount - 1;
    public LlvmValue GetIndex(int index) => GetOperand(index + 1)!;
    public IEnumerable<LlvmValue> Indices => Enumerable.Range(0, IndexCount).Select(GetIndex);
    public LlvmGepNoWrapFlags NoWrapFlags { get; set; }
    public bool HasAllZeroIndices => Indices.All(index => index is LlvmConstantInt { IsZero: true });
    public bool HasAllConstantIndices => Indices.All(index => index is LlvmConstantInt);
    protected override LlvmInstruction CloneImpl() => new LlvmGetElementPtrInstruction(SourceElementType, PointerOperand, Indices.ToArray(), NoWrapFlags)
    { TargetStorageType = TargetStorageType };

    public static LlvmType GetReturnType(LlvmValue pointer, IReadOnlyList<LlvmValue> indices)
    {
        if (pointer.Type is LlvmVectorType) return pointer.Type;
        foreach (LlvmValue index in indices)
            if (index.Type is LlvmVectorType vector) return LlvmVectorType.Get(pointer.Type, vector.ElementCount);
        return pointer.Type;
    }
    public static LlvmType? GetIndexedType(LlvmType source, IReadOnlyList<LlvmValue> indices)
    {
        LlvmType? type = source;
        for (int index = 1; index < indices.Count && type is not null; index++) type = GetTypeAtIndex(type, indices[index]);
        return type;
    }
    public static LlvmType? GetTypeAtIndex(LlvmType type, LlvmValue index) => type switch
    {
        LlvmStructType structure when !structure.IsOpaque && index is LlvmConstantInt integer
            && integer.Type.IsIntegerTy(32) && integer.Value.GetLimitedValue() < (ulong)structure.Elements.Count => structure.Elements[(int)integer.Value.GetLimitedValue()],
        LlvmArrayType array when index.Type.GetScalarType().IsIntegerTy() => array.ElementType,
        LlvmVectorType vector when index.Type.GetScalarType().IsIntegerTy() => vector.ElementType,
        _ => null
    };
    private static LlvmValue?[] BuildOperands(LlvmValue pointer, IReadOnlyList<LlvmValue> indices) => [pointer, .. indices];
}
