// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from DerivedTypes.h and Type.cpp, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed class LlvmIntegerType : LlvmType
{
    public const uint MinIntBits = 1;
    public const uint MaxIntBits = 1 << 23;
    public uint BitWidth { get; }
    internal LlvmIntegerType(LlvmContext context, uint bits) : base(context, LlvmTypeId.IntegerTyID) => BitWidth = bits;
    public static LlvmIntegerType Get(LlvmContext context, uint bits)
    {
        Debug.Assert(bits >= MinIntBits, "bitwidth too small");
        Debug.Assert(bits <= MaxIntBits, "bitwidth too large");
        if (context.BuiltinIntegers.TryGetValue(bits, out var builtin)) return builtin;
        if (!context.IntegerTypes.TryGetValue(bits, out var type))
            context.IntegerTypes.Add(bits, type = new(context, bits));
        return type;
    }
    public LlvmIntegerType GetExtendedType() => Get(Context, 2 * BitWidth);
    public LlvmIntegerType GetTruncatedType()
    {
        Debug.Assert((BitWidth & 1) == 0, "Cannot truncate integer type with odd bit-width");
        return Get(Context, BitWidth / 2);
    }
    public ulong GetBitMask() => ulong.MaxValue >> (int)(64 - BitWidth);
    public ulong GetSignBit() => 1UL << (int)(BitWidth - 1);
}

public sealed class LlvmByteType : LlvmType
{
    public const uint MinByteBits = 1;
    public const uint MaxByteBits = 1 << 23;
    public uint BitWidth { get; }
    internal LlvmByteType(LlvmContext context, uint bits) : base(context, LlvmTypeId.ByteTyID) => BitWidth = bits;
    public static LlvmByteType Get(LlvmContext context, uint bits)
    {
        Debug.Assert(bits >= MinByteBits, "bitwidth too small");
        Debug.Assert(bits <= MaxByteBits, "bitwidth too large");
        // The pinned upstream switch deliberately has no case 1 (unlike IntegerType).
        if (bits != 1 && context.BuiltinBytes.TryGetValue(bits, out var builtin)) return builtin;
        if (!context.ByteTypes.TryGetValue(bits, out var type))
            context.ByteTypes.Add(bits, type = new(context, bits));
        return type;
    }
}
