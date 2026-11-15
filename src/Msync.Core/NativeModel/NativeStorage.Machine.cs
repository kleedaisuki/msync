using System.Buffers.Binary;
using Msync.Core.Llvm;

namespace Msync.Core.Native;

public sealed partial class NativeStorage
{
    public static NativeValue ReadMachineScalar(NativeMachineType type, NativeAddress address, bool littleEndian = true)
    {
        ref Data storage = ref address.Allocation.Storage;
        storage.RequireInitialized(address.Offset);
        return storage.ReadMachineInitialized(type, address.Offset, littleEndian);
    }
    public static void WriteMachineScalar(NativeMachineType type, NativeAddress address, NativeValue value, bool littleEndian = true) =>
        address.Allocation.Storage.WriteMachine(type, address.Offset, value, littleEndian);

    internal partial struct Data
    {
        internal readonly void MarkInitialized(int offset, int count) => initialized.Slice(offset, count).Fill(true);
        internal readonly NativeValue ReadMachineInitialized(NativeMachineType type, NativeOffset offset, bool littleEndian = true)
        {
            ReadOnlySpan<byte> raw = bytes.Slice(offset.Bytes, type.ByteSize);
            if (type.BitWidth <= 64)
            {
                ulong word = raw.Length switch
                {
                    1 => raw[0],
                    2 => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(raw) : BinaryPrimitives.ReadUInt16BigEndian(raw),
                    4 => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(raw) : BinaryPrimitives.ReadUInt32BigEndian(raw),
                    8 => littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(raw) : BinaryPrimitives.ReadUInt64BigEndian(raw),
                    _ => ReadPartialWord(raw, littleEndian)
                };
                return MachineValue(type, new(type.BitWidth, word, implicitTrunc: true));
            }
            Span<ulong> words = new ulong[(type.BitWidth + 63) / 64];
            for (int index = 0; index < raw.Length; ++index)
            {
                int bit = (littleEndian ? index : raw.Length - index - 1) * 8;
                words[bit / 64] |= (ulong)raw[index] << (bit % 64);
            }
            return MachineValue(type, new(type.BitWidth, words));
        }
        internal readonly void WriteMachine(NativeMachineType type, NativeOffset offset, NativeValue value, bool littleEndian = true)
        {
            LlvmAPInt bits = value.MachineBits;
            Span<byte> raw = bytes.Slice(offset.Bytes, type.ByteSize);
            if (bits.IsSingleWord)
            {
                ulong word = bits.GetZExtValue();
                switch (raw.Length)
                {
                    case 1: raw[0] = (byte)word; break;
                    case 2:
                        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(raw, (ushort)word);
                        else BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)word);
                        break;
                    case 4:
                        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(raw, (uint)word);
                        else BinaryPrimitives.WriteUInt32BigEndian(raw, (uint)word);
                        break;
                    case 8:
                        if (littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(raw, word);
                        else BinaryPrimitives.WriteUInt64BigEndian(raw, word);
                        break;
                    default: WriteMachineWords(bits, raw, littleEndian); break;
                }
            }
            else WriteMachineWords(bits, raw, littleEndian);
            MarkInitialized(offset.Bytes, raw.Length);
        }

        private static NativeValue MachineValue(NativeMachineType type, LlvmAPInt bits) =>
            type.IsInteger ? NativeValue.FromMachineInteger(bits, type)
                : NativeValue.FromMachineFloat(type.FloatFormat, bits, type);

        private static ulong ReadPartialWord(ReadOnlySpan<byte> raw, bool littleEndian)
        {
            ulong result = 0;
            for (int index = 0; index < raw.Length; ++index)
            {
                int bit = (littleEndian ? index : raw.Length - index - 1) * 8;
                result |= (ulong)raw[index] << bit;
            }
            return result;
        }

        private static void WriteMachineWords(LlvmAPInt bits, Span<byte> raw, bool littleEndian)
        {
            for (int index = 0; index < raw.Length; ++index)
            {
                int bit = (littleEndian ? index : raw.Length - index - 1) * 8;
                raw[index] = (byte)(bits.GetWord((uint)(bit / 64)) >> (bit % 64));
            }
        }
    }
}
