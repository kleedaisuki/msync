// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MemoryLocation.h/.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public readonly record struct LlvmLocationSize
{
    private const ulong Before = ulong.MaxValue, ScalableBit = 1UL << 62, ImpreciseBit = 1UL << 63;
    private const ulong After = (Before - 1) & ~ScalableBit, MapEmptyValue = Before - 2, MapTombstoneValue = Before - 3;
    private const ulong Maximum = (MapTombstoneValue - 1) & ~(ImpreciseBit | ScalableBit);
    private readonly ulong raw;
    private LlvmLocationSize(ulong value) => raw = value;
    public static LlvmLocationSize Precise(ulong value) => new(value > Maximum ? After : value);
    public static LlvmLocationSize Precise(LlvmTypeSize value) => new(value.KnownMinValue > Maximum ? After
        : value.KnownMinValue | (value.IsScalable ? ScalableBit : 0));
    public static LlvmLocationSize UpperBound(ulong value) => value == 0 ? Precise(0)
        : new(value > Maximum ? After : value | ImpreciseBit);
    public static LlvmLocationSize UpperBound(LlvmTypeSize value) => value.IsScalable ? AfterPointer() : UpperBound(value.GetFixedValue());
    public static LlvmLocationSize AfterPointer() => new(After);
    public static LlvmLocationSize BeforeOrAfterPointer() => new(Before);
    public static LlvmLocationSize MapEmpty() => new(MapEmptyValue);
    public static LlvmLocationSize MapTombstone() => new(MapTombstoneValue);
    public bool HasValue => raw != After && raw != Before;
    public bool IsScalable => (raw & ScalableBit) != 0;
    public bool IsPrecise => (raw & ImpreciseBit) == 0;
    public bool IsZero => HasValue && GetValue().KnownMinValue == 0;
    public bool MayBeBeforePointer => raw == Before;
    public ulong ToRaw() => raw;
    public LlvmTypeSize GetValue()
    {
        Debug.Assert(HasValue);
        Debug.Assert((raw & ~(ImpreciseBit | ScalableBit)) < Maximum);
        return new(raw & ~(ImpreciseBit | ScalableBit), IsScalable);
    }
    public LlvmLocationSize UnionWith(LlvmLocationSize other)
    {
        if (this == other) return this;
        if (raw == Before || other.raw == Before) return BeforeOrAfterPointer();
        if (raw == After || other.raw == After || IsScalable || other.IsScalable) return AfterPointer();
        return UpperBound(Math.Max(GetValue().GetFixedValue(), other.GetValue().GetFixedValue()));
    }
}

public readonly partial record struct LlvmMemoryLocation(LlvmValue? Ptr, LlvmLocationSize Size)
{
    public LlvmMemoryLocation() : this(null, LlvmLocationSize.BeforeOrAfterPointer()) { }
    public LlvmMemoryLocation(LlvmValue? pointer, LlvmTypeSize size) : this(pointer, LlvmLocationSize.Precise(size)) { }
    public LlvmMemoryLocation(LlvmValue? pointer, ulong size) : this(pointer, LlvmLocationSize.Precise(size)) { }
    public static LlvmMemoryLocation GetAfter(LlvmValue pointer) => new(pointer, LlvmLocationSize.AfterPointer());
    public static LlvmMemoryLocation GetBeforeOrAfter(LlvmValue pointer) => new(pointer, LlvmLocationSize.BeforeOrAfterPointer());
    public LlvmMemoryLocation WithNewPtr(LlvmValue? pointer) => new(pointer, Size);
    public LlvmMemoryLocation WithNewSize(LlvmLocationSize size) => new(Ptr, size);
    public static LlvmMemoryLocation? GetOrNone(LlvmInstruction instruction, LlvmDataLayout? layout = null)
    {
        if (instruction is not (LlvmLoadInstruction or LlvmStoreInstruction)) return null;
        layout ??= instruction.Parent!.Parent!.Parent!.DataLayout;
        return instruction switch
        {
            LlvmLoadInstruction load => new(load.PointerOperand, layout.GetTypeStoreSize(load.Type)),
            LlvmStoreInstruction store => new(store.PointerOperand, layout.GetTypeStoreSize(store.ValueOperand.Type)),
            _ => null
        };
    }
    public static LlvmMemoryLocation Get(LlvmInstruction instruction, LlvmDataLayout? layout = null) => GetOrNone(instruction, layout)!.Value;
}
