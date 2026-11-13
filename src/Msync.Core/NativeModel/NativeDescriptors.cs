namespace Msync.Core.Native;

public readonly record struct NativeConnection(ConnectionKind Kind, string Address = "")
{
    public override string ToString() => Kind switch
    {
        ConnectionKind.Ssh => $"ssh://{Address}",
        ConnectionKind.Wsl => $"wsl://{Address}",
        _ => Kind.ToString().ToLowerInvariant()
    };
}

public readonly record struct NativePath(string Raw, string? WorkingDirectory = null,
    NativeConnection? Connection = null, string? HomeDirectory = null, string? LocalSpelling = null)
{
    public string NativeText => Connection is null or { Kind: ConnectionKind.Local } ? LocalSpelling ?? Raw : Raw;
    public NativePath WithRaw(string raw, string? localSpelling = null) => this with
    { Raw = raw, LocalSpelling = localSpelling == raw ? null : localSpelling };

    public string LocalPath(string defaultDirectory, string? defaultHome = null)
    {
        string home = HomeDirectory ?? defaultHome ?? HostContext.HomeDirectory;
        string spelling = NativeText;
        string path = spelling == "~" ? home : spelling.StartsWith("~/", StringComparison.Ordinal)
            || OperatingSystem.IsWindows() && spelling.StartsWith("~\\", StringComparison.Ordinal)
                ? System.IO.Path.Combine(home, spelling[2..]) : spelling;
        if (OperatingSystem.IsWindows() && System.IO.Path.IsPathRooted(path) && !System.IO.Path.IsPathFullyQualified(path))
            return System.IO.Path.GetFullPath(path, WorkingDirectory ?? defaultDirectory);
        return System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(WorkingDirectory ?? defaultDirectory, path);
    }

    public override string ToString() => Connection is null or { Kind: ConnectionKind.Local }
        ? NativeText : $"{Connection} @ {Raw}";
}

public readonly record struct NativeFunction(int Function, NativeEnvironment? Environment = null, string? NativeName = null);

public readonly struct NativeCapture
{
    private readonly NativeValue value;
    private readonly NativeAddress location;
    public bool IsLocation { get; }
    public NativeCapture(NativeValue value) => (this.value, location, IsLocation) = (value, default, false);
    public NativeCapture(NativeAddress location) : this(location, location.Type) { }
    public NativeCapture(NativeAddress location, NativeType type)
    {
        if (location.IsNull) throw new ArgumentException("A capture requires a live location.", nameof(location));
        ArgumentNullException.ThrowIfNull(type);
        (value, this.location, IsLocation) = (default, location.OffsetBy(default, type), true);
    }
    public NativeValue Read() => IsLocation ? NativeStorage.LoadTrusted(location.Type, location) : value;
    public bool TryRead(out NativeValue result)
    {
        if (IsLocation && !location.IsInitialized) { result = default; return false; }
        result = Read();
        return true;
    }
    public NativeAddress Location => IsLocation ? location : throw new InvalidOperationException("Capture is by value.");
}

public sealed record NativeCaptureLayout(NativeType Type, bool Location = false);

public sealed class NativeEnvironmentLayout
{
    public NativeEnvironmentLayout(IEnumerable<NativeCaptureLayout> captures)
    {
        NativeCaptureLayout[] contracts = captures.ToArray();
        foreach (NativeCaptureLayout contract in contracts) ArgumentNullException.ThrowIfNull(contract.Type);
        Captures = Array.AsReadOnly(contracts);
        Storage = new(contracts.Select((capture, index) => new NativeField(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            capture.Location ? NativeType.Of(NativeKind.Address) : capture.Type)));
    }
    public IReadOnlyList<NativeCaptureLayout> Captures { get; }
    public NativeLayout Storage { get; }
}

