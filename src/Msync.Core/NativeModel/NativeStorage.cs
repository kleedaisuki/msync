using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

public sealed class NativeAllocation
{
    private readonly NativeStorage storage;
    public NativeAllocation(NativeLayout layout, int layoutId = -1, int elementCount = 1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegative(elementCount);
        Layout = layout;
        LayoutId = layoutId;
        ElementCount = elementCount;
        storage = new(elementCount == 1 ? layout : NativeLayout.Repeat(layout, elementCount));
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeAddress AddressAt(NativeOffset offset, NativeType? type = null) => new(this, offset, type);
    public IEnumerable<NativeValue> InitializedValues => storage.InitializedValues;
    internal NativeStorage Storage => storage;
}

public readonly struct NativeAddress : IEquatable<NativeAddress>
{
    private readonly NativeAllocation? allocation;
    private readonly NativeType? type;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAddress(NativeAllocation allocation, NativeOffset offset, NativeType? type = null) =>
        (this.allocation, Offset, this.type) = (allocation, offset, type);
    public bool IsNull => allocation is null;
    public NativeAllocation Allocation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => allocation ?? ThrowUninitialized();
    }
    public NativeOffset Offset { get; }
    public bool IsInitialized
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsNull && Allocation.Storage.IsInitialized(Offset);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeAllocation ThrowUninitialized() => throw new InvalidOperationException("Uninitialized native address.");
    public NativeType Type => type ?? throw new InvalidOperationException("This address requires a statically supplied storage type.");
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

public sealed class NativeStorage
{
    private readonly NativeLayout layout;
    private readonly byte[] bytes;
    private readonly NativeRoot[] roots;
    private readonly bool[] initialized;
    internal void ResetFrame()
    {
        roots.AsSpan().Clear();
        initialized.AsSpan().Clear();
    }

    internal byte[] RawBytes => bytes;
    internal bool[] InitializedFlags => initialized;

