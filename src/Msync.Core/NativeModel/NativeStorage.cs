using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

public sealed class NativeAllocation
{
    private NativeStorage.Data storage;
    internal int PendingValues;
    public NativeAllocation(NativeLayout layout, int layoutId = -1, int elementCount = 1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        Layout = layout;
        LayoutId = layoutId;
        ElementCount = elementCount;
        storage = new(elementCount == 1 ? layout : NativeLayout.Repeat(layout, elementCount));
    }
    internal NativeAllocation(NativeLayout layout, bool allocate)
    {
        Layout = layout;
        LayoutId = -1;
        ElementCount = 1;
        storage = new(layout, allocate);
    }
    public NativeLayout Layout { get; }
    public int LayoutId { get; }
    public int AllocationSite { get; init; }
    public int ElementCount { get; }
    public NativeAddress Index(int index, NativeLayout stride) => Address.Index(index, stride);
    public NativeAddress Address => new(this, default);
    public NativeAddress Field(int index)
    {
        NativeFieldLayout slot = Layout[index];
        return new(this, slot.Offset, slot.Type);
    }
    public NativeAddress AddressAt(NativeOffset offset, NativeType? type = null) => new(this, offset, type);
    public IEnumerable<NativeValue> InitializedValues
    {
        get
        {
            foreach (NativeFieldLayout slot in storage.layout.Fields)
                if (storage.IsInitialized(slot.Offset)) yield return storage.ReadInitialized(slot.Type, slot.Offset);
        }
    }
    internal ref NativeStorage.Data Storage => ref storage;
}

public readonly struct NativeAddress : IEquatable<NativeAddress>
{
    private readonly NativeAllocation? allocation;
    private readonly NativeType? type;
    internal NativeAddress(NativeAllocation allocation, NativeOffset offset, NativeType? type = null) =>
        (this.allocation, Offset, this.type) = (allocation, offset, type);
    public bool IsNull => allocation is null;
    public NativeAllocation Allocation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => allocation ?? throw new InvalidOperationException("Uninitialized native address.");
    }
    public NativeOffset Offset { get; }
    public bool IsInitialized => !IsNull && Allocation.Storage.IsInitialized(Offset);
    public NativeType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => type ?? throw new InvalidOperationException("This address requires a statically supplied storage type.");
    }
    internal NativeType? StorageType => type;
    public NativeAddress Field(int index)
    {
        NativeFieldLayout slot = Allocation.Layout[index];
        return OffsetBy(slot.Offset, slot.Type);
    }
    public NativeAddress Index(int index, NativeLayout stride)
    {
        ArgumentNullException.ThrowIfNull(stride);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        NativeAddress result = OffsetBy(stride.Extent * index);
        NativeStorage.ValidateRegion(result, stride, 1);
        return result;
    }
    public NativeAddress OffsetBy(NativeOffset offset, NativeType? type = null) => new(Allocation, Offset + offset, type);
    public NativeValue Load() => NativeStorage.Load(Type, this);
    public void Store(NativeValue value) => NativeStorage.Store(Type, this, value);
    public void Clear() => NativeStorage.Clear(Type, this);
    public bool Equals(NativeAddress other) => ReferenceEquals(allocation, other.allocation) && Offset == other.Offset;
    public override bool Equals(object? obj) => obj is NativeAddress address && Equals(address);
    public override int GetHashCode() => HashCode.Combine(allocation, Offset);
    public static bool operator ==(NativeAddress left, NativeAddress right) => left.Equals(right);
    public static bool operator !=(NativeAddress left, NativeAddress right) => !left.Equals(right);
}

public sealed partial class NativeStorage
{
    private NativeStorage() { }

    public static NativeValue Load(NativeType type, NativeAddress address)
    {
        Validate(type, address);
        return LoadTrusted(type, address);
    }
    public static void Store(NativeType type, NativeAddress address, NativeValue value)
    {
        Validate(type, address);
        if (!type.Accepts(value)) throw new ArgumentException($"{value.Kind} cannot be stored in {type}.", nameof(value));
        StoreTrusted(type, address, value);
    }
    public static void Clear(NativeType type, NativeAddress address)
    {
        Validate(type, address);
        ClearTrusted(type, address);
    }
    public static void Copy(NativeAddress source, NativeAddress destination)
    {
        NativeType type = source.Type;
        Validate(type, source);
        source.Allocation.Storage.RequireInitialized(source.Offset);
        Validate(destination.Type, destination);
        if (type.Equals(destination.Type)) CopyTrusted(type, source, destination);
        else destination.Store(source.Load());
    }
    public static void Move(NativeAddress source, NativeAddress destination)
    {
        if (source == destination) return;
        destination.Store(source.Load());
        source.Clear();
    }

