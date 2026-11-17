using System.Buffers.Binary;

namespace Msync.Core.Native;

public sealed partial class NativeStorage
{
    // Owned inline by NativeAllocation; rebinding must operate through its ref accessor.
    internal partial struct Data
    {
        internal readonly NativeLayout layout;
        internal NativeRawSlice<byte> bytes;
        internal NativeRootBank roots;
        internal NativeRawSlice<bool> initialized;
        internal readonly nint CallableCells;
        internal readonly byte[] RawBytes => bytes.Buffer;
        internal readonly bool[] InitializedFlags => initialized.Buffer;
        internal readonly int RawOffset => bytes.Offset;
        internal readonly nint RawAddress => bytes.Address;
        internal readonly nint InitializationAddress => initialized.Address;
        internal readonly Span<byte> RawSpan => bytes.Slice(0, bytes.Length);
        internal readonly Span<bool> InitializationSpan => initialized.Slice(0, initialized.Length);


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

        internal unsafe Data(NativeLayout layout, nint address, nint callableCells,
            NativeGuestObjectTable? objects = null, nint edges = 0, NativeRoot[]? reused = null)
        {
            this.layout = layout;
            bytes = new((byte*)address, layout.RawSize);
            initialized = new((bool*)((byte*)address + layout.RawSize), layout.RawSize);
            int count = layout.ReferenceCount;
            NativeRoot[] host = reused is not null && reused.Length >= count ? reused : count == 0 ? [] : new NativeRoot[count];
            roots = new(host, 0, count, objects, edges);
            CallableCells = callableCells;
        }

        internal void BindStack(byte[] bytes, NativeRoot[] roots, bool[] initialized, NativeOffset offset)
        {
            this.bytes = new(bytes, offset.Bytes, layout.RawSize);
            this.roots = new(roots, offset.References, layout.ReferenceCount);
            this.initialized = new(initialized, offset.Bytes, layout.RawSize);
        }

        internal unsafe void BindStack(byte* bytes, NativeRoot[] roots, bool* initialized, NativeOffset offset,
            NativeGuestObjectTable? objects = null, nint* edges = null)
        {
            this.bytes = new(bytes + offset.Bytes, layout.RawSize);
            this.roots = new(roots, offset.References, layout.ReferenceCount, objects,
                edges is null ? 0 : (nint)(edges + offset.References));
            this.initialized = new(initialized + offset.Bytes, layout.RawSize);
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
            allocation = roots.ReadAllocation(offset.References)!;
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
            return NativeValue.Restore(kind, bits, flags, roots, offset.References, actual.References);
        }
        internal readonly void Write(NativeType type, NativeOffset offset, NativeValue value)
        {
            if (type.Machine is { } machine) { WriteMachine(machine, offset, value); return; }
            int extent = WriteSource(type, offset, value);
            MarkInitialized(offset.Bytes, Math.Max(1, extent));
        }
        // Compiled source locations expose readiness at their declared start;
        // public raw-view writes and machine carriers retain byte-extent flags.
        internal readonly void WriteCompiled(NativeType type, NativeOffset offset, NativeValue value)
        {
            if (type.Machine is { } machine) { WriteMachine(machine, offset, value); return; }
            WriteSource(type, offset, value);
            initialized[offset.Bytes] = true;
        }
        private readonly int WriteSource(NativeType type, NativeOffset offset, NativeValue value)
        {
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
            int first = 0;
            if (value.Kind == NativeKind.Address)
            {
                roots.WriteAllocation(offset.References, value.Root(0).Allocation!);
                first = 1;
            }
            else if (value.Kind == NativeKind.Function)
            {
                NativeEnvironment? environment = value.Root(0).Environment;
                roots.WriteEnvironment(offset.References, environment);
                PublishCallable(offset.References, environment);
                first = 1;
            }
            for (int root = first; root < actual.References; root++)
                roots.WriteHost(offset.References + root, value.Root(root));
            if (format.References > actual.References)
                roots.Clear(offset.References + actual.References, format.References - actual.References);
            if (type.IsUnion) bytes[offset.Bytes] = (byte)value.Kind;
            return format.Bytes;
        }
        internal readonly unsafe void PublishCallable(int root, NativeEnvironment? environment)
        {
            if (CallableCells != 0) ((nint*)CallableCells)[root] = environment?.MachineCaptures ?? 0;
        }

        internal readonly unsafe void PublishCallables(int start, int count)
        {
            if (CallableCells == 0) return;
            nint* cells = (nint*)CallableCells;
            for (int index = start; index < start + count; index++)
                cells[index] = roots.CallableCell(index);
        }
        internal readonly void EraseCompiled(NativeType type, NativeOffset offset)
        {
            roots.Clear(offset.References, type.Representation.References);
            if (type.Machine is { } machine) initialized.Slice(offset.Bytes, machine.ByteSize).Clear();
            else initialized[offset.Bytes] = false;
        }
        internal readonly void Erase(NativeType type, NativeOffset offset)
        {
            NativeRepresentation format = NativeRepresentation.Of(type);
            roots.Clear(offset.References, format.References);
            initialized.Slice(offset.Bytes, Math.Max(1, format.Bytes)).Clear();
        }
    }
}
