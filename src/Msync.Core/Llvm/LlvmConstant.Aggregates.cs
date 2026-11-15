// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Constants.h,
// lib/IR/Constants.cpp ConstantAggregate/Array/Struct factories and operand changes;
// lib/IR/ConstantsContext.h ConstantUniqueMap::replaceOperandsInPlace.
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Msync.Core.Llvm;

public sealed partial class LlvmContext
{
    internal readonly Dictionary<LlvmAggregateConstantKey, LlvmConstantArray> ArrayConstants = new();
    internal readonly Dictionary<LlvmAggregateConstantKey, LlvmConstantStruct> StructConstants = new();
    internal readonly Dictionary<uint, LlvmConstantByte> ByteZeroConstants = new();
    internal readonly Dictionary<uint, LlvmConstantByte> ByteOneConstants = new();
    internal readonly Dictionary<LlvmAPInt, LlvmConstantByte> ByteConstants = new();
}

internal readonly struct LlvmAggregateConstantKey : IEquatable<LlvmAggregateConstantKey>
{
    internal LlvmAggregateConstantKey(LlvmType type, IReadOnlyList<LlvmConstant> elements)
    {
        Type = type; Elements = elements.ToArray();
        HashCode hash = new(); hash.Add(RuntimeHelpers.GetHashCode(type));
        foreach (LlvmConstant element in Elements) hash.Add(RuntimeHelpers.GetHashCode(element));
        hashCode = hash.ToHashCode();
    }
    internal LlvmType Type { get; }
    internal LlvmConstant[] Elements { get; }
    private readonly int hashCode;
    public bool Equals(LlvmAggregateConstantKey other)
    {
        if (!ReferenceEquals(Type, other.Type) || Elements.Length != other.Elements.Length) return false;
        for (int index = 0; index < Elements.Length; ++index)
            if (!ReferenceEquals(Elements[index], other.Elements[index])) return false;
        return true;
    }
    public override bool Equals(object? value) => value is LlvmAggregateConstantKey other && Equals(other);
    public override int GetHashCode() => hashCode;
}

public abstract class LlvmConstantAggregate : LlvmConstant
{
    private LlvmAggregateConstantKey key;
    private protected LlvmConstantAggregate(LlvmAggregateConstantKey key, LlvmValueKind kind)
        : base(key.Type, (byte)kind, key.Elements.Cast<LlvmValue?>().ToArray())
    {
        this.key = key;
        if (key.Type is LlvmStructType { IsOpaque: false } structure)
            for (int index = 0; index < key.Elements.Length; ++index)
                Debug.Assert(ReferenceEquals(key.Elements[index].Type, structure.Elements[index]));
    }
    public LlvmConstant GetElement(int index) => (LlvmConstant)GetOperand(index)!;

    internal override void HandleOperandChange(LlvmValue from, LlvmValue to)
    {
        Debug.Assert(to is LlvmConstant);
        LlvmConstant replacementOperand = (LlvmConstant)to;
        var values = new LlvmConstant[OperandCount];
        bool allSame = true;
        int updated = 0, changedIndex = -1;
        for (int index = 0; index < values.Length; ++index)
        {
            LlvmConstant value = GetElement(index);
            if (ReferenceEquals(value, from)) { value = replacementOperand; ++updated; changedIndex = index; }
            values[index] = value; allSame &= ReferenceEquals(value, replacementOperand);
        }
        Debug.Assert(updated != 0);
        LlvmConstant? replacement = null;
        if (this is LlvmConstantVector) replacement = LlvmConstantVector.GetCanonical(values);
        else if (allSame && replacementOperand.IsNullValue) replacement = LlvmConstantAggregateZero.Get(Type);
        else if (allSame && replacementOperand is LlvmUndefValue) replacement = LlvmUndefValue.Get(Type);
        else if (this is LlvmConstantArray) replacement = LlvmConstantArray.GetCanonical((LlvmArrayType)Type, values);
        LlvmAggregateConstantKey newKey = new(Type, values);
        if (replacement is null)
        {
            if (this is LlvmConstantArray) replacement = Context.ArrayConstants.GetValueOrDefault(newKey);
            else if (this is LlvmConstantVector) replacement = Context.VectorConstants.GetValueOrDefault(newKey);
            else replacement = Context.StructConstants.GetValueOrDefault(newKey);
        }
        if (replacement is not null)
        {
            Debug.Assert(!ReferenceEquals(replacement, this));
            ReplaceAllUsesWith(replacement);
            RemoveFromTable(); DropAllReferences();
            NotifyValueDeletion();
            return;
        }
        RemoveFromTable();
        if (updated == 1) GetOperandUse(changedIndex).Set(replacementOperand);
        else
            for (int index = 0; index < OperandCount; ++index)
                if (ReferenceEquals(GetOperand(index), from)) GetOperandUse(index).Set(replacementOperand);
        key = newKey;
        if (this is LlvmConstantArray array) Context.ArrayConstants.Add(key, array);
        else if (this is LlvmConstantVector vector) Context.VectorConstants.Add(key, vector);
        else Context.StructConstants.Add(key, (LlvmConstantStruct)this);
    }
    private void RemoveFromTable()
    {
        if (this is LlvmConstantArray) Context.ArrayConstants.Remove(key);
        else if (this is LlvmConstantVector) Context.VectorConstants.Remove(key);
        else Context.StructConstants.Remove(key);
    }
}