    public static void CopyRegion(NativeAddress source, NativeAddress destination, NativeLayout layout, int count)
    {
        ValidateRegion(source, layout, count);
        ValidateRegion(destination, layout, count);
        CopyRegionTrusted(source, destination, layout, count);
    }
    internal static void CopyRegionTrusted(NativeAddress source, NativeAddress destination, NativeLayout layout, int count)
    {
        NativeOffset size = layout.Extent * count;
        ref Data input = ref source.Allocation.Storage;
        ref Data output = ref destination.Allocation.Storage;
        input.bytes.Slice(source.Offset.Bytes, size.Bytes).CopyTo(output.bytes.Slice(destination.Offset.Bytes, size.Bytes));
        input.roots.Slice(source.Offset.References, size.References).CopyTo(output.roots.Slice(destination.Offset.References, size.References));
        input.initialized.Slice(source.Offset.Bytes, size.Bytes).CopyTo(output.initialized.Slice(destination.Offset.Bytes, size.Bytes));
    }
    public static void ClearRegion(NativeAddress address, NativeLayout layout, int count)
    {
        ValidateRegion(address, layout, count);
        NativeOffset size = layout.Extent * count;
        ref Data storage = ref address.Allocation.Storage;
        storage.bytes.Slice(address.Offset.Bytes, size.Bytes).Clear();
        storage.roots.Slice(address.Offset.References, size.References).Clear();
        storage.initialized.Slice(address.Offset.Bytes, size.Bytes).Clear();
    }
    public static void ValidateRegion(NativeAddress address, NativeLayout layout, int count)
    {
        ArgumentNullException.ThrowIfNull(layout);
        CheckRegionBounds(address, layout, count);
        ref Data storage = ref address.Allocation.Storage;
        if (ReferenceEquals(address.Allocation.Layout, layout) && layout.RawSize > 0
            && address.Offset.Bytes % layout.RawSize == 0
            && address.Offset.References == checked(address.Offset.Bytes / layout.RawSize * layout.ReferenceCount)) return;
        for (int item = 0; item < count; item++)
            foreach (NativeFieldLayout field in layout.Fields)
                _ = storage.layout.Find(address.Offset + layout.Extent * item + field.Offset, field.Type);
    }

