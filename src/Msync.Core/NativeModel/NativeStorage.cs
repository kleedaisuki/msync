using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

public sealed class NativeAllocation
{
    private NativeStorage.Data storage;
    private int allocationSite;
    internal int PendingValues;
    internal NativeAllocation? PreviousStackView;
    private nint hostMachineCaptures;
    internal ref nint MachineCaptures => ref GuestReferences is null
        ? ref hostMachineCaptures : ref NativeGuestObjectTable.CaptureCell(storage.RawAddress);
    internal NativeEnvironment? GuestEnvironment;
    private NativeEnvironment? reusableEnvironment;
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
    internal NativeAllocation(NativeLayout layout, int layoutId, int elementCount, NativeLayout storageLayout, nint raw, object owner,
        nint callableCells = 0)
    {
        Layout = layout;
        LayoutId = layoutId;
        ElementCount = elementCount;
        storage = new(storageLayout, raw, callableCells);
        NativeHeapOwner = owner;
    }
    internal NativeAllocation(object owner, NativeGuestObjectTable references) =>
        (NativeHeapOwner, GuestReferences, Layout) = (owner, references, null!);

    internal void BindGuest(NativeLayout layout, NativeLayout storageLayout, int id, int elementCount, int site,
        nint raw, nint callableCells, nint edges, nint pendingCell)
    {
        Layout = layout;
        LayoutId = id;
        ElementCount = elementCount;
        allocationSite = site;
        storage = new(storageLayout, raw, callableCells, GuestReferences, edges, pendingCell);
    }

    internal void ClearGuestRoots()
    {
        storage.roots.Clear();
        GuestEnvironment = null;
    }

    internal NativeEnvironment OpenGuestEnvironment(NativeEnvironmentLayout layout, out bool reused)
    {
        NativeEnvironment? environment = reusableEnvironment;
        reused = environment is not null;
        if (environment is null) reusableEnvironment = environment = NativeEnvironment.View(layout, Address);
        else environment.BindGuest(layout, Address);
        return GuestEnvironment = environment;
    }

