// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp ConstantVector,
// ConstantInt/ConstantByte vector splats, Constant::getSplatValue; IROptions.td.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmContext
{
    internal readonly Dictionary<LlvmAggregateConstantKey, LlvmConstantVector> VectorConstants = new();
    internal readonly Dictionary<LlvmConstantDataKey, LlvmConstantDataVector> DataVectorConstants = new();
    internal readonly Dictionary<(LlvmElementCount, LlvmAPInt), LlvmConstantInt> IntSplatConstants = new();
    internal readonly Dictionary<(LlvmElementCount, LlvmAPInt), LlvmConstantByte> ByteSplatConstants = new();
    internal readonly Dictionary<(LlvmElementCount, LlvmAPFloat), LlvmConstantFP> FPSplatConstants = new();
    public bool UseConstantIntForFixedLengthSplat { get; set; }
    public bool UseConstantIntForScalableSplat { get; set; }
}

public sealed class LlvmConstantVector : LlvmConstantAggregate
{
    private LlvmConstantVector(LlvmAggregateConstantKey key) : base(key, LlvmValueKind.ConstantVectorVal)
    {
        Debug.Assert(key.Elements.Length == ((LlvmFixedVectorType)key.Type).NumElements);
    }
    public LlvmFixedVectorType VectorType => (LlvmFixedVectorType)Type;

    public static LlvmConstant Get(IReadOnlyList<LlvmConstant> elements)
    {
        if (GetCanonical(elements) is { } canonical) return canonical;
        var type = LlvmFixedVectorType.Get(elements[0].Type, (uint)elements.Count);
        var key = new LlvmAggregateConstantKey(type, elements);
        if (!type.Context.VectorConstants.TryGetValue(key, out var vector))
            type.Context.VectorConstants.Add(key, vector = new(key));
        return vector;
    }

    internal static LlvmConstant? GetCanonical(IReadOnlyList<LlvmConstant> elements)
    {
        Debug.Assert(elements.Count != 0);
        var first = elements[0];
        var type = LlvmFixedVectorType.Get(first.Type, (uint)elements.Count);
        bool zero = first.IsNullValue, undef = first is LlvmUndefValue, poison = first is LlvmPoisonValue;
        bool floating = first is LlvmConstantFP, integer = first is LlvmConstantInt && first.Context.UseConstantIntForFixedLengthSplat;
        bool bytes = first is LlvmConstantByte, pointerNull = first is LlvmConstantPointerNull;
        if (zero || undef || floating || integer || bytes || pointerNull)
            for (int index = 1; index < elements.Count; ++index)
            {
                if (ReferenceEquals(elements[index], first)) continue;
                zero = undef = poison = floating = integer = bytes = pointerNull = false;
                break;
            }
        if (pointerNull) return LlvmConstantPointerNull.Get(type);
        if (zero) return LlvmConstantAggregateZero.Get(type);
        if (poison) return LlvmPoisonValue.Get(type);
        if (undef) return LlvmUndefValue.Get(type);
        if (floating) return LlvmConstantFP.Get(type.Context, type.ElementCount, ((LlvmConstantFP)first).Value);
        if (integer) return LlvmConstantInt.Get(type.Context, type.ElementCount, ((LlvmConstantInt)first).Value);
        if (bytes) return LlvmConstantByte.Get(type.Context, type.ElementCount, ((LlvmConstantByte)first).Value);
        return LlvmConstantDataVector.GetSequenceIfElementsMatch(type, elements);
    }