    internal static void CheckRegionBounds(NativeAddress address, NativeLayout layout, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ref Data storage = ref address.Allocation.Storage;
        NativeOffset end = address.Offset + layout.Extent * count;
        if (address.Offset.Bytes < 0 || address.Offset.References < 0
            || end.Bytes > storage.bytes.Length || end.References > storage.roots.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Native region exceeds its owning allocation.");
    }

    // Admission is an external boundary. Verified IR uses the offset-based entrypoints below.
    private static void Validate(NativeType type, NativeAddress address)
    {
        ArgumentNullException.ThrowIfNull(type);
        _ = address.Allocation.Storage.layout.Find(address.Offset, type);
    }
    internal static void ValidateAddress(NativeType type, NativeAddress address) => Validate(type, address);
    internal static NativeValue LoadTrusted(NativeType type, NativeAddress address) =>
        address.Allocation.Storage.Read(type, address.Offset);
    internal static NativeValue LoadInitialized(NativeType type, NativeAddress address) =>
        address.Allocation.Storage.ReadInitialized(type, address.Offset);
    internal static void StoreTrusted(NativeType type, NativeAddress address, NativeValue value) =>
        address.Allocation.Storage.Write(type, address.Offset, value);
    internal static void ClearTrusted(NativeType type, NativeAddress address) =>
        address.Allocation.Storage.Erase(type, address.Offset);

    internal static void CopyTrusted(NativeType type, NativeAddress source, NativeAddress destination)
    {
        ref Data input = ref source.Allocation.Storage;
        ref Data output = ref destination.Allocation.Storage;
        NativeRepresentation format = type.Representation;
        if (format.Bytes > 0)
            input.bytes.Slice(source.Offset.Bytes, format.Bytes)
                .CopyTo(output.bytes.Slice(destination.Offset.Bytes, format.Bytes));
        if (format.References > 0)
            CopyRoots(ref input, source.Offset.References, ref output, destination.Offset.References, format.References);
        output.MarkInitialized(destination.Offset.Bytes, Math.Max(1, format.Bytes));
    }

    internal static void CopyKindTrusted(NativeKind actualKind, NativeAddress source, NativeAddress destination)
    {
        ref Data input = ref source.Allocation.Storage;
        ref Data output = ref destination.Allocation.Storage;
        NativeRepresentation srcFormat = source.Type.Representation;
        NativeRepresentation dstFormat = destination.Type.Representation;
        NativeRepresentation actualFormat = source.Type.IsMachine ? source.Type.Representation : NativeRepresentation.Of(actualKind);
        int srcStart = checked(source.Offset.Bytes + srcFormat.PayloadOffset);
        int dstStart = checked(destination.Offset.Bytes + dstFormat.PayloadOffset);
        if (actualFormat.Bytes > 0)
            input.bytes.Slice(srcStart, actualFormat.Bytes).CopyTo(output.bytes.Slice(dstStart, actualFormat.Bytes));
        if (actualFormat.References > 0)
            CopyRoots(ref input, source.Offset.References, ref output, destination.Offset.References, actualFormat.References);
        if (dstFormat.References > actualFormat.References)
            output.roots.Slice(destination.Offset.References + actualFormat.References,
                dstFormat.References - actualFormat.References).Clear();
        if (destination.Type.IsUnion) output.bytes[destination.Offset.Bytes] = (byte)actualKind;
        output.MarkInitialized(dstStart, Math.Max(1, actualFormat.Bytes));
        if (destination.Type.IsUnion) output.initialized[destination.Offset.Bytes] = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRoots(ref Data input, int source, ref Data output, int destination, int count)
    {
        if (count > 2)
        {
            input.roots.Slice(source, count).CopyTo(output.roots.Slice(destination, count));
            return;
        }
        // Snapshot both references before storing: adjacent address carriers may overlap.
        object? first = input.roots[source].Value;
        object? second = count == 2 ? input.roots[source + 1].Value : null;
        output.roots[destination].Value = first;
        if (count == 2) output.roots[destination + 1].Value = second;
    }

    public static double ReadNumber(NativeAddress address)
    { Validate(NativeType.Of(NativeKind.Number), address); return LoadNumber(address); }
    public static bool ReadBool(NativeAddress address)
    { Validate(NativeType.Of(NativeKind.Bool), address); return LoadBool(address); }
    public static string ReadString(NativeAddress address)
    { Validate(NativeType.Of(NativeKind.String), address); return LoadString(address); }
    public static NativeAddress ReadAddress(NativeAddress address)
    { Validate(NativeType.Of(NativeKind.Address), address); return LoadAddress(address); }
    public static NativePath ReadPath(NativeAddress address)
    { Validate(NativeType.Of(NativeKind.Path), address); return LoadPath(address); }
    public static NativeConnection ReadConnection(NativeAddress address)
    { Validate(NativeType.Of(NativeKind.Connection), address); return LoadConnection(address); }
    public static void WriteNumber(NativeAddress address, double value)
    { Validate(NativeType.Of(NativeKind.Number), address); StoreNumber(address, value); }
    public static void WriteBool(NativeAddress address, bool value)
    { Validate(NativeType.Of(NativeKind.Bool), address); StoreBool(address, value); }
    public static void WriteString(NativeAddress address, string value)
    { Validate(NativeType.Of(NativeKind.String), address); StoreString(address, value); }
    public static void WritePath(NativeAddress address, NativePath value)
    { Validate(NativeType.Of(NativeKind.Path), address); StorePath(address, value); }
    public static void WriteConnection(NativeAddress address, NativeConnection value)
    { Validate(NativeType.Of(NativeKind.Connection), address); StoreConnection(address, value); }

    internal static double LoadReadyNumber(NativeAddress address, int payloadOffset = 0) =>
        BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(address.Allocation.Storage.bytes.Slice(address.Offset.Bytes + payloadOffset, 8)));

    public static double LoadNumber(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        return BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8)));
    }
    public static bool LoadBool(NativeAddress address) => Readable(address).bytes[address.Offset.Bytes] != 0;
    public static string LoadString(NativeAddress address) => Readable(address).roots[address.Offset.References].Text!;
    public static NativeConnection LoadConnection(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        return storage.roots[address.Offset.References].Connection!.Connection;
    }
    public static NativePath LoadPath(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        int raw = address.Offset.Bytes, root = address.Offset.References;
        NativeConnection? connection = storage.bytes[raw + 1] == 0 ? null
            : new NativeConnection((ConnectionKind)storage.bytes[raw], storage.roots[root + 4].Text ?? "");
        return new(storage.roots[root].Text!, storage.roots[root + 1].Text, connection,
            storage.roots[root + 2].Text, storage.roots[root + 3].Text);
    }
    internal static NativeFunction LoadFunctionDirect(NativeAddress address)
    {
        ref Data storage = ref address.Allocation.Storage;
        int raw = address.Offset.Bytes + address.Type.Representation.PayloadOffset;
        int root = address.Offset.References;
        return new(BinaryPrimitives.ReadInt32LittleEndian(storage.bytes.Slice(raw, 4)),
            storage.roots[root].Environment, storage.roots[root + 1].Text);
    }

    public static NativeFunction LoadFunction(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        int root = address.Offset.References;
        return new(BinaryPrimitives.ReadInt32LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 4)),
            storage.roots[root].Environment, storage.roots[root + 1].Text);
    }
    public static NativeRange LoadRange(NativeAddress address) => Readable(address).roots[address.Offset.References].Range!;
    public static NativeProcess LoadProcess(NativeAddress address) => Readable(address).roots[address.Offset.References].Process!;
    public static TypeRef LoadType(NativeAddress address) => Readable(address).roots[address.Offset.References].Type!;
    public static NativeAddress LoadAddress(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8));
        return storage.roots[address.Offset.References].Allocation!.AddressAt(NativeValue.Unpack(bits),
            storage.roots[address.Offset.References + 1].StorageType);
    }
    internal static NativeAddress LoadAddressDirect(NativeAddress address)
    {
        ref Data storage = ref address.Allocation.Storage;
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8));
        return storage.roots[address.Offset.References].Allocation!.AddressAt(NativeValue.Unpack(bits),
            storage.roots[address.Offset.References + 1].StorageType);
    }
    private static ref Data Readable(NativeAddress address)
    {
        ref Data storage = ref address.Allocation.Storage;
        storage.RequireInitialized(address.Offset);
        return ref storage;
    }
    internal static void StoreScalarTrusted(NativeAddress address, NativeKind kind, ulong bits)
    {
        ref Data storage = ref address.Allocation.Storage;
        NativeRepresentation format = address.Type.Representation;
        int offset = address.Offset.Bytes;
        if (format.PayloadOffset != 0)
        {
            storage.bytes[offset] = (byte)kind;
        }
        int payload = offset + format.PayloadOffset;
        if (kind == NativeKind.Number) BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(payload, 8), bits);
        else storage.bytes[payload] = (byte)bits;
        if (format.References != 0) storage.roots.Slice(address.Offset.References, format.References).Clear();
        storage.MarkInitialized(payload, kind == NativeKind.Number ? 8 : 1);
        if (format.PayloadOffset != 0) storage.initialized[offset] = true;
    }

    public static void StoreNumber(NativeAddress address, double value)
    {
        ref Data storage = ref address.Allocation.Storage;
        BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8), BitConverter.DoubleToUInt64Bits(value));
        storage.MarkInitialized(address.Offset.Bytes, 8);
    }
    public static void StoreBool(NativeAddress address, bool value)
    {
        ref Data storage = ref address.Allocation.Storage;
        storage.bytes[address.Offset.Bytes] = value ? (byte)1 : (byte)0;
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreString(NativeAddress address, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Text = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreConnection(NativeAddress address, NativeConnection value)
    {
        ref Data storage = ref address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Connection = new(value) };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StorePath(NativeAddress address, NativePath value)
    {
        ArgumentNullException.ThrowIfNull(value.Raw);
        ref Data storage = ref address.Allocation.Storage;
        int raw = address.Offset.Bytes, root = address.Offset.References;
        storage.bytes[raw] = (byte)(value.Connection?.Kind ?? ConnectionKind.Local);
        storage.bytes[raw + 1] = value.Connection.HasValue ? (byte)1 : (byte)0;
        storage.roots[root] = new() { Text = value.Raw };
        storage.roots[root + 1] = new() { Text = value.WorkingDirectory };
        storage.roots[root + 2] = new() { Text = value.HomeDirectory };
        storage.roots[root + 3] = new() { Text = value.LocalSpelling };
        storage.roots[root + 4] = new() { Text = value.Connection?.Address };
        storage.MarkInitialized(raw, 2);
    }
    public static void StoreFunction(NativeAddress address, NativeFunction value)
    {
        ref Data storage = ref address.Allocation.Storage;
        int root = address.Offset.References;
        BinaryPrimitives.WriteInt32LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 4), value.Function);
        storage.roots[root] = new() { Environment = value.Environment };
        storage.roots[root + 1] = new() { Text = value.NativeName };
        storage.MarkInitialized(address.Offset.Bytes, 4);
    }
    public static void StoreRange(NativeAddress address, NativeRange value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Range = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreProcess(NativeAddress address, NativeProcess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Process = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreType(NativeAddress address, TypeRef value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Type = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreAddress(NativeAddress address, NativeAddress value)
    {
        if (value.IsNull) throw new ArgumentException("Null pointers are not namespace values.", nameof(value));
        ref Data storage = ref address.Allocation.Storage;
        BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8), NativeValue.Pack(value.Offset));
        storage.roots[address.Offset.References] = new() { Allocation = value.Allocation };
        storage.roots[address.Offset.References + 1] = new() { StorageType = value.StorageType };
        storage.MarkInitialized(address.Offset.Bytes, 8);
    }
}
