// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from DerivedTypes.h and Type.cpp, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public abstract partial class LlvmVectorType : LlvmType
{
    public LlvmType ElementType => containedTypes[0];
    public LlvmElementCount ElementCount { get; }
    protected LlvmVectorType(LlvmType element, LlvmElementCount count, LlvmTypeId id) : base(element.Context, id)
    { containedTypes = Array.AsReadOnly(new[] { element }); ElementCount = count; }
    public static LlvmVectorType Get(LlvmType element, LlvmElementCount count) => count.IsScalable
        ? LlvmScalableVectorType.Get(element, count.KnownMinValue)
        : LlvmFixedVectorType.Get(element, count.KnownMinValue);
    public static LlvmVectorType Get(LlvmType element, uint count, bool scalable) => Get(element, new LlvmElementCount(count, scalable));
    public static LlvmVectorType Get(LlvmType element, LlvmVectorType other) => Get(element, other.ElementCount);
    public static bool IsValidElementType(LlvmType type) => type.IsIntegerTy() || type.IsFloatingPointTy() ||
        type.IsPointerTy() || type is LlvmTypedPointerType || type.IsByteTy() ||
        type is LlvmTargetExtensionType target && target.HasProperty(LlvmTargetExtensionProperty.CanBeVectorElement);
    public static LlvmVectorType GetInteger(LlvmVectorType vector)
    {
        uint bits = (uint)vector.ElementType.GetPrimitiveSizeInBits().GetFixedValue();
        Debug.Assert(bits != 0, "Element size must be of a non-zero size");
        return Get(LlvmIntegerType.Get(vector.Context, bits), vector.ElementCount);
    }
}

public sealed class LlvmFixedVectorType : LlvmVectorType
{
    public uint NumElements => ElementCount.KnownMinValue;
    private LlvmFixedVectorType(LlvmType element, uint count) : base(element, LlvmElementCount.Fixed(count), LlvmTypeId.FixedVectorTyID) { }
    public static LlvmFixedVectorType Get(LlvmType element, uint count)
    {
        Debug.Assert(count > 0, "Cannot get a vector of 0 elements!");
        Debug.Assert(IsValidElementType(element), "Invalid element type for vector");
        var key = (element, LlvmElementCount.Fixed(count));
        if (!element.Context.VectorTypes.TryGetValue(key, out var type))
            element.Context.VectorTypes.Add(key, type = new LlvmFixedVectorType(element, count));
        return (LlvmFixedVectorType)type;
    }
}

public sealed class LlvmScalableVectorType : LlvmVectorType
{
    public uint MinNumElements => ElementCount.KnownMinValue;
    private LlvmScalableVectorType(LlvmType element, uint count) : base(element, LlvmElementCount.Scalable(count), LlvmTypeId.ScalableVectorTyID) { }
    public static LlvmScalableVectorType Get(LlvmType element, uint minCount)
    {
        Debug.Assert(minCount > 0, "Cannot get a vector of 0 elements!");
        Debug.Assert(IsValidElementType(element), "Invalid element type for vector");
        var key = (element, LlvmElementCount.Scalable(minCount));
        if (!element.Context.VectorTypes.TryGetValue(key, out var type))
            element.Context.VectorTypes.Add(key, type = new LlvmScalableVectorType(element, minCount));
        return (LlvmScalableVectorType)type;
    }
}
