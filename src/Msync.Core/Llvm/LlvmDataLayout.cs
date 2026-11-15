// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/DataLayout.h/.cpp.
// Layout algorithms/defaults are translated; unsupported textual specifications
// are explicit incomplete port coverage, never interpreted as the host layout.
using System.Diagnostics;
using System.Globalization;

namespace Msync.Core.Llvm;

public sealed partial class LlvmDataLayout : IEquatable<LlvmDataLayout>
{
    private readonly Dictionary<LlvmStructType, LlvmStructLayout> layouts = new(ReferenceEqualityComparer.Instance);
    private uint allocaAddressSpace, programAddressSpace, globalsAddressSpace;
    private bool bigEndian;
    private readonly List<byte> legalIntegerWidths = [];
    public string StringRepresentation { get; }
    public LlvmDataLayout(string layout = "")
    {
        StringRepresentation = layout;
        if (layout.Length == 0) return;
        foreach (string specification in layout.Split('-')) ParseSpecification(specification);
        FinalizeNonIntegralSpecifications();
    }
    public static LlvmDataLayout Parse(string layout) => new(layout);
    public string GetStringRepresentation() => StringRepresentation;
    public bool IsBigEndian => bigEndian;
    public bool IsLittleEndian => !bigEndian;
    public bool IsLegalInteger(ulong width) => legalIntegerWidths.Any(value => value == width);
    public bool IsIllegalInteger(ulong width) => !IsLegalInteger(width);
    public bool FitsInLegalInteger(uint width) => legalIntegerWidths.Any(value => width <= value);
    public LlvmIntegerType? GetSmallestLegalIntType(LlvmContext context, uint width = 0)
    {
        foreach (uint legalWidth in legalIntegerWidths)
            if (width <= legalWidth) return LlvmIntegerType.Get(context, legalWidth);
        return null;
    }
    public uint GetProgramAddressSpace() => programAddressSpace;
    public uint GetAllocaAddressSpace() => allocaAddressSpace;
    public uint GetDefaultGlobalsAddressSpace() => globalsAddressSpace;
    public uint GetPointerSizeInBits(uint addressSpace = 0) => GetPointerSpec(addressSpace).BitWidth;
    public uint GetIndexSizeInBits(uint addressSpace) => GetPointerSpec(addressSpace).IndexBitWidth;
    public uint GetAddressSizeInBits(uint addressSpace) => GetIndexSizeInBits(addressSpace);
    public uint GetPointerSize(uint addressSpace = 0) => (GetPointerSizeInBits(addressSpace) + 7) / 8;
    public uint GetIndexSize(uint addressSpace) => (GetIndexSizeInBits(addressSpace) + 7) / 8;
    public uint GetAddressSize(uint addressSpace) => GetIndexSize(addressSpace);
    public LlvmAlign GetPointerABIAlignment(uint addressSpace = 0) => GetPointerSpec(addressSpace).ABIAlign;
    public LlvmAlign GetPointerPrefAlignment(uint addressSpace = 0) => GetPointerSpec(addressSpace).PrefAlign;
    public uint GetPointerTypeSizeInBits(LlvmType type)
    {
        Debug.Assert(type.IsPtrOrPtrVectorTy());
        return GetPointerSizeInBits(((LlvmPointerType)type.GetScalarType()).AddressSpace);
    }
    public uint GetIndexTypeSizeInBits(LlvmType type)
    {
        Debug.Assert(type.IsPtrOrPtrVectorTy());
        return GetIndexSizeInBits(((LlvmPointerType)type.GetScalarType()).AddressSpace);
    }
    public LlvmPointerType GetAllocaPtrType(LlvmContext context) => LlvmPointerType.Get(context, allocaAddressSpace);
    public LlvmIntegerType GetIntPtrType(LlvmContext context, uint addressSpace = 0) =>
        LlvmIntegerType.Get(context, GetPointerSizeInBits(addressSpace));
    public LlvmIntegerType GetIndexType(LlvmContext context, uint addressSpace = 0) =>
        LlvmIntegerType.Get(context, GetIndexSizeInBits(addressSpace));
    public LlvmType GetIndexType(LlvmType pointerType)
    {
        var element = (LlvmPointerType)pointerType.GetScalarType();
        var index = GetIndexType(pointerType.Context, element.AddressSpace);
        return pointerType is LlvmVectorType vector ? LlvmVectorType.Get(index, vector.ElementCount) : index;
    }
    public LlvmIntegerType GetAddressType(LlvmContext context, uint addressSpace = 0) => GetIndexType(context, addressSpace);
    public LlvmType GetAddressType(LlvmType pointerType) => GetIndexType(pointerType);

