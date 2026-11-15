// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/ModRef.h.
namespace Msync.Core.Llvm;

[Flags]
public enum LlvmCaptureComponents : byte
{
    None = 0, AddressIsNull = 1, Address = 3,
    ReadProvenance = 4, Provenance = 12, All = 15
}

public readonly record struct LlvmCaptureInfo(LlvmCaptureComponents OtherComponents, LlvmCaptureComponents RetComponents)
{
    public LlvmCaptureInfo(LlvmCaptureComponents components) : this(components, components) { }
    public LlvmCaptureComponents Components => OtherComponents | RetComponents;
    public bool IsRetOnly => OtherComponents == LlvmCaptureComponents.None;
    public bool CapturesNothing => Components == LlvmCaptureComponents.None;
    public bool CapturesFullProvenance => (Components & LlvmCaptureComponents.Provenance) == LlvmCaptureComponents.Provenance;
    public static LlvmCaptureInfo None() => new(LlvmCaptureComponents.None);
    public static LlvmCaptureInfo All() => new(LlvmCaptureComponents.All);
    public static LlvmCaptureInfo RetOnly(LlvmCaptureComponents components = LlvmCaptureComponents.All) => new(LlvmCaptureComponents.None, components);
    public static LlvmCaptureInfo CreateFromIntValue(uint value) => new((LlvmCaptureComponents)(value >> 4), (LlvmCaptureComponents)(value & 15));
    public uint ToIntValue() => ((uint)OtherComponents << 4) | (uint)RetComponents;
    public static LlvmCaptureInfo operator |(LlvmCaptureInfo left, LlvmCaptureInfo right) => new(left.OtherComponents | right.OtherComponents, left.RetComponents | right.RetComponents);
    public static LlvmCaptureInfo operator &(LlvmCaptureInfo left, LlvmCaptureInfo right) => new(left.OtherComponents & right.OtherComponents, left.RetComponents & right.RetComponents);
    public static implicit operator LlvmCaptureComponents(LlvmCaptureInfo value) => value.Components;
}
