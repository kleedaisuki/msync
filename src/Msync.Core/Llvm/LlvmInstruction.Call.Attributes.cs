// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: CallBase::tryIntersectAttributes,
// AttributeSet::intersectWith and Attributes.td.
namespace Msync.Core.Llvm;

public abstract partial class LlvmCallBase
{
    private bool HasCompatiblePreservedAttributes(LlvmCallBase other) => NoInline == other.NoInline
        && NoDuplicate == other.NoDuplicate && Convergent == other.Convergent
        && ReturnsTwice == other.ReturnsTwice && NoMerge == other.NoMerge
        && StrictFP == other.StrictFP && NoImplicitFloat == other.NoImplicitFloat && NoBuiltin == other.NoBuiltin;

    internal bool HasSameCallSiteAttributes(LlvmCallBase other, bool intersectAttrs = false)
    {
        if (intersectAttrs) return CanIntersectAttributes(other);
        if (!HasCompatiblePreservedAttributes(other) || NoReturn != other.NoReturn ||
            MemoryEffectsAttribute != other.MemoryEffectsAttribute || NoUnwind != other.NoUnwind ||
            NoCreateUndefOrPoison != other.NoCreateUndefOrPoison || NoBuiltin != other.NoBuiltin || WillReturnAttribute != other.WillReturnAttribute ||
            NoFree != other.NoFree || NoSync != other.NoSync || Speculatable != other.Speculatable ||
            !ReturnAttributes.SourceEquals(other.ReturnAttributes)) return false;
        int count = Math.Max(ArgumentCount, other.ArgumentCount);
        for (int index = 0; index < count; ++index)
            if (!GetParamAttributes(index).SourceEquals(other.GetParamAttributes(index))) return false;
        return true;
    }
    private bool CanIntersectAttributes(LlvmCallBase other)
    {
        if (!HasCompatiblePreservedAttributes(other) || !ReturnAttributes.TryIntersect(other.ReturnAttributes, out _)) return false;
        int count = Math.Max(ArgumentCount, other.ArgumentCount);
        for (int index = 0; index < count; ++index)
            if (!GetParamAttributes(index).TryIntersect(other.GetParamAttributes(index), out _)) return false;
        return true;
    }
    public bool TryIntersectAttributes(LlvmCallBase other)
    {
        if (!CanIntersectAttributes(other)) return false;
        ReturnAttributes.TryIntersect(other.ReturnAttributes, out var returns);
        ReturnAttributes = returns;
        for (int index = 0; index < ArgumentCount; ++index)
        {
            GetParamAttributes(index).TryIntersect(other.GetParamAttributes(index), out var attributes);
            SetParamAttributes(index, attributes);
        }
        NoReturn &= other.NoReturn; NoUnwind &= other.NoUnwind;
        NoCreateUndefOrPoison &= other.NoCreateUndefOrPoison;
        WillReturnAttribute &= other.WillReturnAttribute; NoFree &= other.NoFree;
        NoSync &= other.NoSync; Speculatable &= other.Speculatable;
        MemoryEffectsAttribute = MemoryEffectsAttribute is { } left && other.MemoryEffectsAttribute is { } right
            ? new LlvmMemoryEffectsAttribute(left.Raw | right.Raw) : null;
        return true;
    }
}
