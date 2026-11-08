using System.Globalization;

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
    public override string ToString() => Connection is null || Connection.Kind == ConnectionKind.Local ? NativeText : $"{Connection}::{Raw}";
}
public sealed record PathSelection(PathValue Directory, string Pattern,
    Func<IAsyncEnumerable<Value>, CancellationToken, IAsyncEnumerable<Value>>? Apply = null, bool Recursive = false);

public sealed record RangeValue(IAsyncEnumerable<Value> Items, TypeRef ItemType, PathSelection? Selection = null,
    Func<CancellationToken, ValueTask<RangeValue>>? Resolve = null) : Value
{
    public RangeValue(IEnumerable<Value> items, TypeRef itemType) : this(Enumerate(items), itemType) { }
    private static async IAsyncEnumerable<Value> Enumerate(IEnumerable<Value> items)
    {
        foreach (var item in items) yield return item;
        await Task.CompletedTask;
    }
    public override TypeRef Type => new(TypeKind.Range, ItemType);
    public override string ToString() => $"Range<{ItemType}>";
}
public sealed record ScopeValue : Value
{
    private sealed class MemberView(IReadOnlyDictionary<string, Value> members) : IReadOnlyDictionary<string, Value>
    {
        public int Count => members.Count;
        public IEnumerable<string> Keys => members.Keys;
        public IEnumerable<Value> Values => members.Values.Select(Read);
        public Value this[string name] => Read(members[name]);
        public bool ContainsKey(string name) => members.ContainsKey(name);
        public bool TryGetValue(string name, out Value value)
        {
            if (members.TryGetValue(name, out var stored)) { value = Read(stored); return true; }
            value = Value.Null; return false;
        }
        public IEnumerator<KeyValuePair<string, Value>> GetEnumerator() =>
            members.Select(pair => KeyValuePair.Create(pair.Key, Read(pair.Value))).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        private static Value Read(Value value) => value is CellValue cell ? cell.Data : value;
    }
    private readonly Dictionary<string, Value> fields;
    public ScopeValue(IReadOnlyDictionary<string, Value> members)
    {
        fields = members as Dictionary<string, Value> ?? members.ToDictionary();
        Members = new MemberView(fields);
    }
    public ScopeValue(IReadOnlyDictionary<string, Value> members, int allocationSite) : this(members) => AllocationSite = allocationSite;
    public IReadOnlyDictionary<string, Value> Members { get; }
    public int AllocationSite { get; init; }
    public void SetMember(string name, Value value)
    {
        if (!fields.TryGetValue(name, out var previous)) throw new MsyncException($"Scope has no member '{name}'");
        if (previous is CellValue cell) cell.Data = value;
        else fields[name] = value;
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
    public Task<ProcessValue>? Completion { get; init; }
    public override TypeRef Type => TypeRef.Process;
    public override string ToString() => $"Process(exit={ExitCode})";
}
