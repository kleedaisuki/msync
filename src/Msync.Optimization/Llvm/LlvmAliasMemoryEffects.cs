// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/ModRef.h MemoryEffectsBase.
namespace Msync.Optimization.Llvm;

public enum LlvmIRMemLocation : byte { ArgMem, InaccessibleMem, ErrnoMem, Other, TargetMem0, TargetMem1 }
public readonly record struct LlvmMemoryEffects
{
    private readonly uint data;
    private LlvmMemoryEffects(uint value) => data = value;
    public LlvmMemoryEffects(LlvmIRMemLocation location, LlvmModRefInfo info) => data = (uint)info << (2 * (int)location);
    public LlvmMemoryEffects(LlvmModRefInfo info)
    {
        data = 0;
        for (int location = 0; location < 6; ++location) data |= (uint)info << (2 * location);
    }
    public static LlvmMemoryEffects Unknown() => new(LlvmModRefInfo.ModRef);
    public static LlvmMemoryEffects None() => new(LlvmModRefInfo.NoModRef);
    public static LlvmMemoryEffects ReadOnly() => new(LlvmModRefInfo.Ref);
    public static LlvmMemoryEffects WriteOnly() => new(LlvmModRefInfo.Mod);
    public static LlvmMemoryEffects ArgMemOnly(LlvmModRefInfo info = LlvmModRefInfo.ModRef) => new(LlvmIRMemLocation.ArgMem, info);
    public static LlvmMemoryEffects InaccessibleMemOnly(LlvmModRefInfo info = LlvmModRefInfo.ModRef) => new(LlvmIRMemLocation.InaccessibleMem, info);
    public static LlvmMemoryEffects ErrnoMemOnly(LlvmModRefInfo info = LlvmModRefInfo.ModRef) => new(LlvmIRMemLocation.ErrnoMem, info);
    public static LlvmMemoryEffects OtherMemOnly(LlvmModRefInfo info = LlvmModRefInfo.ModRef) => new(LlvmIRMemLocation.Other, info);
    public static LlvmMemoryEffects InaccessibleOrArgMemOnly(LlvmModRefInfo info = LlvmModRefInfo.ModRef) =>
        InaccessibleMemOnly(info).WithModRef(LlvmIRMemLocation.ArgMem, info);
    public static LlvmMemoryEffects InaccessibleOrErrnoMemOnly(LlvmModRefInfo inaccessible = LlvmModRefInfo.ModRef,
        LlvmModRefInfo errno = LlvmModRefInfo.ModRef) => InaccessibleMemOnly(inaccessible).WithModRef(LlvmIRMemLocation.ErrnoMem, errno);
    public static LlvmMemoryEffects InaccessibleOrArgOrErrnoMemOnly(LlvmModRefInfo inaccessibleOrArg = LlvmModRefInfo.ModRef,
        LlvmModRefInfo errno = LlvmModRefInfo.ModRef) => InaccessibleOrArgMemOnly(inaccessibleOrArg).WithModRef(LlvmIRMemLocation.ErrnoMem, errno);
    public static LlvmMemoryEffects ArgumentOrErrnoMemOnly(LlvmModRefInfo argument = LlvmModRefInfo.ModRef,
        LlvmModRefInfo errno = LlvmModRefInfo.ModRef) => ArgMemOnly(argument).WithModRef(LlvmIRMemLocation.ErrnoMem, errno);
    public static LlvmMemoryEffects CreateFromIntValue(uint value) => new(value);
    public uint ToIntValue() => data;
    public LlvmModRefInfo GetModRef(LlvmIRMemLocation location) => (LlvmModRefInfo)((data >> (2 * (int)location)) & 3);
    public LlvmModRefInfo GetModRef()
    {
        LlvmModRefInfo info = 0;
        for (int location = 0; location < 6; ++location) info |= GetModRef((LlvmIRMemLocation)location);
        return info;
    }
    public LlvmMemoryEffects WithModRef(LlvmIRMemLocation location, LlvmModRefInfo info) =>
        new((data & ~(3U << (2 * (int)location))) | ((uint)info << (2 * (int)location)));
    public LlvmMemoryEffects WithoutLoc(LlvmIRMemLocation location) => WithModRef(location, LlvmModRefInfo.NoModRef);
    public bool DoesNotAccessMemory => data == 0;
    public bool OnlyReadsMemory => !GetModRef().IsModSet();
    public bool OnlyWritesMemory => !GetModRef().IsRefSet();
    public bool OnlyAccessesArgPointees => WithoutLoc(LlvmIRMemLocation.ArgMem).DoesNotAccessMemory;
    public bool DoesAccessArgPointees => GetModRef(LlvmIRMemLocation.ArgMem).IsModOrRefSet();
    public bool OnlyAccessesInaccessibleMem => WithoutLoc(LlvmIRMemLocation.InaccessibleMem).DoesNotAccessMemory;
    public bool OnlyAccessesInaccessibleOrTargetMem => WithoutLoc(LlvmIRMemLocation.TargetMem0)
        .WithoutLoc(LlvmIRMemLocation.TargetMem1).WithoutLoc(LlvmIRMemLocation.InaccessibleMem).DoesNotAccessMemory;
    public bool IsTargetMemLoc(LlvmIRMemLocation location) => location is LlvmIRMemLocation.TargetMem0 or LlvmIRMemLocation.TargetMem1;
    public bool IsTargetMemLocSameForAll => GetModRef(LlvmIRMemLocation.TargetMem0) == GetModRef(LlvmIRMemLocation.TargetMem1);
    public bool OnlyAccessesErrnoMem => WithoutLoc(LlvmIRMemLocation.ErrnoMem).DoesNotAccessMemory;
    public bool OnlyAccessesInaccessibleOrArgMem => WithoutLoc(LlvmIRMemLocation.InaccessibleMem)
        .WithoutLoc(LlvmIRMemLocation.ArgMem).DoesNotAccessMemory;
    public static LlvmMemoryEffects operator &(LlvmMemoryEffects a, LlvmMemoryEffects b) => new(a.data & b.data);
    public static LlvmMemoryEffects operator |(LlvmMemoryEffects a, LlvmMemoryEffects b) => new(a.data | b.data);
    public static LlvmMemoryEffects operator -(LlvmMemoryEffects a, LlvmMemoryEffects b) => new(a.data & ~b.data);
}
