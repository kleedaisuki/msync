using System.Globalization;
using System.Runtime.CompilerServices;

namespace Msync.Core;

public abstract record Value
{
    public abstract TypeRef Type { get; }
    public static readonly Value Null = new NullValue();
    public static readonly Value True = new BoolValue(true);
    public static readonly Value False = new BoolValue(false);
}
public sealed record NullValue : Value
{
    public override TypeRef Type => TypeRef.Null;
    public override string ToString() => "null";
}
public sealed record BoolValue(bool Data) : Value
{
    public override TypeRef Type => TypeRef.Bool;
    public override string ToString() => Data ? "true" : "false";
}
public sealed record StringValue(string Data) : Value
{
    public override TypeRef Type => TypeRef.String;
    public override string ToString() => Data;
}
public sealed record NumberValue(double Data) : Value
{
    public override TypeRef Type => TypeRef.Number;
    public override string ToString() => Data.ToString(CultureInfo.InvariantCulture);
}
public enum ConnectionKind : byte { Local, Ssh, Wsl, Environment, SearchPath }
public sealed record ConnectionValue(ConnectionKind Kind, string Address = "") : Value
{
    public override TypeRef Type => TypeRef.Connection;
    public override string ToString() => Kind == ConnectionKind.Ssh ? $"ssh://{Address}" : Kind == ConnectionKind.Wsl ? $"wsl://{Address}" : Kind.ToString().ToLowerInvariant();
}
public sealed record PathValue(string Raw, string? WorkingDirectory = null, ConnectionValue? Connection = null, string? HomeDirectory = null,
    string? LocalSpelling = null) : Value
{
    public PathValue(string raw, string? workingDirectory, ConnectionValue? connection, string? homeDirectory)
        : this(raw, workingDirectory, connection, homeDirectory, null) { }
    public override TypeRef Type => TypeRef.Path;
    public string NativeText => Connection?.Kind is null or ConnectionKind.Local ? LocalSpelling ?? Raw : Raw;
    public PathValue WithRaw(string raw, string? localSpelling = null) => this with
    { Raw = raw, LocalSpelling = localSpelling == raw ? null : localSpelling };
    public void Deconstruct(out string raw, out string? workingDirectory, out ConnectionValue? connection, out string? homeDirectory)
    { raw = Raw; workingDirectory = WorkingDirectory; connection = Connection; homeDirectory = HomeDirectory; }
    public string LocalPath(string defaultDirectory, string? defaultHome = null)
    {
        var home = HomeDirectory ?? defaultHome ?? HostContext.HomeDirectory;
        var spelling = NativeText;
        var path = spelling == "~" ? home : spelling.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && spelling.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, spelling[2..]) : spelling;
        if (OperatingSystem.IsWindows() && Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            return Path.GetFullPath(path, WorkingDirectory ?? defaultDirectory);
        return Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory ?? defaultDirectory, path);
    }
    public override string ToString() => Connection is null || Connection.Kind == ConnectionKind.Local ? NativeText : $"{Raw} @ {Connection}";
}
public sealed record PathSelection(PathValue Directory, string Pattern,
    Func<IAsyncEnumerable<Value>, CancellationToken, IAsyncEnumerable<Value>>? Apply = null, bool Recursive = false);

public interface IRangeTypeOrigin
{
    TypeRef InitialItemType { get; }
}