public sealed class LlvmConstantArray : LlvmConstantAggregate
{
    private LlvmConstantArray(LlvmAggregateConstantKey key) : base(key, LlvmValueKind.ConstantArrayVal) =>
        Debug.Assert((ulong)key.Elements.Length == ((LlvmArrayType)key.Type).NumElements);
    public LlvmArrayType ArrayType => (LlvmArrayType)Type;
    public static LlvmConstant Get(LlvmArrayType type, IReadOnlyList<LlvmConstant> elements)
    {
        LlvmConstant? canonical = GetCanonical(type, elements);
        if (canonical is not null) return canonical;
        LlvmAggregateConstantKey key = new(type, elements);
        if (!type.Context.ArrayConstants.TryGetValue(key, out var value))
            type.Context.ArrayConstants.Add(key, value = new(key));
        return value;
    }
    internal static LlvmConstant? GetCanonical(LlvmArrayType type, IReadOnlyList<LlvmConstant> elements)
    {
        if (elements.Count == 0) return LlvmConstantAggregateZero.Get(type);
        foreach (LlvmConstant element in elements) Debug.Assert(ReferenceEquals(element.Type, type.ElementType));
        LlvmConstant first = elements[0];
        bool same = elements.All(element => ReferenceEquals(first, element));
        if (same && first is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
        if (same && first is LlvmUndefValue) return LlvmUndefValue.Get(type);
        if (same && first.IsNullValue) return LlvmConstantAggregateZero.Get(type);
        return LlvmConstantDataArray.GetSequenceIfElementsMatch(type, elements);
    }
}

public sealed class LlvmConstantStruct : LlvmConstantAggregate
{
    private LlvmConstantStruct(LlvmAggregateConstantKey key) : base(key, LlvmValueKind.ConstantStructVal) { }
    public LlvmStructType StructType => (LlvmStructType)Type;
    public static LlvmConstant Get(LlvmStructType type, IReadOnlyList<LlvmConstant> elements)
    {
        Debug.Assert(type.IsOpaque || elements.Count == type.Elements.Count);
        bool zero = true, undef = false, poison = false;
        if (elements.Count != 0)
        {
            undef = elements[0] is LlvmUndefValue; poison = elements[0] is LlvmPoisonValue; zero = elements[0].IsNullValue;
            if (undef || zero)
                foreach (LlvmConstant element in elements)
                {
                    if (!element.IsNullValue) zero = false;
                    if (element is not LlvmPoisonValue) poison = false;
                    if (element is LlvmPoisonValue || element is not LlvmUndefValue) undef = false;
                }
        }
        if (zero) return LlvmConstantAggregateZero.Get(type);
        if (poison) return LlvmPoisonValue.Get(type);
        if (undef) return LlvmUndefValue.Get(type);
        LlvmAggregateConstantKey key = new(type, elements);
        if (!type.Context.StructConstants.TryGetValue(key, out var value))
            type.Context.StructConstants.Add(key, value = new(key));
        return value;
    }
    public static LlvmStructType GetTypeForElements(LlvmContext context, IReadOnlyList<LlvmConstant> elements, bool packed = false) =>
        LlvmStructType.Get(context, elements.Select(element => element.Type).ToArray(), packed);
}

// Scalar ConstantByte is needed to preserve the type of existing byte-string CDA elements.
public sealed partial class LlvmConstantByte : LlvmConstantData
{
    private LlvmConstantByte(LlvmType type, LlvmAPInt value) : base(type, LlvmValueKind.ConstantByteVal)
    {
        Debug.Assert(((LlvmByteType)type.GetScalarType()).BitWidth == value.BitWidth); Value = value;
        if (value.IsZero) SubclassOptionalData = IsNullValueBit;
    }
    public LlvmAPInt Value { get; }
    public LlvmByteType ByteType => (LlvmByteType)Type.GetScalarType();
    public ulong GetZExtValue() => Value.GetZExtValue();
    public static LlvmConstantByte Get(LlvmByteType type, ulong value, bool isSigned = false, bool implicitTrunc = false) =>
        Get(type.Context, new LlvmAPInt(type.BitWidth, value, isSigned, implicitTrunc));
    public static LlvmConstantByte Get(LlvmContext context, LlvmAPInt value)
    {
        if (value.IsZero)
        {
            if (!context.ByteZeroConstants.TryGetValue(value.BitWidth, out var zero))
                context.ByteZeroConstants.Add(value.BitWidth, zero = new(LlvmByteType.Get(context, value.BitWidth), value));
            return zero;
        }
        if (value.IsOne)
        {
            if (!context.ByteOneConstants.TryGetValue(value.BitWidth, out var one))
                context.ByteOneConstants.Add(value.BitWidth, one = new(LlvmByteType.Get(context, value.BitWidth), value));
            return one;
        }
        if (!context.ByteConstants.TryGetValue(value, out var constant))
            context.ByteConstants.Add(value, constant = new(LlvmByteType.Get(context, value.BitWidth), value));
        return constant;
    }
}

public abstract partial class LlvmConstant
{
    public static LlvmConstant GetNullValue(LlvmType type) => type switch
    {
        LlvmIntegerType integer => LlvmConstantInt.Get(integer, 0),
        LlvmByteType bytes => LlvmConstantByte.Get(bytes, 0),
        LlvmPointerType pointer => LlvmConstantPointerNull.Get(pointer),
        LlvmVectorType vector when vector.ElementType.IsPointerTy() => LlvmConstantPointerNull.Get(vector),
        LlvmVectorType vector when vector.ElementType.IsFloatingPointTy() =>
            LlvmConstantFP.Get(vector, LlvmAPFloat.GetZero(vector.ElementType.GetFltSemantics())),
        LlvmVectorType or LlvmArrayType or LlvmStructType => LlvmConstantAggregateZero.Get(type),
        _ when type.IsFloatingPointTy() => LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(type.GetFltSemantics())),
        _ => throw new NotSupportedException($"Null constant construction for {type.Id} has not been ported.")
    };

