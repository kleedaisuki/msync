namespace Msync.Core;

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
    public override string ToString() => Connection is null || Connection.Kind == ConnectionKind.Local ? NativeText : $"{Connection} @ {Raw}";
}
