// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// ConstantDataSequential::getImpl and ConstantDataArray::getString/get(uint8_t),
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940, Constants.h / Constants.cpp.
using System.Text;

namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantDataArray : LlvmConstantData
{
    private readonly byte[] data;
    private LlvmConstantDataArray(LlvmArrayType type, byte[] data) : base(type, LlvmValueKind.ConstantDataArrayVal) =>
        this.data = data;
    public LlvmArrayType ArrayType => (LlvmArrayType)Type;
    public ulong NumElements => ArrayType.NumElements;
    public ReadOnlySpan<byte> GetRawDataValues() => data;

    public static LlvmConstant Get(LlvmContext context, ReadOnlySpan<byte> elements) =>
        GetImpl(LlvmArrayType.Get(LlvmIntegerType.Get(context, 8), (ulong)elements.Length), elements);

    public static LlvmConstant GetString(LlvmContext context, ReadOnlySpan<byte> text, bool addNull = true, bool byteString = false)
    {
        byte[] bytes = new byte[text.Length + (addNull ? 1 : 0)];
        text.CopyTo(bytes);
        LlvmType element = byteString ? LlvmByteType.Get(context, 8) : LlvmIntegerType.Get(context, 8);
        return GetImpl(LlvmArrayType.Get(element, (ulong)bytes.Length), bytes);
    }

    // StringRef is a byte string upstream; managed text crosses that boundary as UTF-8.
    public static LlvmConstant GetString(LlvmContext context, string text, bool addNull = true, bool byteString = false) =>
        GetString(context, Encoding.UTF8.GetBytes(text), addNull, byteString);

    internal static LlvmConstant GetImpl(LlvmArrayType type, ReadOnlySpan<byte> elements)
    {
        bool zero = true;
        foreach (byte element in elements) zero &= element == 0;
        if (zero) return LlvmConstantAggregateZero.Get(type);
        LlvmConstantDataKey key = new(type, elements.ToArray());
        if (!type.Context.DataArrayConstants.TryGetValue(key, out var value))
            type.Context.DataArrayConstants.Add(key, value = new(type, key.Data));
        return value;
    }
}

internal readonly struct LlvmConstantDataKey(LlvmType type, byte[] data) : IEquatable<LlvmConstantDataKey>
{
    internal byte[] Data { get; } = data;
    private LlvmType Type { get; } = type;
    public bool Equals(LlvmConstantDataKey other) => ReferenceEquals(Type, other.Type) && Data.AsSpan().SequenceEqual(other.Data);
    public override bool Equals(object? value) => value is LlvmConstantDataKey other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Type);
        foreach (byte value in Data) hash.Add(value);
        return hash.ToHashCode();
    }
}