    public LlvmConstant? GetAggregateElement(uint index)
    {
        Debug.Assert(Type.IsArrayTy() || Type.IsStructTy() || Type.IsVectorTy());
        if (this is LlvmConstantAggregate aggregate)
            return index < aggregate.OperandCount ? aggregate.GetElement((int)index) : null;
        LlvmType? elementType = GetAggregateElementType(Type, index);
        if (elementType is null) return null;
        if (this is LlvmConstantAggregateZero) return GetNullValue(elementType);
        if (this is LlvmConstantInt integer && Type.IsVectorTy()) return LlvmConstantInt.Get(Context, integer.Value);
        if (this is LlvmConstantByte bytes && Type.IsVectorTy()) return LlvmConstantByte.Get(Context, bytes.Value);
        if (this is LlvmConstantFP floating && Type.IsVectorTy()) return LlvmConstantFP.Get(Context, floating.Value);
        if (this is LlvmConstantPointerNull && Type.IsVectorTy()) return LlvmConstantPointerNull.Get(elementType);
        if (Type is LlvmScalableVectorType) return null;
        if (this is LlvmPoisonValue) return LlvmPoisonValue.Get(elementType);
        if (this is LlvmUndefValue) return LlvmUndefValue.Get(elementType);
        if (this is LlvmConstantDataArray dataArray) return dataArray.GetElementAsConstant(index);
        if (this is LlvmConstantDataVector dataVector) return dataVector.GetElementAsConstant(index);
        return null;
    }
    public LlvmConstant? GetAggregateElement(LlvmConstant index)
    {
        Debug.Assert(index.Type.IsIntegerTy());
        return index is LlvmConstantInt integer && integer.Value.GetActiveBits() <= 64
            ? GetAggregateElement(unchecked((uint)integer.GetZExtValue())) : null;
    }
    private static LlvmType? GetAggregateElementType(LlvmType type, uint index) => type switch
    {
        LlvmArrayType array when index < array.NumElements => array.ElementType,
        LlvmStructType structure when index < structure.Elements.Count => structure.Elements[(int)index],
        LlvmVectorType vector when index < vector.ElementCount.KnownMinValue => vector.ElementType,
        _ => null
    };
}