public sealed class NativeEnvironment
{
    private readonly NativeAllocation storage;
    public NativeEnvironment(NativeEnvironmentLayout layout, int layoutId = -1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Layout = layout;
        storage = new(layout.Storage, layoutId);
    }
    public NativeEnvironment(NativeEnvironmentLayout layout, IReadOnlyList<NativeCapture> captures) : this(layout)
    {
        ArgumentNullException.ThrowIfNull(captures);
        if (captures.Count != layout.Captures.Count)
            throw new ArgumentException("Capture count disagrees with the static environment layout.", nameof(captures));
        for (int index = 0; index < captures.Count; index++) Initialize(index, captures[index]);
    }
    public NativeAddress Address => storage.Address;
    public NativeAddress CaptureAddress(int index) => storage.Field(index);
    public void Initialize(int index, NativeCapture capture)
    {
        NativeCaptureLayout contract = Layout.Captures[index];
        if (capture.IsLocation != contract.Location)
            throw new ArgumentException("Capture mode disagrees with the static environment layout.", nameof(capture));
        NativeValue value = capture.IsLocation ? NativeValue.FromAddress(capture.Location) : capture.Read();
        if (capture.IsLocation && !contract.Type.Equals(capture.Location.Type))
            throw new ArgumentException("Capture location type disagrees with its invariant contract.", nameof(capture));
        if (!Layout.Storage[index].Type.Accepts(value))
            throw new ArgumentException("Capture value disagrees with its static storage type.", nameof(capture));
        NativeStorage.StoreTrusted(Layout.Storage[index].Type, storage.Field(index), value);
    }
    public bool TryGetCapture(int index, out NativeCapture capture)
    {
        if (!storage.Field(index).IsInitialized) { capture = default; return false; }
        capture = this[index];
        return true;
    }
    public NativeEnvironmentLayout Layout { get; }
    public int Count => Layout.Captures.Count;
    public NativeCapture this[int index]
    {
        get
        {
            NativeCaptureLayout contract = Layout.Captures[index];
            NativeValue value = NativeStorage.LoadTrusted(Layout.Storage[index].Type, storage.Field(index));
            return contract.Location ? new(value.AsAddress(), contract.Type) : new(value);
        }
    }
}

public sealed record NativePathSelection(NativePath Directory, string Pattern, bool Recursive = false,
    Func<IAsyncEnumerable<NativeValue>, CancellationToken, IAsyncEnumerable<NativeValue>>? Apply = null);