    private void ParseSpecification(string specification)
    {
        if (specification.Length == 0) throw new FormatException("empty specification is not allowed");
        if (ParseNonIntegralSpecification(specification)) return;
        if (specification[0] == 'p') { ParsePointerSpecification(specification); return; }
        if (ParseNaturalAlignmentSpecification(specification)) return;
        char kind = specification[0];
        string rest = specification[1..];
        if (kind is 'e' or 'E')
        {
            if (rest.Length != 0) throw new FormatException("malformed specification, must be just 'e' or 'E'");
            bigEndian = kind == 'E';
            return;
        }
        if (kind == 's') return; // Upstream's deprecated, intentionally ignored spelling.
        if (kind == 'n')
        {
            foreach (string component in rest.Split(':'))
            {
                if (component.Length == 0) throw new FormatException("size component cannot be empty");
                if (!uint.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out uint width)
                    || width == 0 || width >= (1U << 24))
                    throw new FormatException("size must be a non-zero 24-bit integer");
                // DataLayout's LegalIntWidths is unsigned char at this source pin.
                legalIntegerWidths.Add(unchecked((byte)width));
            }
            return;
        }
        if (kind is 'P' or 'G' or 'A')
        {
            if (rest.Length == 0) throw new FormatException($"malformed specification, must be of the form \"{kind}<address space>\"");
            if (!uint.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out uint addressSpace)
                || addressSpace >= (1U << 24)) throw new FormatException("address space must be a 24-bit integer");
            if (kind == 'P') programAddressSpace = addressSpace;
            else if (kind == 'G') globalsAddressSpace = addressSpace;
            else allocaAddressSpace = addressSpace;
            return;
        }
        throw new NotSupportedException($"LLVM DataLayout specification '{specification}' is not ported yet.");
    }

    public LlvmTypeSize GetTypeSizeInBits(LlvmType type)
    {
        Debug.Assert(type.IsSized());
        return type.Id switch
        {
            LlvmTypeId.LabelTyID => LlvmTypeSize.Fixed(GetPointerSizeInBits()),
            LlvmTypeId.PointerTyID => LlvmTypeSize.Fixed(GetPointerSizeInBits(((LlvmPointerType)type).AddressSpace)),
            LlvmTypeId.ArrayTyID => ((LlvmArrayType)type).NumElements * GetTypeAllocSizeInBits(((LlvmArrayType)type).ElementType),
            LlvmTypeId.StructTyID => GetStructLayout((LlvmStructType)type).SizeInBits,
            LlvmTypeId.ByteTyID => LlvmTypeSize.Fixed(((LlvmByteType)type).BitWidth),
            LlvmTypeId.IntegerTyID => LlvmTypeSize.Fixed(((LlvmIntegerType)type).BitWidth),
            LlvmTypeId.HalfTyID or LlvmTypeId.BFloatTyID => LlvmTypeSize.Fixed(16),
            LlvmTypeId.FloatTyID => LlvmTypeSize.Fixed(32),
            LlvmTypeId.DoubleTyID => LlvmTypeSize.Fixed(64),
            LlvmTypeId.PPC_FP128TyID or LlvmTypeId.FP128TyID => LlvmTypeSize.Fixed(128),
            LlvmTypeId.X86_AMXTyID => LlvmTypeSize.Fixed(8192),
            LlvmTypeId.X86_FP80TyID => LlvmTypeSize.Fixed(80),
            LlvmTypeId.FixedVectorTyID or LlvmTypeId.ScalableVectorTyID => VectorSize((LlvmVectorType)type),
            LlvmTypeId.TargetExtTyID => GetTypeSizeInBits(((LlvmTargetExtensionType)type).GetLayoutType()),
            _ => throw new InvalidOperationException("DataLayout::getTypeSizeInBits(): Unsupported type")
        };
    }
    private LlvmTypeSize VectorSize(LlvmVectorType type) => new(
        type.ElementCount.KnownMinValue * GetTypeSizeInBits(type.ElementType).GetFixedValue(), type.ElementCount.IsScalable);
    public LlvmTypeSize GetTypeStoreSize(LlvmType type) => GetTypeSizeInBits(type).GetWithIncrement(7).DivideCoefficientBy(8);
    public LlvmTypeSize GetTypeStoreSizeInBits(LlvmType type) => GetTypeStoreSize(type) * 8UL;
    public LlvmTypeSize GetTypeAllocSizeInBits(LlvmType type) => GetTypeAllocSize(type) * 8UL;
    public LlvmTypeSize GetTypeAllocSize(LlvmType type)
    {
        if (type is LlvmArrayType array) return array.NumElements * GetTypeAllocSize(array.ElementType);
        if (type is LlvmStructType structure)
        {
            LlvmStructLayout layout = GetStructLayout(structure);
            return structure.IsPacked ? layout.SizeInBytes : layout.SizeInBytes.AlignTo(LlvmAlign.Max(new(1), layout.Alignment).Value);
        }
        if (type is LlvmIntegerType integer)
            return LlvmTypeSize.Fixed((integer.BitWidth + 7UL) / 8).AlignTo(IntegerAlignment(integer.BitWidth, true).Value);
        if (type is LlvmPointerType pointer)
            return LlvmTypeSize.Fixed(GetPointerSize(pointer.AddressSpace)).AlignTo(GetPointerABIAlignment(pointer.AddressSpace).Value);
        if (type is LlvmTargetExtensionType extension) return GetTypeAllocSize(extension.GetLayoutType());
        return GetTypeStoreSize(type).AlignTo(GetABITypeAlign(type).Value);
    }
    public LlvmAlign GetABITypeAlign(LlvmType type) => Alignment(type, true);
    public LlvmAlign GetPrefTypeAlign(LlvmType type) => Alignment(type, false);
    private LlvmAlign Alignment(LlvmType type, bool abi)
    {
        Debug.Assert(type.IsSized());
        if (type.IsLabelTy()) return abi ? GetPointerABIAlignment() : GetPointerPrefAlignment();
        if (type is LlvmPointerType pointer) return abi ? GetPointerABIAlignment(pointer.AddressSpace) : GetPointerPrefAlignment(pointer.AddressSpace);
        if (type is LlvmArrayType array) return Alignment(array.ElementType, abi);
        if (type is LlvmStructType structure)
        {
            if (structure.IsPacked && abi) return new(1);
            return LlvmAlign.Max(abi ? new(1) : new(8), GetStructLayout(structure).Alignment);
        }
        if (type is LlvmByteType bytes) return IntegerAlignment(bytes.BitWidth, abi);
        if (type is LlvmIntegerType integer) return IntegerAlignment(integer.BitWidth, abi);
        if (type.IsFloatingPointTy())
        {
            ulong bits = GetTypeSizeInBits(type).GetFixedValue();
            return new(bits switch { 16 => 2, 32 => 4, 64 => 8, 128 => 16, _ => PowerOf2Ceil(bits / 8) });
        }
        if (type is LlvmVectorType) return new(PowerOf2Ceil(GetTypeStoreSize(type).KnownMinValue));
        if (type.IsX86_AMXTy()) return new(64);
        if (type is LlvmTargetExtensionType extension) return Alignment(extension.GetLayoutType(), abi);
        throw new InvalidOperationException("Bad type for getAlignment!!!");
    }
    private static LlvmAlign IntegerAlignment(uint bits, bool abi) => new(bits switch
    {
        <= 8 => 1, <= 16 => 2, <= 32 => 4, _ => abi ? 4UL : 8UL
    });
    private static ulong PowerOf2Ceil(ulong value) => value <= 1 ? value : LlvmTypeSize.NextPowerOf2(value - 1);
    public LlvmStructLayout GetStructLayout(LlvmStructType type)
    {
        if (!layouts.TryGetValue(type, out LlvmStructLayout? layout)) layouts.Add(type, layout = new(type, this));
        return layout;
    }
    public bool Equals(LlvmDataLayout? other) => other is not null && bigEndian == other.bigEndian
        && allocaAddressSpace == other.allocaAddressSpace && programAddressSpace == other.programAddressSpace
        && globalsAddressSpace == other.globalsAddressSpace
        && stackNaturalAlignment == other.stackNaturalAlignment
        && functionPointerAlignment == other.functionPointerAlignment
        && functionPointerAlignmentType == other.functionPointerAlignmentType
        && legalIntegerWidths.SequenceEqual(other.legalIntegerWidths)
        && pointerSpecs.Count == other.pointerSpecs.Count
        && pointerSpecs.All(pair => other.pointerSpecs.TryGetValue(pair.Key, out PointerSpec? value) && pair.Value == value);
    public override bool Equals(object? other) => other is LlvmDataLayout layout && Equals(layout);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(bigEndian); hash.Add(allocaAddressSpace); hash.Add(programAddressSpace); hash.Add(globalsAddressSpace);
        hash.Add(stackNaturalAlignment); hash.Add(functionPointerAlignment); hash.Add(functionPointerAlignmentType);
        foreach (byte width in legalIntegerWidths) hash.Add(width);
        foreach (var pair in pointerSpecs.OrderBy(pair => pair.Key)) { hash.Add(pair.Key); hash.Add(pair.Value); }
        return hash.ToHashCode();
    }
    public override string ToString() => StringRepresentation;
}

