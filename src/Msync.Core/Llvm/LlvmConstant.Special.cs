// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from llvm/IR/Constants.h and llvm/lib/IR/Constants.cpp,
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public class LlvmUndefValue : LlvmConstantData
{
    protected LlvmUndefValue(LlvmType type, LlvmValueKind kind) : base(type, kind) { }
    private LlvmUndefValue(LlvmType type) : this(type, LlvmValueKind.UndefValueVal) { }
    public static LlvmUndefValue Get(LlvmType type)
    {
        if (!type.Context.UndefConstants.TryGetValue(type, out var value))
            type.Context.UndefConstants.Add(type, value = new(type));
        return value;
    }
}

public sealed class LlvmPoisonValue : LlvmUndefValue
{
    private LlvmPoisonValue(LlvmType type) : base(type, LlvmValueKind.PoisonValueVal) { }
    public new static LlvmPoisonValue Get(LlvmType type)
    {
        if (!type.Context.PoisonConstants.TryGetValue(type, out var value))
            type.Context.PoisonConstants.Add(type, value = new(type));
        return value;
    }
}

public sealed class LlvmConstantPointerNull : LlvmConstantData
{
    private LlvmConstantPointerNull(LlvmType type) : base(type, LlvmValueKind.ConstantPointerNullVal) =>
        SubclassOptionalData = IsNullValueBit;
    public LlvmPointerType PointerType => (LlvmPointerType)Type.GetScalarType();
    public static LlvmConstantPointerNull Get(LlvmPointerType type) => Get((LlvmType)type);
    public static LlvmConstantPointerNull Get(LlvmType type)
    {
        Debug.Assert(type.GetScalarType().IsPointerTy(), "Invalid type for null pointer constant");
        if (!type.Context.PointerNullConstants.TryGetValue(type, out var value))
            type.Context.PointerNullConstants.Add(type, value = new(type));
        return value;
    }
}

public sealed class LlvmConstantAggregateZero : LlvmConstantData
{
    private LlvmConstantAggregateZero(LlvmType type) : base(type, LlvmValueKind.ConstantAggregateZeroVal) =>
        SubclassOptionalData = IsNullValueBit;
    public static LlvmConstantAggregateZero Get(LlvmType type)
    {
        Debug.Assert(type.IsStructTy() || type.IsArrayTy() || type.IsVectorTy());
        if (!type.Context.AggregateZeroConstants.TryGetValue(type, out var value))
            type.Context.AggregateZeroConstants.Add(type, value = new(type));
        return value;
    }
}
