// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Attributes.h/.cpp/.td.
using System.Diagnostics;
namespace Msync.Core.Llvm;

// Semantic attribute identities, not LLVM's TableGen-generated numeric IDs.
[Flags]
public enum LlvmAttributeKind : ulong
{
    None = 0, NoUndef = 1U << 0, NonNull = 1U << 1, Returned = 1U << 2,
    ReadNone = 1U << 3, ReadOnly = 1U << 4, WriteOnly = 1U << 5,
    ByVal = 1U << 6, InAlloca = 1U << 7, Preallocated = 1U << 8,
    NoAlias = 1U << 9, ByRef = 1U << 10, StructRet = 1U << 11,
    SwiftSelf = 1U << 12, SwiftError = 1U << 13, Nest = 1U << 14,
    Alignment = 1U << 15, Dereferenceable = 1U << 16,
    DereferenceableOrNull = 1U << 17, Range = 1U << 18,
    NoFPClass = 1U << 19, Captures = 1U << 20, ElementType = 1U << 21,
    SExt = 1U << 22, ZExt = 1U << 23, AllocAlign = 1U << 24,
    Writable = 1U << 25, DeadOnUnwind = 1U << 26, Initializes = 1U << 27,
    DeadOnReturn = 1U << 28, NoFree = 1U << 29, NoFreeObj = 1U << 30,
    AllocatedPointer = 1UL << 31, ImmArg = 1UL << 32
}

public readonly record struct LlvmRangeAttribute
{
    public LlvmRangeAttribute(LlvmAPInt lower, LlvmAPInt upper)
    { Debug.Assert(lower.BitWidth == upper.BitWidth); Lower = lower; Upper = upper; }
    public LlvmAPInt Lower { get; }
    public LlvmAPInt Upper { get; }
    public uint BitWidth => Lower.BitWidth;
    public LlvmConstantRange ToConstantRange() => LlvmConstantRange.GetNonEmpty(Lower, Upper);
    public static LlvmRangeAttribute FromConstantRange(LlvmConstantRange range) => new(range.Lower, range.Upper);
}

public readonly partial record struct LlvmParameterAttributes
{
    public LlvmAttributeKind Flags { get; init; }
    public LlvmAlign? Alignment { get; init; }
    public ulong? DereferenceableBytes { get; init; }
    public ulong? DereferenceableOrNullBytes { get; init; }
    public LlvmCaptureInfo? CaptureInfo { get; init; }
    public LlvmRangeAttribute? Range { get; init; }
    public LlvmFPClassTest NoFPClass { get; init; }
    public LlvmType? ByValType { get; init; }
    public LlvmType? ByRefType { get; init; }
    public LlvmType? StructRetType { get; init; }
    public LlvmType? PreallocatedType { get; init; }
    public LlvmType? InAllocaType { get; init; }
    public LlvmType? ElementType { get; init; }
    public ulong? DeadOnReturnBytes { get; init; }
    public IReadOnlyList<LlvmRangeAttribute>? Initializes { get; init; }
    public bool IsEmpty => this == default;
    public bool NonNull => Has(LlvmAttributeKind.NonNull);
    public bool NoUndef => Has(LlvmAttributeKind.NoUndef);
    public bool Returned => Has(LlvmAttributeKind.Returned);
    public bool Has(LlvmAttributeKind attribute) => attribute switch
    {
        LlvmAttributeKind.Initializes => Initializes is not null,
        LlvmAttributeKind.DeadOnReturn => DeadOnReturnBytes.HasValue,
        LlvmAttributeKind.Alignment => Alignment.HasValue,
        LlvmAttributeKind.Dereferenceable => DereferenceableBytes.HasValue,
        LlvmAttributeKind.DereferenceableOrNull => DereferenceableOrNullBytes.HasValue,
        LlvmAttributeKind.Range => Range.HasValue,
        LlvmAttributeKind.NoFPClass => NoFPClass != LlvmFPClassTest.None,
        LlvmAttributeKind.Captures => CaptureInfo.HasValue,
        LlvmAttributeKind.ByVal => ByValType is not null || (Flags & attribute) != 0,
        LlvmAttributeKind.ByRef => ByRefType is not null || (Flags & attribute) != 0,
        LlvmAttributeKind.StructRet => StructRetType is not null || (Flags & attribute) != 0,
        LlvmAttributeKind.Preallocated => PreallocatedType is not null || (Flags & attribute) != 0,
        LlvmAttributeKind.InAlloca => InAllocaType is not null || (Flags & attribute) != 0,
        LlvmAttributeKind.ElementType => ElementType is not null,
        _ => (Flags & attribute) != 0
    };
    public bool HasPoisonGeneratingAttributes => Alignment.HasValue || Range.HasValue ||
        Has(LlvmAttributeKind.NonNull) || NoFPClass != LlvmFPClassTest.None;
    public bool HasUBImplyingAttributes => Has(LlvmAttributeKind.NoUndef) ||
        DereferenceableBytes.HasValue || DereferenceableOrNullBytes.HasValue;
    public LlvmParameterAttributes WithoutPoisonGeneratingAttributes() => this with
    { Flags = Flags & ~LlvmAttributeKind.NonNull, Alignment = null, Range = null, NoFPClass = LlvmFPClassTest.None };
    public LlvmParameterAttributes WithoutUBImplyingAttributes() => this with
    { Flags = Flags & ~LlvmAttributeKind.NoUndef, DereferenceableBytes = null, DereferenceableOrNullBytes = null };
    public LlvmParameterAttributes Add(LlvmAttributeKind attribute) => this with { Flags = Flags | attribute };
    public LlvmParameterAttributes Remove(LlvmAttributeKind attribute) => attribute switch
    {
        LlvmAttributeKind.Initializes => this with { Initializes = null },
        LlvmAttributeKind.DeadOnReturn => this with { DeadOnReturnBytes = null },
        LlvmAttributeKind.Alignment => this with { Alignment = null },
        LlvmAttributeKind.Dereferenceable => this with { DereferenceableBytes = null },
        LlvmAttributeKind.DereferenceableOrNull => this with { DereferenceableOrNullBytes = null },
        LlvmAttributeKind.Range => this with { Range = null },
        LlvmAttributeKind.NoFPClass => this with { NoFPClass = LlvmFPClassTest.None },
        LlvmAttributeKind.Captures => this with { CaptureInfo = null },
        LlvmAttributeKind.ByVal => this with { Flags = Flags & ~attribute, ByValType = null },
        LlvmAttributeKind.ByRef => this with { Flags = Flags & ~attribute, ByRefType = null },
        LlvmAttributeKind.StructRet => this with { Flags = Flags & ~attribute, StructRetType = null },
        LlvmAttributeKind.InAlloca => this with { Flags = Flags & ~attribute, InAllocaType = null },
        LlvmAttributeKind.Preallocated => this with { Flags = Flags & ~attribute, PreallocatedType = null },
        LlvmAttributeKind.ElementType => this with { ElementType = null },
        _ => this with { Flags = Flags & ~attribute }
    };

    public bool Overlaps(LlvmAttributeKind mask)
    {
        for (ulong remaining = (ulong)mask; remaining != 0; remaining &= remaining - 1)
            if (Has((LlvmAttributeKind)(remaining & (~remaining + 1)))) return true;
        return false;
    }
    public LlvmParameterAttributes RemoveMask(LlvmAttributeKind mask)
    {
        var result = this;
        for (ulong remaining = (ulong)mask; remaining != 0; remaining &= remaining - 1)
            result = result.Remove((LlvmAttributeKind)(remaining & (~remaining + 1)));
        return result;
    }
}