public sealed class LlvmStructLayout
{
    private readonly LlvmTypeSize[] offsets;
    public LlvmTypeSize SizeInBytes { get; }
    public LlvmTypeSize SizeInBits => SizeInBytes * 8UL;
    public LlvmAlign Alignment { get; }
    public bool HasPadding { get; }
    public IReadOnlyList<LlvmTypeSize> MemberOffsets { get; }
    internal LlvmStructLayout(LlvmStructType type, LlvmDataLayout layout)
    {
        Debug.Assert(!type.IsOpaque);
        LlvmTypeSize size = default;
        LlvmAlign alignment = default;
        bool padded = false;
        offsets = new LlvmTypeSize[type.NumElements];
        MemberOffsets = Array.AsReadOnly(offsets);
        for (int index = 0; index < offsets.Length; index++)
        {
            LlvmType member = type.Elements[index];
            if (index == 0 && member.IsScalableTy()) size = LlvmTypeSize.Scalable(0);
            LlvmAlign memberAlignment = type.IsPacked ? new(1) : layout.GetABITypeAlign(member);
            if (!size.IsScalable && !memberAlignment.IsAligned(size.KnownMinValue))
            {
                padded = true;
                size = LlvmTypeSize.Fixed(memberAlignment.AlignTo(size.KnownMinValue));
            }
            alignment = LlvmAlign.Max(alignment, memberAlignment);
            offsets[index] = size;
            size += layout.GetTypeAllocSize(member);
        }
        if (!size.IsScalable && !alignment.IsAligned(size.KnownMinValue))
        {
            padded = true;
            size = LlvmTypeSize.Fixed(alignment.AlignTo(size.KnownMinValue));
        }
        (SizeInBytes, Alignment, HasPadding) = (size, alignment, padded);
    }
    public LlvmTypeSize GetSizeInBytes() => SizeInBytes;
    public LlvmTypeSize GetSizeInBits() => SizeInBits;
    public LlvmAlign GetAlignment() => Alignment;
    public LlvmTypeSize GetElementOffset(uint index) => offsets[index];
    public LlvmTypeSize GetElementOffsetInBits(uint index) => offsets[index] * 8UL;
    public uint GetElementContainingOffset(ulong offset)
    {
        Debug.Assert(!SizeInBytes.IsScalable);
        int begin = 0, end = offsets.Length;
        LlvmTypeSize size = LlvmTypeSize.Fixed(offset);
        while (begin < end)
        {
            int middle = begin + (end - begin) / 2;
            if (LlvmTypeSize.IsKnownLT(size, offsets[middle])) end = middle;
            else begin = middle + 1;
        }
        Debug.Assert(begin != 0);
        return (uint)(begin - 1);
    }
}