    internal NativeStorage(NativeLayout layout)
    {
        this.layout = layout;
        bytes = layout.RawSize == 0 ? [] : new byte[layout.RawSize];
        roots = layout.ReferenceCount == 0 ? [] : new NativeRoot[layout.ReferenceCount];
        initialized = layout.RawSize == 0 ? [] : new bool[layout.RawSize];
    }

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
        NativeStorage input = source.Allocation.Storage, output = destination.Allocation.Storage;
        input.bytes.AsSpan(source.Offset.Bytes, size.Bytes).CopyTo(output.bytes.AsSpan(destination.Offset.Bytes, size.Bytes));
        input.roots.AsSpan(source.Offset.References, size.References).CopyTo(output.roots.AsSpan(destination.Offset.References, size.References));
        input.initialized.AsSpan(source.Offset.Bytes, size.Bytes).CopyTo(output.initialized.AsSpan(destination.Offset.Bytes, size.Bytes));
    }
    public static void ClearRegion(NativeAddress address, NativeLayout layout, int count)
    {
        ValidateRegion(address, layout, count);
        NativeOffset size = layout.Extent * count;
        NativeStorage storage = address.Allocation.Storage;
        storage.bytes.AsSpan(address.Offset.Bytes, size.Bytes).Clear();
        storage.roots.AsSpan(address.Offset.References, size.References).Clear();
        storage.initialized.AsSpan(address.Offset.Bytes, size.Bytes).Clear();
    }
    public static void ValidateRegion(NativeAddress address, NativeLayout layout, int count)
    {
        ArgumentNullException.ThrowIfNull(layout);
        CheckRegionBounds(address, layout, count);
        NativeStorage storage = address.Allocation.Storage;
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
        NativeStorage storage = address.Allocation.Storage;
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
    internal static void StoreTrusted(NativeType type, NativeAddress address, NativeValue value) =>
        address.Allocation.Storage.Write(type, address.Offset, value);
    internal static void ClearTrusted(NativeType type, NativeAddress address) =>
        address.Allocation.Storage.Erase(type, address.Offset);

    internal static void CopyTrusted(NativeType type, NativeAddress source, NativeAddress destination)
    {
        source.Allocation.Storage.RequireInitialized(source.Offset);
        CopyTrustedUnchecked(type, source, destination);
    }

    internal static void CopyTrustedUnchecked(NativeType type, NativeAddress source, NativeAddress destination)
    {
        NativeStorage input = source.Allocation.Storage, output = destination.Allocation.Storage;
        NativeRepresentation format = type.Representation;
        int srcByte = source.Offset.Bytes, dstByte = destination.Offset.Bytes;
        if (format.References == 0 && format.Bytes == 8)
        {
            if (BitConverter.IsLittleEndian)
                Unsafe.WriteUnaligned(ref output.bytes[dstByte], Unsafe.ReadUnaligned<ulong>(ref input.bytes[srcByte]));
            else
                input.bytes.AsSpan(srcByte, 8).CopyTo(output.bytes.AsSpan(dstByte, 8));
            output.initialized[dstByte] = true;
            return;
        }
        switch (format.Bytes)
        {
            case 0: break;
            case 1: output.bytes[dstByte] = input.bytes[srcByte]; break;
            case 2: Unsafe.WriteUnaligned(ref output.bytes[dstByte], Unsafe.ReadUnaligned<ushort>(ref input.bytes[srcByte])); break;
            case 4: Unsafe.WriteUnaligned(ref output.bytes[dstByte], Unsafe.ReadUnaligned<uint>(ref input.bytes[srcByte])); break;
            case 8: Unsafe.WriteUnaligned(ref output.bytes[dstByte], Unsafe.ReadUnaligned<ulong>(ref input.bytes[srcByte])); break;
            case 16:
                Unsafe.WriteUnaligned(ref output.bytes[dstByte], Unsafe.ReadUnaligned<ulong>(ref input.bytes[srcByte]));
                Unsafe.WriteUnaligned(ref output.bytes[dstByte + 8], Unsafe.ReadUnaligned<ulong>(ref input.bytes[srcByte + 8]));
                break;
            default:
                input.bytes.AsSpan(srcByte, format.Bytes)
                    .CopyTo(output.bytes.AsSpan(dstByte, format.Bytes));
                break;
        }
        int srcRef = source.Offset.References, dstRef = destination.Offset.References;
        switch (format.References)
        {
            case 0: break;
            case 1: output.roots[dstRef] = input.roots[srcRef]; break;
            case 2:
                output.roots[dstRef] = input.roots[srcRef];
                output.roots[dstRef + 1] = input.roots[srcRef + 1];
                break;
            default:
                input.roots.AsSpan(srcRef, format.References)
                    .CopyTo(output.roots.AsSpan(dstRef, format.References));
                break;
        }
        output.initialized[dstByte] = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsInitialized(NativeOffset offset) => initialized[offset.Bytes];
    private void RequireInitialized(NativeOffset offset)
    {
        if (!IsInitialized(offset)) throw new InvalidOperationException($"Native storage at {offset} is uninitialized.");
    }
    private NativeValue Read(NativeType type, NativeOffset offset)
    {
        RequireInitialized(offset);
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
        else if (actual.Bytes == 8) bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(start, 8));
        else if (actual.Bytes == 4) bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start, 4));
        else if (actual.Bytes == 1) bits = bytes[start];
        return NativeValue.Restore(kind, bits, flags, roots.AsSpan(offset.References, actual.References));
    }
    private void Write(NativeType type, NativeOffset offset, NativeValue value)
    {
        NativeRepresentation format = NativeRepresentation.Of(type);
        NativeRepresentation actual = NativeRepresentation.Of(value.Kind);
        bool fullBytesWritten = !type.IsUnion && format.PayloadOffset == 0 && actual.Bytes == format.Bytes
            && (actual.Bytes is 1 or 4 or 8 || (value.Kind == NativeKind.Path && format.Bytes == 2));
        if (!fullBytesWritten && format.Bytes > 0) bytes.AsSpan(offset.Bytes, format.Bytes).Clear();
        int start = checked(offset.Bytes + format.PayloadOffset);
        if (value.Kind == NativeKind.Path)
        {
            bytes[start] = (byte)value.Bits;
            bytes[start + 1] = value.Flags;
        }
        else if (actual.Bytes == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(start, 8), value.Bits);
        else if (actual.Bytes == 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(start, 4), (uint)value.Bits);
        else if (actual.Bytes == 1) bytes[start] = (byte)value.Bits;
        for (int root = 0; root < actual.References; root++) roots[offset.References + root] = value.Root(root);
        if (format.References > actual.References)
            roots.AsSpan(offset.References + actual.References, format.References - actual.References).Clear();
        if (type.IsUnion) bytes[offset.Bytes] = (byte)value.Kind;
        initialized[offset.Bytes] = true;
    }
    private void Erase(NativeType type, NativeOffset offset)
    {
        NativeRepresentation format = NativeRepresentation.Of(type);
        bytes.AsSpan(offset.Bytes, format.Bytes).Clear();
        roots.AsSpan(offset.References, format.References).Clear();
        initialized[offset.Bytes] = false;
    }
    internal IEnumerable<NativeValue> InitializedValues
    {
        get
        {
            foreach (NativeFieldLayout slot in layout.Fields)
                if (initialized[slot.Offset.Bytes]) yield return Read(slot.Type, slot.Offset);
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double LoadNumber(NativeAddress address)
    {
        NativeStorage storage = Readable(address);
        if (BitConverter.IsLittleEndian)
            return BitConverter.UInt64BitsToDouble(Unsafe.ReadUnaligned<ulong>(ref storage.bytes[address.Offset.Bytes]));
        return BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 8)));
    }
    public static bool LoadBool(NativeAddress address) => Readable(address).bytes[address.Offset.Bytes] != 0;
    public static string LoadString(NativeAddress address) => Readable(address).roots[address.Offset.References].Text!;
    public static NativeConnection LoadConnection(NativeAddress address)
    {
        NativeStorage storage = Readable(address);
        return new((ConnectionKind)storage.bytes[address.Offset.Bytes], storage.roots[address.Offset.References].Text!);
    }
    public static NativePath LoadPath(NativeAddress address)
    {
        NativeStorage storage = Readable(address);
        int raw = address.Offset.Bytes, root = address.Offset.References;
        NativeConnection? connection = storage.bytes[raw + 1] == 0 ? null
            : new NativeConnection((ConnectionKind)storage.bytes[raw], storage.roots[root + 4].Text ?? "");
        return new(storage.roots[root].Text!, storage.roots[root + 1].Text, connection,
            storage.roots[root + 2].Text, storage.roots[root + 3].Text);
    }
    public static NativeFunction LoadFunction(NativeAddress address)
    {
        NativeStorage storage = Readable(address);
        int root = address.Offset.References;
        return new(BinaryPrimitives.ReadInt32LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 4)),
            storage.roots[root].Environment, storage.roots[root + 1].Text);
    }
    public static NativeRange LoadRange(NativeAddress address) => Readable(address).roots[address.Offset.References].Range!;
    public static NativeProcess LoadProcess(NativeAddress address) => Readable(address).roots[address.Offset.References].Process!;
    public static TypeRef LoadType(NativeAddress address) => Readable(address).roots[address.Offset.References].Type!;
    public static NativeAddress LoadAddress(NativeAddress address)
    {
        NativeStorage storage = Readable(address);
        ulong bits = BitConverter.IsLittleEndian
            ? Unsafe.ReadUnaligned<ulong>(ref storage.bytes[address.Offset.Bytes])
            : BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 8));
        return storage.roots[address.Offset.References].Allocation!.AddressAt(NativeValue.Unpack(bits),
            storage.roots[address.Offset.References + 1].StorageType);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NativeAddress LoadAddressDirect(NativeAddress address)
    {
        NativeStorage storage = address.Allocation.Storage;
        ulong bits = BitConverter.IsLittleEndian
            ? Unsafe.ReadUnaligned<ulong>(ref storage.bytes[address.Offset.Bytes])
            : BinaryPrimitives.ReadUInt64LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 8));
        return storage.roots[address.Offset.References].Allocation!.AddressAt(NativeValue.Unpack(bits),
            storage.roots[address.Offset.References + 1].StorageType);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NativeStorage Readable(NativeAddress address)
    {
        NativeStorage storage = address.Allocation.Storage;
        storage.RequireInitialized(address.Offset);
        return storage;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreNumber(NativeAddress address, double value)
    {
        NativeStorage storage = address.Allocation.Storage;
        if (BitConverter.IsLittleEndian)
            Unsafe.WriteUnaligned(ref storage.bytes[address.Offset.Bytes], BitConverter.DoubleToUInt64Bits(value));
        else BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 8), BitConverter.DoubleToUInt64Bits(value));
        storage.initialized[address.Offset.Bytes] = true;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StoreBool(NativeAddress address, bool value)
    {
        NativeStorage storage = address.Allocation.Storage;
        storage.bytes[address.Offset.Bytes] = value ? (byte)1 : (byte)0;
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreString(NativeAddress address, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        NativeStorage storage = address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Text = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreConnection(NativeAddress address, NativeConnection value)
    {
        NativeStorage storage = address.Allocation.Storage;
        storage.bytes[address.Offset.Bytes] = (byte)value.Kind;
        storage.roots[address.Offset.References] = new() { Text = value.Address ?? "" };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StorePath(NativeAddress address, NativePath value)
    {
        ArgumentNullException.ThrowIfNull(value.Raw);
        NativeStorage storage = address.Allocation.Storage;
        int raw = address.Offset.Bytes, root = address.Offset.References;
        storage.bytes[raw] = (byte)(value.Connection?.Kind ?? ConnectionKind.Local);
        storage.bytes[raw + 1] = value.Connection.HasValue ? (byte)1 : (byte)0;
        storage.roots[root] = new() { Text = value.Raw };
        storage.roots[root + 1] = new() { Text = value.WorkingDirectory };
        storage.roots[root + 2] = new() { Text = value.HomeDirectory };
        storage.roots[root + 3] = new() { Text = value.LocalSpelling };
        storage.roots[root + 4] = new() { Text = value.Connection?.Address };
        storage.initialized[raw] = true;
    }
    public static void StoreFunction(NativeAddress address, NativeFunction value)
    {
        NativeStorage storage = address.Allocation.Storage;
        int root = address.Offset.References;
        BinaryPrimitives.WriteInt32LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 4), value.Function);
        storage.roots[root] = new() { Environment = value.Environment };
        storage.roots[root + 1] = new() { Text = value.NativeName };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreRange(NativeAddress address, NativeRange value)
    {
        ArgumentNullException.ThrowIfNull(value);
        NativeStorage storage = address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Range = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreProcess(NativeAddress address, NativeProcess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        NativeStorage storage = address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Process = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreType(NativeAddress address, TypeRef value)
    {
        ArgumentNullException.ThrowIfNull(value);
        NativeStorage storage = address.Allocation.Storage;
        storage.roots[address.Offset.References] = new() { Type = value };
        storage.initialized[address.Offset.Bytes] = true;
    }
    public static void StoreAddress(NativeAddress address, NativeAddress value)
    {
        if (value.IsNull) throw new ArgumentException("Null pointers are not namespace values.", nameof(value));
        NativeStorage storage = address.Allocation.Storage;
        ulong packed = NativeValue.Pack(value.Offset);
        if (BitConverter.IsLittleEndian) Unsafe.WriteUnaligned(ref storage.bytes[address.Offset.Bytes], packed);
        else BinaryPrimitives.WriteUInt64LittleEndian(storage.bytes.AsSpan(address.Offset.Bytes, 8), packed);
        storage.roots[address.Offset.References] = new() { Allocation = value.Allocation };
        storage.roots[address.Offset.References + 1] = new() { StorageType = value.StorageType };
        storage.initialized[address.Offset.Bytes] = true;
    }
}