public sealed partial class LlvmConstantDataArray
{
    public LlvmType ElementType => ArrayType.ElementType;
    public int ElementByteSize => (int)(ElementType.GetScalarSizeInBits() / 8);
    public static bool IsElementTypeCompatible(LlvmType type) =>
        type.IsHalfTy() || type.IsBFloatTy() || type.IsFloatTy() || type.IsDoubleTy()
        || type is LlvmIntegerType { BitWidth: 8 or 16 or 32 or 64 }
        || type is LlvmByteType { BitWidth: 8 or 16 or 32 or 64 };
    public LlvmConstant GetElementAsConstant(uint index)
    {
        Debug.Assert(index < NumElements);
        ulong value = GetElementBits(index);
        return ElementType switch
        {
            LlvmIntegerType integer => LlvmConstantInt.Get(integer, value),
            LlvmByteType bytes => LlvmConstantByte.Get(bytes, value),
            _ when ElementType.IsDoubleTy() => LlvmConstantFP.Get(ElementType, LlvmAPFloat.FromBits(value)),
            _ => throw new NotSupportedException($"ConstantDataArray element decoding for {ElementType.Id} has not been ported.")
        };
    }
    public ulong GetElementAsInteger(uint index)
    {
        Debug.Assert(ElementType.IsIntegerTy() || ElementType.IsByteTy());
        return GetElementBits(index);
    }
    public LlvmAPInt GetElementAsAPInt(uint index) => new((uint)(ElementByteSize * 8), GetElementAsInteger(index));
    public LlvmAPFloat GetElementAsAPFloat(uint index)
    {
        if (!ElementType.IsDoubleTy()) throw new NotSupportedException("Only binary64 ConstantDataArray FP decoding has been ported.");
        return LlvmAPFloat.FromBits(GetElementBits(index));
    }
    private ulong GetElementBits(uint index)
    {
        Debug.Assert(index < NumElements);
        int size = ElementByteSize, offset = (int)index * size;
        ulong value = 0;
        for (int part = 0; part < size; ++part) value |= (ulong)data[offset + part] << (BitConverter.IsLittleEndian ? part * 8 : (size - part - 1) * 8);
        return value;
    }
    internal static LlvmConstant? GetSequenceIfElementsMatch(LlvmArrayType type, IReadOnlyList<LlvmConstant> elements)
    {
        if (!IsElementTypeCompatible(type.ElementType)) return null;
        int size = (int)(type.ElementType.GetScalarSizeInBits() / 8);
        var raw = new byte[elements.Count * size];
        for (int index = 0; index < elements.Count; ++index)
        {
            ulong value;
            switch (elements[index])
            {
                case LlvmConstantInt integer when elements[0] is LlvmConstantInt: value = integer.GetZExtValue(); break;
                case LlvmConstantByte bytes when elements[0] is LlvmConstantByte: value = bytes.GetZExtValue(); break;
                case LlvmConstantFP floating when elements[0] is LlvmConstantFP: value = floating.Value.BitcastToAPInt().GetLimitedValue(); break;
                default: return null;
            }
            for (int part = 0; part < size; ++part) raw[index * size + part] = (byte)(value >> (BitConverter.IsLittleEndian ? part * 8 : (size - part - 1) * 8));
        }
        return GetImpl(type, raw);
    }
}