public sealed record RangeValue(IAsyncEnumerable<Value> Items, TypeRef ItemType, PathSelection? Selection = null,
    Func<CancellationToken, ValueTask<RangeValue>>? Resolve = null) : Value
{
    private TypeRef itemType = ItemType;
    public TypeRef ItemType { get => itemType; init => itemType = value; }

    public void InferItemType(Value value)
    {
        if (itemType.Kind == TypeKind.Never) itemType = value.Type;
    }

    // A first-write carrier solution must not move a live Range between hash buckets.
    private TypeRef IdentityItemType => Items is IRangeTypeOrigin origin ? origin.InitialItemType : ItemType;
    public bool Equals(RangeValue? other) => ReferenceEquals(this, other) || other is not null
        && EqualityComparer<IAsyncEnumerable<Value>>.Default.Equals(Items, other.Items)
        && IdentityItemType == other.IdentityItemType && Selection == other.Selection && Resolve == other.Resolve;
    public override int GetHashCode() => HashCode.Combine(typeof(RangeValue), Items, IdentityItemType, Selection, Resolve);

    // Roots already retained by storage or a lazy producer; ownership never
    // enumerates a Range or keeps discarded snapshot inputs alive.
    public IReadOnlyList<Value>? RetainedValues { get; set; }
    public RangeValue(IEnumerable<Value> items, TypeRef itemType) : this(Enumerate(items), itemType)
        => RetainedValues = items as IReadOnlyList<Value>;
    private static async IAsyncEnumerable<Value> Enumerate(IEnumerable<Value> items)
    {
        foreach (var item in items) yield return item;
        await Task.CompletedTask;
    }
    public override TypeRef Type => new(TypeKind.Range, ItemType);
    public override string ToString() => $"Range<{ItemType}>";
}
public sealed record ScopeValue : Value, IReadOnlyDictionary<string, Value>
{
    private readonly Value[] fields;
    public bool Equals(ScopeValue? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public ScopeValue(IReadOnlyDictionary<string, Value> members)
    {
        var names = new string[members.Count];
        fields = new Value[members.Count];
        int index = 0;
        foreach (var member in members) { names[index] = member.Key; fields[index++] = member.Value; }
        Layout = ScopeLayout.Own(names);
    }
    public ScopeValue(ScopeLayout layout, Value[] fields)
    {
        if (fields.Length != layout.Count) throw new ArgumentException("Scope field storage disagrees with its layout.", nameof(fields));
        Layout = layout;
        this.fields = fields;
    }
    public ScopeValue(IReadOnlyDictionary<string, Value> members, int allocationSite) : this(members) => AllocationSite = allocationSite;
    public ScopeLayout Layout { get; }
    public IReadOnlyDictionary<string, Value> Members => this;
    public int AllocationSite { get; init; }
    public int Count => Layout.Count;
    public IEnumerable<string> Keys => Layout.Names;
    public IEnumerable<Value> Values => fields.Select(Read);
    public Value this[string name] => GetField(Layout.GetOffset(name));
    public bool ContainsKey(string name) => Layout.TryGetOffset(name, out _);
    public bool TryGetValue(string name, out Value value)
    {
        if (Layout.TryGetOffset(name, out int offset)) { value = GetField(offset); return true; }
        value = Value.Null; return false;
    }
    public Value GetField(int offset) => Read(fields[offset]);
    public bool IsField(int offset, string name) => (uint)offset < (uint)Layout.Count
        && string.Equals(Layout.Names[offset], name, StringComparison.Ordinal);
    public bool TryGetFieldOffset(string name, out int offset) => Layout.TryGetOffset(name, out offset);
    public void SetField(int offset, Value value)
    {
        if (fields[offset] is CellValue cell) cell.Data = value;
        else fields[offset] = value;
    }
    public void SetMember(string name, Value value)
    {
        if (!Layout.TryGetOffset(name, out int offset)) throw new MsyncException($"Scope has no member '{name}'");
        SetField(offset, value);
    }
    public ScopeValue MapFields(Func<Value, Value> transform)
    {
        var values = new Value[fields.Length];
        for (int index = 0; index < values.Length; index++) values[index] = transform(GetField(index));
        return new(Layout, values) { AllocationSite = AllocationSite };
    }
    private static Value Read(Value value) => value is CellValue cell ? cell.Data : value;
    public Enumerator GetEnumerator() => new(this);
    IEnumerator<KeyValuePair<string, Value>> IEnumerable<KeyValuePair<string, Value>>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public struct Enumerator(ScopeValue scope) : IEnumerator<KeyValuePair<string, Value>>
    {
        private int position = -1;
        public readonly KeyValuePair<string, Value> Current => KeyValuePair.Create(scope.Layout.Names[position], scope.GetField(position));
        readonly object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++position < scope.Count;
        public void Reset() => position = -1;
        public readonly void Dispose() { }
    }
    public override TypeRef Type => TypeRef.Scope;
    public override string ToString() => "scope { " + string.Join(", ", Members.Keys) + " }";
}
public sealed record CellValue(Value Data) : Value
{
    public Value Data { get; set; } = Data;
    public override TypeRef Type => Data.Type;
    public override string ToString() => Data.ToString() ?? "null";
}
public sealed record FunctionValue(int Function, IReadOnlyList<Value>? Captures = null, string? NativeName = null) : Value
{
    public override TypeRef Type => TypeRef.Function;
    public override string ToString() => NativeName is null ? $"fn #{Function}" : $"native fn {NativeName}";
}
public sealed record TypeValue(TypeRef Data) : Value
{
    public override TypeRef Type => TypeRef.Type;
    public override string ToString() => Data.ToString();
}
public sealed record ProcessValue(int ExitCode, string StandardOutput, string StandardError) : Value
{
    public Action? AbortOwnedTransport { get; init; }
    public Task<ProcessValue>? Completion { get; init; }
    public IProcessControl? Control { get; init; }
    public int? ProcessId { get; init; }
    public override TypeRef Type => TypeRef.Process;
    public override string ToString() => $"Process(exit={ExitCode})";
}

public interface IProcessControl
{
    int ProcessId { get; }
    bool IsRunning { get; }
    ValueTask WriteInputAsync(string text, CancellationToken cancellationToken);
    ValueTask CloseInputAsync(CancellationToken cancellationToken);
    bool Kill();
    bool Terminate();
}
