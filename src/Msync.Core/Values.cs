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
public sealed record PathValue(string Raw, string? WorkingDirectory = null, ConnectionValue? Connection = null, string? HomeDirectory = null) : Value
{
    public override TypeRef Type => TypeRef.Path;
    public string LocalPath(string defaultDirectory, string? defaultHome = null)
    {
        var home = HomeDirectory ?? defaultHome ?? HostContext.HomeDirectory;
        var path = Raw == "~" ? home : Raw.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && Raw.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(home, Raw[2..]) : Raw;
        return Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory ?? defaultDirectory, path);
    }
    public override string ToString() => Connection is null || Connection.Kind == ConnectionKind.Local ? Raw : $"{Connection}::{Raw}";
}
public sealed record PathSelection(PathValue Directory, string Pattern);

public sealed record RangeValue(IAsyncEnumerable<Value> Items, TypeRef ItemType, PathSelection? Selection = null) : Value
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
public sealed record ScopeValue(IReadOnlyDictionary<string, Value> Members) : Value
{
    public override TypeRef Type => TypeRef.Scope;
    public override string ToString() => "scope { " + string.Join(", ", Members.Keys) + " }";
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
    public override TypeRef Type => TypeRef.Process;
    public override string ToString() => $"Process(exit={ExitCode})";
}