    public static LlvmConstant GetSplat(LlvmElementCount count, LlvmConstant value)
    {
        if (value is LlvmConstantPointerNull) return LlvmConstantPointerNull.Get(LlvmVectorType.Get(value.Type, count));
        if (value is LlvmConstantByte bytes) return LlvmConstantByte.Get(value.Context, count, bytes.Value);
        if (value is LlvmConstantFP floating) return LlvmConstantFP.Get(value.Context, count, floating.Value);
        if (!count.IsScalable)
        {
            if (!value.IsNullValue && value is LlvmConstantInt integer && value.Context.UseConstantIntForFixedLengthSplat)
                return LlvmConstantInt.Get(value.Context, count, integer.Value);
            if (value is LlvmConstantInt && LlvmConstantDataVector.IsElementTypeCompatible(value.Type))
                return LlvmConstantDataVector.GetSplat(count.KnownMinValue, value);
            return Get(Enumerable.Repeat(value, (int)count.KnownMinValue).ToArray());
        }
        if (!value.IsNullValue && value is LlvmConstantInt scalableInteger && value.Context.UseConstantIntForScalableSplat)
            return LlvmConstantInt.Get(value.Context, count, scalableInteger.Value);
        var type = LlvmVectorType.Get(value.Type, count);
        if (value.IsNullValue) return LlvmConstantAggregateZero.Get(type);
        if (value is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
        if (value is LlvmUndefValue) return LlvmUndefValue.Get(type);
        var poison = LlvmPoisonValue.Get(type);
        var insert = LlvmConstantExpr.GetInsertElement(poison, value, LlvmConstantInt.Get(LlvmIntegerType.Get(value.Context, 64), 0))!;
        return LlvmConstantExpr.GetShuffleVector(insert, poison, new int[count.KnownMinValue])!;
    }

    public new LlvmConstant? GetSplatValue(bool allowPoison = false)
    {
        var element = GetElement(0);
        for (int index = 1; index < OperandCount; ++index)
        {
            var current = GetElement(index);
            if (ReferenceEquals(current, element)) continue;
            if (!allowPoison) return null;
            if (current is LlvmPoisonValue) continue;
            if (element is LlvmPoisonValue) element = current;
            if (!ReferenceEquals(current, element)) return null;
        }
        return element;
    }
}

public sealed partial class LlvmConstantInt
{
    public static LlvmConstantInt Get(LlvmContext context, LlvmElementCount count, LlvmAPInt value)
    {
        var key = (count, value);
        if (!context.IntSplatConstants.TryGetValue(key, out var constant))
            context.IntSplatConstants.Add(key, constant = new(LlvmVectorType.Get(LlvmIntegerType.Get(context, value.BitWidth), count), value));
        return constant;
    }
    public static LlvmConstant Get(LlvmType type, LlvmAPInt value)
    {
        var scalar = Get(type.Context, value);
        Debug.Assert(ReferenceEquals(scalar.Type, type.GetScalarType()));
        return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
    }
    public static LlvmConstant Get(LlvmType type, ulong value, bool isSigned = false, bool implicitTrunc = false)
    {
        var scalar = Get((LlvmIntegerType)type.GetScalarType(), value, isSigned, implicitTrunc);
        return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
    }
}

public sealed partial class LlvmConstantByte
{
    public static LlvmConstantByte Get(LlvmContext context, LlvmElementCount count, LlvmAPInt value)
    {
        var key = (count, value);
        if (!context.ByteSplatConstants.TryGetValue(key, out var constant))
            context.ByteSplatConstants.Add(key, constant = new(LlvmVectorType.Get(LlvmByteType.Get(context, value.BitWidth), count), value));
        return constant;
    }
    public static LlvmConstant Get(LlvmType type, LlvmAPInt value)
    {
        var scalar = Get(type.Context, value);
        Debug.Assert(ReferenceEquals(scalar.Type, type.GetScalarType()));
        return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
    }
    public static LlvmConstant Get(LlvmType type, ulong value, bool isSigned = false, bool implicitTrunc = false)
    {
        var scalar = Get((LlvmByteType)type.GetScalarType(), value, isSigned, implicitTrunc);
        return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, scalar) : scalar;
    }
}

public sealed partial class LlvmConstantFP
{
    public static LlvmConstantFP Get(LlvmContext context, LlvmElementCount count, LlvmAPFloat value)
    {
        var key = (count, value);
        if (!context.FPSplatConstants.TryGetValue(key, out var constant))
        {
            var scalar = Get(context, value);
            context.FPSplatConstants.Add(key, constant = new(LlvmVectorType.Get(scalar.Type, count), value));
        }
        return constant;
    }
}

public abstract partial class LlvmConstant
{
    public LlvmConstant? GetSplatValue(bool allowPoison = false)
    {
        Debug.Assert(Type.IsVectorTy());
        var element = ((LlvmVectorType)Type).ElementType;
        if (this is LlvmPoisonValue) return LlvmPoisonValue.Get(element);
        if (this is LlvmConstantAggregateZero) return GetNullValue(element);
        if (this is LlvmConstantInt integer) return LlvmConstantInt.Get(Context, integer.Value);
        if (this is LlvmConstantByte bytes) return LlvmConstantByte.Get(Context, bytes.Value);
        if (this is LlvmConstantFP floating) return LlvmConstantFP.Get(Context, floating.Value);
        if (this is LlvmConstantPointerNull) return LlvmConstantPointerNull.Get(element);
        if (this is LlvmConstantDataVector data) return data.GetSplatValue();
        if (this is LlvmConstantVector vector) return vector.GetSplatValue(allowPoison);
        if (this is LlvmConstantExpr { Opcode: LlvmOpcode.ShuffleVector } shuffle && shuffle.GetOperand(1) is LlvmUndefValue &&
            shuffle.GetOperand(0) is LlvmConstantExpr { Opcode: LlvmOpcode.InsertElement } insert && insert.GetOperand(0) is LlvmUndefValue &&
            insert.GetOperand(2) is LlvmConstantInt { IsZero: true })
        {
            foreach (int index in shuffle.ShuffleMask) if (index != 0) return null;
            return (LlvmConstant)insert.GetOperand(1)!;
        }
        return null;
    }
}
