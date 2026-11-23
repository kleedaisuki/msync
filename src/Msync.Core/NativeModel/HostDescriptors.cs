namespace Msync.Core.Native;

public class NativeConnectionResource(NativeConnection connection)
{
    public NativeConnection Connection { get; } = connection with { Address = connection.Address ?? "" };
}

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
