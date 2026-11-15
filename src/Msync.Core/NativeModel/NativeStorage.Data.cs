using System.Buffers.Binary;

namespace Msync.Core.Native;

public sealed partial class NativeStorage
{
    // Owned inline by NativeAllocation; rebinding must operate through its ref accessor.
    internal partial struct Data
    {
        internal readonly NativeLayout layout;
        internal NativeStorageSlice<byte> bytes;
        internal NativeStorageSlice<NativeRoot> roots;
        internal NativeStorageSlice<bool> initialized;
        internal readonly byte[] RawBytes => bytes.Buffer;
        internal readonly bool[] InitializedFlags => initialized.Buffer;
        internal readonly int RawOffset => bytes.Offset;

        internal readonly void ResetFrame()
        {
            roots.Clear();
            initialized.Clear();
        }

        internal Data(NativeLayout layout, bool allocate = true)
        {
            this.layout = layout;
            bytes = new(allocate && layout.RawSize != 0 ? new byte[layout.RawSize] : []);
            roots = new(allocate && layout.ReferenceCount != 0 ? new NativeRoot[layout.ReferenceCount] : []);
            initialized = new(allocate && layout.RawSize != 0 ? new bool[layout.RawSize] : []);
        }

        internal void BindStack(byte[] bytes, NativeRoot[] roots, bool[] initialized, NativeOffset offset)
        {
            this.bytes = new(bytes, offset.Bytes, layout.RawSize);
            this.roots = new(roots, offset.References, layout.ReferenceCount);
            this.initialized = new(initialized, offset.Bytes, layout.RawSize);
        }

        internal void DetachStack()
        {
            bytes = new([]);
            roots = new([]);
            initialized = new([]);
        }

        internal readonly NativeKind ReadKind(NativeOffset offset) => (NativeKind)bytes[offset.Bytes];

        internal readonly bool TryReadAddressHeader(NativeType type, NativeOffset offset,
            out NativeAllocation allocation, out NativeOffset pointeeOffset)
        {
            int start = checked(offset.Bytes + type.Representation.PayloadOffset);
            allocation = roots[offset.References].Allocation!;
            pointeeOffset = NativeValue.Unpack(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(start, 8)));
            return allocation is not null;
        }

        internal readonly bool IsInitialized(NativeOffset offset) => initialized[offset.Bytes];
        internal readonly void RequireInitialized(NativeOffset offset)
        {
            if (!IsInitialized(offset)) throw new InvalidOperationException($"Native storage at {offset} is uninitialized.");
        }
        internal readonly NativeValue Read(NativeType type, NativeOffset offset)
        {
            RequireInitialized(offset);
            return ReadInitialized(type, offset);
        }
        internal readonly NativeValue ReadInitialized(NativeType type, NativeOffset offset)
        {
            if (type.Machine is { } machine) return ReadMachineInitialized(machine, offset);
            NativeRepresentation format = NativeRepresentation.Of(type);
            bool union = type.IsUnion;
            NativeKind kind = union ? (NativeKind)bytes[offset.Bytes] : type.Kind;
            NativeRepresentation actual = union ? NativeRepresentation.Of(kind) : format;
            int start = checked(offset.Bytes + format.PayloadOffset);
            ulong bits = 0;
            byte flags = 0;
            if (kind == NativeKind.Path)
            {
                bits = bytes[start];
                flags = bytes[start + 1];
            }
            else if (actual.Bytes == 8) bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(start, 8));
            else if (actual.Bytes == 4) bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(start, 4));
            else if (actual.Bytes == 1) bits = bytes[start];
            return NativeValue.Restore(kind, bits, flags, roots.Slice(offset.References, actual.References));
        }
        internal readonly void Write(NativeType type, NativeOffset offset, NativeValue value)
        {
            if (type.Machine is { } machine) { WriteMachine(machine, offset, value); return; }
            NativeRepresentation format = NativeRepresentation.Of(type);
            NativeRepresentation actual = NativeRepresentation.Of(value.Kind);
            int start = checked(offset.Bytes + format.PayloadOffset);
            if (value.Kind == NativeKind.Path)
            {
                bytes[start] = (byte)value.Bits;
                bytes[start + 1] = value.Flags;
            }
            else if (actual.Bytes == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(start, 8), value.Bits);
            else if (actual.Bytes == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(start, 4), (uint)value.Bits);
            else if (actual.Bytes == 1) bytes[start] = (byte)value.Bits;
            for (int root = 0; root < actual.References; root++) roots[offset.References + root] = value.Root(root);
            if (format.References > actual.References)
                roots.Slice(offset.References + actual.References, format.References - actual.References).Clear();
            if (type.IsUnion) bytes[offset.Bytes] = (byte)value.Kind;
            MarkInitialized(offset.Bytes, Math.Max(1, format.Bytes));
        }
        internal readonly void Erase(NativeType type, NativeOffset offset)
        {
            NativeRepresentation format = NativeRepresentation.Of(type);
            roots.Slice(offset.References, format.References).Clear();
            initialized.Slice(offset.Bytes, Math.Max(1, format.Bytes)).Clear();
        }
    }
}
