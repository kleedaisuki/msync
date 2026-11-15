// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.h/.cpp,
// ConstantDataSequential raw native-endian storage, ConstantDataVector factories.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed class LlvmConstantDataVector : LlvmConstantData
{
    private readonly byte[] data;
    private LlvmConstantDataVector(LlvmFixedVectorType type, byte[] data) : base(type, LlvmValueKind.ConstantDataVectorVal) => this.data = data;
    public LlvmFixedVectorType VectorType => (LlvmFixedVectorType)Type;
    public LlvmType ElementType => VectorType.ElementType;
    public uint NumElements => VectorType.NumElements;
    public int ElementByteSize => (int)(ElementType.GetScalarSizeInBits() / 8);
    public ReadOnlySpan<byte> GetRawDataValues() => data;
    public static bool IsElementTypeCompatible(LlvmType type) => LlvmConstantDataArray.IsElementTypeCompatible(type);

    public static LlvmConstant Get(LlvmContext context, ReadOnlySpan<byte> elements) =>
        GetImpl(LlvmFixedVectorType.Get(LlvmIntegerType.Get(context, 8), (uint)elements.Length), elements);

    internal static LlvmConstant GetImpl(LlvmFixedVectorType type, ReadOnlySpan<byte> raw)
    {
        Debug.Assert(IsElementTypeCompatible(type.ElementType));
        bool zero = true;
        foreach (byte value in raw) zero &= value == 0;
        if (zero) return LlvmConstantAggregateZero.Get(type);
        var key = new LlvmConstantDataKey(type, raw.ToArray());
        if (!type.Context.DataVectorConstants.TryGetValue(key, out var constant))
            type.Context.DataVectorConstants.Add(key, constant = new(type, key.Data));
        return constant;
    }

    internal static LlvmConstant? GetSequenceIfElementsMatch(LlvmFixedVectorType type, IReadOnlyList<LlvmConstant> elements)
    {
        if (!IsElementTypeCompatible(type.ElementType)) return null;
        int size = (int)(type.ElementType.GetScalarSizeInBits() / 8);
        byte[] raw = new byte[elements.Count * size];
        for (int index = 0; index < elements.Count; ++index)
        {
            ulong value;
            switch (elements[index])
            {
                case LlvmConstantInt integer when elements[0] is LlvmConstantInt: value = integer.Value.GetZExtValue(); break;
                case LlvmConstantByte bytes when elements[0] is LlvmConstantByte: value = bytes.Value.GetZExtValue(); break;
                case LlvmConstantFP floating when elements[0] is LlvmConstantFP: value = floating.Value.BitcastToAPInt().GetZExtValue(); break;
                default: return null;
            }
            WriteElement(raw.AsSpan(index * size, size), value);
        }
        return GetImpl(type, raw);
    }

    public static LlvmConstant GetSplat(uint count, LlvmConstant value)
    {
        var type = LlvmFixedVectorType.Get(value.Type, count);
        Debug.Assert(IsElementTypeCompatible(value.Type));
        if (value is not (LlvmConstantInt or LlvmConstantByte or LlvmConstantFP))
            return LlvmConstantVector.GetSplat(LlvmElementCount.Fixed(count), value);
        int size = (int)(value.Type.GetScalarSizeInBits() / 8);
        ulong bits = value switch
        {
            LlvmConstantInt integer => integer.Value.GetZExtValue(),
            LlvmConstantByte bytes => bytes.Value.GetZExtValue(),
            LlvmConstantFP floating => floating.Value.BitcastToAPInt().GetZExtValue(),
            _ => throw new UnreachableException()
        };
        byte[] raw = new byte[count * size];
        for (int index = 0; index < count; ++index) WriteElement(raw.AsSpan(index * size, size), bits);
        return GetImpl(type, raw);
    }

    private static void WriteElement(Span<byte> raw, ulong value)
    {
        for (int part = 0; part < raw.Length; ++part)
            raw[part] = (byte)(value >> (BitConverter.IsLittleEndian ? part * 8 : (raw.Length - part - 1) * 8));
    }

    private ulong GetElementBits(uint index)
    {
        Debug.Assert(index < NumElements);
        int size = ElementByteSize, offset = (int)index * size;
        ulong value = 0;
        for (int part = 0; part < size; ++part)
            value |= (ulong)data[offset + part] << (BitConverter.IsLittleEndian ? part * 8 : (size - part - 1) * 8);
        return value;
    }

    public ulong GetElementAsInteger(uint index)
    {
        Debug.Assert(ElementType.IsIntegerTy() || ElementType.IsByteTy());
        return GetElementBits(index);
    }
    public LlvmAPInt GetElementAsAPInt(uint index) => new((uint)(ElementByteSize * 8), GetElementAsInteger(index));
    public LlvmAPFloat GetElementAsAPFloat(uint index) =>
        new(ElementType.GetFltSemantics(), new LlvmAPInt(ElementType.GetScalarSizeInBits(), GetElementBits(index)));
    public LlvmConstant GetElementAsConstant(uint index) => ElementType switch
    {
        LlvmIntegerType integer => LlvmConstantInt.Get(integer, GetElementBits(index)),
        LlvmByteType bytes => LlvmConstantByte.Get(bytes, GetElementBits(index)),
        _ => LlvmConstantFP.Get(ElementType, GetElementAsAPFloat(index))
    };
    public bool IsSplat
    {
        get
        {
            int size = ElementByteSize;
            for (int index = size; index < data.Length; index += size)
                if (!data.AsSpan(0, size).SequenceEqual(data.AsSpan(index, size))) return false;
            return true;
        }
    }
    public new LlvmConstant? GetSplatValue(bool allowPoison = false) => IsSplat ? GetElementAsConstant(0) : null;
}