[Flags]
public enum LlvmAttributeSafetyKind : byte { SafeToDrop = 1, UnsafeToDrop = 2, All = SafeToDrop | UnsafeToDrop }

public static class LlvmAttributeFunctions
{
    public static LlvmAttributeKind TypeIncompatible(LlvmType type, LlvmParameterAttributes attributes,
        LlvmAttributeSafetyKind safety = LlvmAttributeSafetyKind.All)
    {
        LlvmAttributeKind result = 0;
        bool safe = (safety & LlvmAttributeSafetyKind.SafeToDrop) != 0;
        bool unsafeToDrop = (safety & LlvmAttributeSafetyKind.UnsafeToDrop) != 0;
        if (!type.IsIntegerTy() && safe) result |= LlvmAttributeKind.AllocAlign;
        if (!type.IsIntegerTy() && !type.IsByteTy() && unsafeToDrop) result |= LlvmAttributeKind.SExt | LlvmAttributeKind.ZExt;
        if (!type.GetScalarType().IsIntegerTy())
        { if (safe) result |= LlvmAttributeKind.Range; }
        else if (attributes.Range is { } range && range.BitWidth != type.GetScalarSizeInBits()) result |= LlvmAttributeKind.Range;
        if (!type.IsPointerTy())
        {
            if (safe) result |= LlvmAttributeKind.NoAlias | LlvmAttributeKind.NonNull | LlvmAttributeKind.ReadNone |
                LlvmAttributeKind.ReadOnly | LlvmAttributeKind.Dereferenceable | LlvmAttributeKind.DereferenceableOrNull |
                LlvmAttributeKind.Writable | LlvmAttributeKind.DeadOnUnwind | LlvmAttributeKind.Initializes |
                LlvmAttributeKind.Captures | LlvmAttributeKind.DeadOnReturn | LlvmAttributeKind.NoFree | LlvmAttributeKind.NoFreeObj;
            if (unsafeToDrop) result |= LlvmAttributeKind.Nest | LlvmAttributeKind.SwiftError | LlvmAttributeKind.Preallocated |
                LlvmAttributeKind.InAlloca | LlvmAttributeKind.ByVal | LlvmAttributeKind.StructRet | LlvmAttributeKind.ByRef |
                LlvmAttributeKind.ElementType | LlvmAttributeKind.AllocatedPointer;
        }
        if (!type.IsPtrOrPtrVectorTy() && safe) result |= LlvmAttributeKind.Alignment;
        if (safe && !IsNoFPClassCompatibleType(type)) result |= LlvmAttributeKind.NoFPClass;
        if (type.IsVoidTy() && safe) result |= LlvmAttributeKind.NoUndef;
        return result;
    }

    public static bool IsNoFPClassCompatibleType(LlvmType type)
    {
        if (type.IsFPOrFPVectorTy()) return true;
        if (type is LlvmStructType structure)
        {
            if (!structure.IsLiteral || !structure.ContainsHomogeneousTypes()) return false;
            type = structure.Elements[0];
        }
        else if (type is LlvmArrayType)
            while (type is LlvmArrayType array) type = array.ElementType;
        return type.IsFPOrFPVectorTy();
    }
}
