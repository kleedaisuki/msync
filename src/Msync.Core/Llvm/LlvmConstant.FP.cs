// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// ConstantFP scalar IEEE construction from Constants.h / Constants.cpp,
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantFP : LlvmConstantData
{
    public LlvmAPFloat Value { get; }
    public bool IsNegative => Value.IsNegative;
    public bool IsZero => Value.IsZero;
    public bool IsNaN => Value.IsNaN;
    private LlvmConstantFP(LlvmType type, LlvmAPFloat value) : base(type, LlvmValueKind.ConstantFPVal)
    {
        Debug.Assert(type.GetScalarType().GetFltSemantics() == value.Semantics);
        Value = value;
        if (value.BitcastToAPInt().IsZero) SubclassOptionalData = IsNullValueBit;
    }
    public static LlvmConstantFP Get(LlvmContext context, LlvmAPFloat value)
    {
        if (!context.FPConstants.TryGetValue(value, out var constant))
            context.FPConstants.Add(value, constant = new(value.Semantics switch
            {
                LlvmAPFloatSemantics.IEEEhalf => LlvmType.GetHalfTy(context),
                LlvmAPFloatSemantics.BFloat => LlvmType.GetBFloatTy(context),
                LlvmAPFloatSemantics.IEEEsingle => LlvmType.GetFloatTy(context),
                _ => LlvmType.GetDoubleTy(context)
            }, value));
        return constant;
    }
    public static LlvmConstantFP Get(LlvmType type, LlvmAPFloat value)
    {
        Debug.Assert(type.GetScalarType().GetFltSemantics() == value.Semantics);
        return type is LlvmVectorType vector ? Get(type.Context, vector.ElementCount, value) : Get(type.Context, value);
    }
    public static LlvmConstantFP Get(LlvmType type, double value) =>
        Get(type, new LlvmAPFloat(value).Convert(type.GetScalarType().GetFltSemantics()).Value);
    public bool IsExactlyValue(LlvmAPFloat value) => Value.BitwiseIsEqual(value);
}
