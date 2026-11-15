// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from llvm/IR/Constants.h and llvm/lib/IR/Constants.cpp: ConstantInt,
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantInt : LlvmConstantData
{
    public LlvmAPInt Value { get; }
    public uint BitWidth => Value.BitWidth;
    public LlvmIntegerType IntegerType => (LlvmIntegerType)Type.GetScalarType();
    public bool IsZero => Value.IsZero;
    public bool IsOne => Value.IsOne;
    public bool IsNegative => Value.IsNegative;
    public bool IsMinusOne => Value.IsAllOnes;

    private LlvmConstantInt(LlvmType type, LlvmAPInt value) : base(type, LlvmValueKind.ConstantIntVal)
    {
        Debug.Assert(value.BitWidth == ((LlvmIntegerType)type.GetScalarType()).BitWidth);
        Value = value;
        if (value.IsZero) SubclassOptionalData = IsNullValueBit;
    }
    public static LlvmConstantInt Get(LlvmContext context, LlvmAPInt value)
    {
        if (value.IsZero)
        {
            if (!context.IntZeroConstants.TryGetValue(value.BitWidth, out var zero))
                context.IntZeroConstants.Add(value.BitWidth, zero = new(LlvmIntegerType.Get(context, value.BitWidth), value));
            return zero;
        }
        if (value.IsOne)
        {
            if (!context.IntOneConstants.TryGetValue(value.BitWidth, out var one))
                context.IntOneConstants.Add(value.BitWidth, one = new(LlvmIntegerType.Get(context, value.BitWidth), value));
            return one;
        }
        if (!context.IntConstants.TryGetValue(value, out var constant))
            context.IntConstants.Add(value, constant = new(LlvmIntegerType.Get(context, value.BitWidth), value));
        return constant;
    }
    public static LlvmConstantInt Get(LlvmIntegerType type, ulong value, bool isSigned = false, bool implicitTrunc = false) =>
        Get(type.Context, new(type.BitWidth, value, isSigned, implicitTrunc));
    public static LlvmConstantInt GetSigned(LlvmIntegerType type, long value, bool implicitTrunc = false) =>
        Get(type, unchecked((ulong)value), isSigned: true, implicitTrunc);
    public static LlvmConstantInt GetTrue(LlvmContext context) =>
        context.TrueConstant ??= Get(LlvmIntegerType.Get(context, 1), 1);
    public static LlvmConstantInt GetFalse(LlvmContext context) =>
        context.FalseConstant ??= Get(LlvmIntegerType.Get(context, 1), 0);
    public static LlvmConstantInt GetBool(LlvmContext context, bool value) => value ? GetTrue(context) : GetFalse(context);
    public ulong GetZExtValue() => Value.GetZExtValue();
    public long GetSExtValue() => Value.GetSExtValue();
    public bool EqualsInt(ulong value) => Value.GetActiveBits() <= 64 && Value.GetZExtValue() == value;
}