    public NativeLayout Layout { get; private set; }
    public int LayoutId { get; private set; }
    public int AllocationSite { get => allocationSite; init => allocationSite = value; }
    public int ElementCount { get; private set; }
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
    internal object? NativeHeapOwner { get; }
    internal NativeGuestObjectTable? GuestReferences { get; }
    internal ReadOnlySpan<NativeRoot> HeapRoots => storage.roots.HostRoots;
    internal ReadOnlySpan<nint> HeapEdges => storage.roots.GuestEdges;
    internal NativeGuestObjectTable? EdgeTable => storage.roots.ReferenceTable;
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
        input.roots.CopyTo(source.Offset.References, output.roots, destination.Offset.References, size.References);
        output.PublishCallables(destination.Offset.References, size.References);
        input.initialized.Slice(source.Offset.Bytes, size.Bytes).CopyTo(output.initialized.Slice(destination.Offset.Bytes, size.Bytes));
    }
    public static void ClearRegion(NativeAddress address, NativeLayout layout, int count)
    {
        ValidateRegion(address, layout, count);
        NativeOffset size = layout.Extent * count;
        ref Data storage = ref address.Allocation.Storage;
        storage.bytes.Slice(address.Offset.Bytes, size.Bytes).Clear();
        storage.roots.Clear(address.Offset.References, size.References);
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
    internal static NativeValue LoadTrusted(NativeType type, NativeAddress address)
    {
        NativeValue value = address.Allocation.Storage.Read(type, address.Offset);
        GC.KeepAlive(address.Allocation);
        return value;
    }
    internal static NativeValue LoadInitialized(NativeType type, NativeAddress address) =>
        address.Allocation.Storage.ReadInitialized(type, address.Offset);
    internal static void StoreTrusted(NativeType type, NativeAddress address, NativeValue value)
    {
        address.Allocation.Storage.Write(type, address.Offset, value);
        GC.KeepAlive(address.Allocation);
    }
    internal static void ClearTrusted(NativeType type, NativeAddress address)
    {
        address.Allocation.Storage.Erase(type, address.Offset);
        GC.KeepAlive(address.Allocation);
    }

    internal static void StoreCompiled(NativeType type, NativeAddress address, NativeValue value) =>
        address.Allocation.Storage.WriteCompiled(type, address.Offset, value);
    internal static void ClearCompiled(NativeType type, NativeAddress address) =>
        address.Allocation.Storage.EraseCompiled(type, address.Offset);

    internal static void CopyTrusted(NativeType type, NativeAddress source, NativeAddress destination)
    {
        CopyPayload(type, source, destination);
        destination.Allocation.Storage.MarkInitialized(destination.Offset.Bytes, Math.Max(1, type.Representation.Bytes));
    }

    internal static void CopyCompiled(NativeType type, NativeAddress source, NativeAddress destination)
    {
        CopyPayload(type, source, destination);
        ref Data output = ref destination.Allocation.Storage;
        if (type.Machine is { } machine) output.MarkInitialized(destination.Offset.Bytes, machine.ByteSize);
        else output.initialized[destination.Offset.Bytes] = true;
    }

    private static void CopyPayload(NativeType type, NativeAddress source, NativeAddress destination)
    {
        ref Data input = ref source.Allocation.Storage;
        ref Data output = ref destination.Allocation.Storage;
        NativeRepresentation format = type.Representation;
        if (format.Bytes > 0)
            input.bytes.Slice(source.Offset.Bytes, format.Bytes)
                .CopyTo(output.bytes.Slice(destination.Offset.Bytes, format.Bytes));
        if (format.References > 0)
            CopyRoots(ref input, source.Offset.References, ref output, destination.Offset.References, format.References);
    }

    internal static void CopyKindTrusted(NativeKind actualKind, NativeAddress source, NativeAddress destination)
    {
        int extent = CopyKindPayload(actualKind, source, destination);
        ref Data output = ref destination.Allocation.Storage;
        output.MarkInitialized(destination.Offset.Bytes + destination.Type.Representation.PayloadOffset, Math.Max(1, extent));
        if (destination.Type.IsUnion) output.initialized[destination.Offset.Bytes] = true;
    }

    internal static void CopyKindCompiled(NativeKind actualKind, NativeAddress source, NativeAddress destination)
    {
        CopyKindPayload(actualKind, source, destination);
        ref Data output = ref destination.Allocation.Storage;
        if (destination.Type.Machine is { } machine) output.MarkInitialized(destination.Offset.Bytes, machine.ByteSize);
        else output.initialized[destination.Offset.Bytes] = true;
    }

    private static int CopyKindPayload(NativeKind actualKind, NativeAddress source, NativeAddress destination)
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
            output.roots.Clear(destination.Offset.References + actualFormat.References,
                dstFormat.References - actualFormat.References);
        if (destination.Type.IsUnion) output.bytes[destination.Offset.Bytes] = (byte)actualKind;
        return actualFormat.Bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRoots(ref Data input, int source, ref Data output, int destination, int count)
    {
        input.roots.CopyTo(source, output.roots, destination, count);
        output.PublishCallables(destination, count);
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
        double value = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8)));
        GC.KeepAlive(address.Allocation);
        return value;
    }
    public static bool LoadBool(NativeAddress address)
    {
        bool value = Readable(address).bytes[address.Offset.Bytes] != 0;
        GC.KeepAlive(address.Allocation);
        return value;
    }
    public static string LoadString(NativeAddress address) => Readable(address).roots.ReadHost(address.Offset.References).Text!;
    public static NativeConnection LoadConnection(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        return storage.roots.ReadHost(address.Offset.References).Connection!.Connection;
    }
    public static NativePath LoadPath(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        int raw = address.Offset.Bytes, root = address.Offset.References;
        NativeConnection? connection = storage.bytes[raw + 1] == 0 ? null
            : new NativeConnection((ConnectionKind)storage.bytes[raw], storage.roots.ReadHost(root + 4).Text ?? "");
        return new(storage.roots.ReadHost(root).Text!, storage.roots.ReadHost(root + 1).Text, connection,
            storage.roots.ReadHost(root + 2).Text, storage.roots.ReadHost(root + 3).Text);
    }
    internal static NativeFunction LoadFunctionDirect(NativeAddress address)
    {
        ref Data storage = ref address.Allocation.Storage;
        int raw = address.Offset.Bytes + address.Type.Representation.PayloadOffset;
        int root = address.Offset.References;
        return new(BinaryPrimitives.ReadInt32LittleEndian(storage.bytes.Slice(raw, 4)),
            storage.roots.ReadEnvironment(root), storage.roots.ReadHost(root + 1).Text);
    }

    public static NativeFunction LoadFunction(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        int root = address.Offset.References;
        return new(BinaryPrimitives.ReadInt32LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 4)),
            storage.roots.ReadEnvironment(root), storage.roots.ReadHost(root + 1).Text);
    }
    public static NativeRange LoadRange(NativeAddress address) => Readable(address).roots.ReadHost(address.Offset.References).Range!;
    public static NativeProcess LoadProcess(NativeAddress address) => Readable(address).roots.ReadHost(address.Offset.References).Process!;
    public static TypeRef LoadType(NativeAddress address) => Readable(address).roots.ReadHost(address.Offset.References).Type!;
    public static NativeAddress LoadAddress(NativeAddress address)
    {
        ref Data storage = ref Readable(address);
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8));
        return storage.roots.ReadAllocation(address.Offset.References)!.AddressAt(NativeValue.Unpack(bits),
            storage.roots.ReadHost(address.Offset.References + 1).StorageType);
    }
    internal static NativeAddress LoadAddressDirect(NativeAddress address)
    {
        ref Data storage = ref address.Allocation.Storage;
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8));
        return storage.roots.ReadAllocation(address.Offset.References)!.AddressAt(NativeValue.Unpack(bits),
            storage.roots.ReadHost(address.Offset.References + 1).StorageType);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeAddress LoadBinding(NativeAllocation frame, NativeOffset cell)
    {
        ref Data storage = ref frame.Storage;
        NativeAllocation? owner = storage.roots.ReadBinding(cell.References, frame);
        if (owner is null) return default;
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.Slice(cell.Bytes, 8));
        return owner.AddressAt(NativeValue.Unpack(bits), storage.roots.ReadHost(cell.References + 1).StorageType);
    }

    internal static void StoreBinding(NativeAllocation frame, NativeOffset cell, NativeAddress value)
    {
        ref Data storage = ref frame.Storage;
        BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(cell.Bytes, 8), NativeValue.Pack(value.Offset));
        storage.roots.WriteBinding(cell.References, frame, value.Allocation);
        storage.roots.WriteStorageType(cell.References + 1, value.StorageType);
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
        if (format.References != 0) storage.roots.Clear(address.Offset.References, format.References);
        storage.initialized[offset] = true;
    }

    internal static void StoreNumberCompiled(NativeAddress address, double value)
    {
        ref Data storage = ref address.Allocation.Storage;
        BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8), BitConverter.DoubleToUInt64Bits(value));
        storage.initialized[address.Offset.Bytes] = true;
    }

    public static void StoreNumber(NativeAddress address, double value)
    {
        ref Data storage = ref address.Allocation.Storage;
        BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8), BitConverter.DoubleToUInt64Bits(value));
        storage.MarkInitialized(address.Offset.Bytes, 8);
        GC.KeepAlive(address.Allocation);
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
        storage.roots.WriteHost(address.Offset.References, new() { Text = value });
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreConnection(NativeAddress address, NativeConnection value)
    {
        ref Data storage = ref address.Allocation.Storage;
        storage.roots.WriteHost(address.Offset.References, new() { Connection = new(value) });
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StorePath(NativeAddress address, NativePath value)
    {
        ArgumentNullException.ThrowIfNull(value.Raw);
        ref Data storage = ref address.Allocation.Storage;
        int raw = address.Offset.Bytes, root = address.Offset.References;
        storage.bytes[raw] = (byte)(value.Connection?.Kind ?? ConnectionKind.Local);
        storage.bytes[raw + 1] = value.Connection.HasValue ? (byte)1 : (byte)0;
        storage.roots.WriteHost(root, new() { Text = value.Raw });
        storage.roots.WriteHost(root + 1, new() { Text = value.WorkingDirectory });
        storage.roots.WriteHost(root + 2, new() { Text = value.HomeDirectory });
        storage.roots.WriteHost(root + 3, new() { Text = value.LocalSpelling });
        storage.roots.WriteHost(root + 4, new() { Text = value.Connection?.Address });
        storage.MarkInitialized(raw, 2);
    }
    public static void StoreFunction(NativeAddress address, NativeFunction value)
    {
        ref Data storage = ref address.Allocation.Storage;
        int root = address.Offset.References;
        BinaryPrimitives.WriteInt32LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 4), value.Function);
        storage.roots.WriteEnvironment(root, value.Environment);
        storage.roots.WriteHost(root + 1, new() { Text = value.NativeName });
        storage.PublishCallable(root, value.Environment);
        storage.MarkInitialized(address.Offset.Bytes, 4);
    }
    public static void StoreRange(NativeAddress address, NativeRange value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots.WriteHost(address.Offset.References, new() { Range = value });
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreProcess(NativeAddress address, NativeProcess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots.WriteHost(address.Offset.References, new() { Process = value });
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreType(NativeAddress address, TypeRef value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ref Data storage = ref address.Allocation.Storage;
        storage.roots.WriteHost(address.Offset.References, new() { Type = value });
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreAddress(NativeAddress address, NativeAddress value)
    {
        if (value.IsNull) throw new ArgumentException("Null pointers are not namespace values.", nameof(value));
        StoreAddressPayload(address, value);
        address.Allocation.Storage.MarkInitialized(address.Offset.Bytes, 8);
    }

    internal static void StoreAddressCompiled(NativeAddress address, NativeAddress value)
    {
        StoreAddressPayload(address, value);
        address.Allocation.Storage.initialized[address.Offset.Bytes] = true;
    }

    private static void StoreAddressPayload(NativeAddress address, NativeAddress value)
    {
        ref Data storage = ref address.Allocation.Storage;
        BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.Slice(address.Offset.Bytes, 8), NativeValue.Pack(value.Offset));
        storage.roots.WriteAllocation(address.Offset.References, value.Allocation);
        storage.roots.WriteStorageType(address.Offset.References + 1, value.StorageType);
    }
}