public sealed class NativeRange
{
    public NativeRange(IAsyncEnumerable<NativeValue> items, TypeRef itemType,
        IReadOnlyList<NativeValue>? retainedValues = null, NativePathSelection? selection = null,
        Func<CancellationToken, ValueTask<NativeRange>>? resolve = null, NativeType? physicalItemType = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemType);
        Items = items;
        PhysicalItemType = physicalItemType ?? NativeType.FromTypeRef(itemType);
        ItemType = itemType;
        Type = new(TypeKind.Range, itemType);
        RetainedValues = retainedValues is null ? [] : Array.AsReadOnly(retainedValues.ToArray());
        Selection = selection;
        Resolve = resolve;
    }

    private NativeRange(NativeSnapshot snapshot, TypeRef itemType)
    {
        Items = snapshot;
        PhysicalItemType = snapshot.PhysicalType;
        ItemType = itemType;
        Type = new(TypeKind.Range, itemType);
        RetainedValues = SnapshotValues = snapshot;
    }
    private NativeRange(NativeRange source, TypeRef itemType)
    {
        Items = source.Items;
        PhysicalItemType = source.PhysicalItemType;
        ItemType = itemType;
        Type = new(TypeKind.Range, itemType);
        RetainedValues = source.RetainedValues;
        SnapshotValues = source.SnapshotValues;
        Selection = source.Selection;
        Resolve = source.Resolve is { } resolve
            ? async token => (await resolve(token).ConfigureAwait(false)).WithItemType(itemType) : null;
    }
    public NativeRange WithItemType(TypeRef itemType)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        return ItemType == itemType ? this : new(this, itemType);
    }
    public IAsyncEnumerable<NativeValue> Items { get; }
    public TypeRef ItemType { get; }
    public NativeType PhysicalItemType { get; }
    public TypeRef Type { get; }
    public IReadOnlyList<NativeValue> RetainedValues { get; }
    public NativePathSelection? Selection { get; }
    public Func<CancellationToken, ValueTask<NativeRange>>? Resolve { get; }
    public IReadOnlyList<NativeValue>? SnapshotValues { get; }

    public static NativeRange Snapshot(IEnumerable<NativeValue> values, TypeRef itemType) =>
        new(new NativeSnapshot(values, itemType, NativeType.FromTypeRef(itemType), validateLogical: true), itemType);

    public static NativeRange Snapshot(IEnumerable<NativeValue> values, TypeRef itemType, NativeType physicalItemType) =>
        new(new NativeSnapshot(values, itemType, physicalItemType, validateLogical: false), itemType);

    public static NativeRange FromMemory(NativeAddress address, int count, NativeLayout element, TypeRef itemType, int field = 0)
    {
        NativeFieldLayout selected = element[field];
        NativeStorage.ValidateRegion(address, element, count);
        return Snapshot(Values(), itemType);

        IEnumerable<NativeValue> Values()
        {
            for (int index = 0; index < count; index++)
                yield return NativeStorage.Load(selected.Type, address.OffsetBy(element.Extent * index + selected.Offset));
        }
    }

    // The caller transfers exclusive ownership of the backing region and must not mutate it afterwards.
    public static NativeRange FromOwnedMemory(NativeAddress address, int count, NativeLayout element, TypeRef itemType, int field = 0) =>
        new(new NativeSnapshot(address, count, element, itemType, field), itemType);

    private sealed class NativeSnapshot : IReadOnlyList<NativeValue>, IAsyncEnumerable<NativeValue>
    {
        private readonly NativeAddress first;
        private readonly NativeOffset stride;
        private readonly NativeType type;
        public NativeSnapshot(IEnumerable<NativeValue> values, TypeRef itemType, NativeType physicalItemType, bool validateLogical)
        {
            ArgumentNullException.ThrowIfNull(itemType);
            ArgumentNullException.ThrowIfNull(physicalItemType);
            NativeValue[] inputs = values.ToArray();
            type = physicalItemType;
            Count = inputs.Length;
            NativeLayout element = new(type);
            NativeAllocation storage = new(element, elementCount: Count);
            first = storage.Address.OffsetBy(element[0].Offset, type);
            stride = element.Extent;
            for (int index = 0; index < Count; index++)
            {
                if (!type.Accepts(inputs[index]) || validateLogical && !itemType.Accepts(inputs[index].Type))
                    throw new ArgumentException($"{inputs[index].Type} is not admitted by the Range's declared item ABI {type} and logical descriptor {itemType}.", nameof(values));
                NativeStorage.StoreTrusted(type, first.OffsetBy(stride * index, type), inputs[index]);
            }
        }
        public NativeSnapshot(NativeAddress address, int count, NativeLayout element, TypeRef itemType, int field)
        {
            NativeFieldLayout selected = element[field];
            if ((NativeType.FromTypeRef(itemType).Kinds & selected.Type.Kinds) != selected.Type.Kinds)
                throw new ArgumentException("Owned memory field is not admitted by its Range element contract.", nameof(itemType));
            NativeStorage.ValidateRegion(address, element, count);
            type = selected.Type;
            first = address.OffsetBy(selected.Offset, type);
            stride = element.Extent;
            Count = count;
        }
        public NativeType PhysicalType => type;
        public int Count { get; }
        public NativeValue this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return NativeStorage.LoadTrusted(type, first.OffsetBy(stride * index, type));
            }
        }
        public IEnumerator<NativeValue> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public async IAsyncEnumerator<NativeValue> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            for (int index = 0; index < Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return this[index];
            }
            await Task.CompletedTask;
        }
    }
}

public sealed class NativeProcess(int exitCode, string standardOutput, string standardError)
{
    public int ExitCode { get; } = exitCode;
    public string StandardOutput { get; } = standardOutput;
    public string StandardError { get; } = standardError;
    public Action? AbortOwnedTransport { get; init; }
    public Task<NativeProcess>? Completion { get; init; }
    public IProcessControl? Control { get; init; }
    public int? ProcessId { get; init; }
    public async ValueTask<NativeProcess> WaitAsync(CancellationToken cancellationToken = default) =>
        Completion is { } completion ? await completion.WaitAsync(cancellationToken).ConfigureAwait(false) : this;
}
