// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Attribute::MemoryEffects
// packed integer payload; Function/CallBase::getMemoryEffects and ModRef.h.
namespace Msync.Core.Llvm;

public readonly record struct LlvmMemoryEffectsAttribute(uint Raw)
{
    internal const uint UnknownRaw = 0xfff, ReadRaw = 0x555, WriteRaw = 0xaaa;
}

public sealed partial class LlvmFunction
{
    public LlvmMemoryEffectsAttribute? MemoryEffectsAttribute { get; set; }
    public LlvmMemoryEffectsAttribute GetMemoryEffects()
    {
        if (MemoryEffectsAttribute is { } attribute) return attribute;
        if (IntrinsicDescriptor is not { } descriptor) return new(LlvmMemoryEffectsAttribute.UnknownRaw);
        if (descriptor.SourceMemoryEffects is { } sourceEffects) return sourceEffects;
        if (descriptor.DoesNotAccessMemory) return new(0);
        if (descriptor.OnlyAccessesInaccessibleMemory) return new(descriptor.OnlyWritesMemory ? 8U : 12U);
        return new(LlvmMemoryEffectsAttribute.UnknownRaw);
    }
}

public abstract partial class LlvmCallBase
{
    public LlvmMemoryEffectsAttribute? MemoryEffectsAttribute { get; set; }
    public LlvmMemoryEffectsAttribute GetMemoryEffects()
    {
        uint effects = MemoryEffectsAttribute?.Raw ?? LlvmMemoryEffectsAttribute.UnknownRaw;
        if (CalledOperand is not LlvmFunction function) return new(effects);
        uint functionEffects = function.GetMemoryEffects().Raw;
        if (OperandBundles.Count != 0)
        {
            if (LlvmIntrinsic.HasReadingOperandBundles(this)) functionEffects |= LlvmMemoryEffectsAttribute.ReadRaw;
            if (LlvmIntrinsic.HasClobberingOperandBundles(this)) functionEffects |= LlvmMemoryEffectsAttribute.WriteRaw;
        }
        if (OperationIsVolatile()) functionEffects |= 12U;
        return new(effects & functionEffects);
    }
    public bool IsPassingUndefUB(int argument) => ParamHasAttr(argument, LlvmAttributeKind.NoUndef) ||
        ParamHasAttr(argument, LlvmAttributeKind.Dereferenceable) || ParamHasAttr(argument, LlvmAttributeKind.DereferenceableOrNull);
}
