using NativePathValue = Msync.Core.Native.NativePath;
using System.Diagnostics;
using System.Globalization;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeConnections(RuntimeTimeline? timeline = null)
{
    private readonly RuntimeTimeline? timeline = timeline;
    private readonly object gate = new();
    private readonly Dictionary<NativeConnection, NativeEndpoint> entries = [];
    private int owners;
    private string? sockets;

    internal void AcquireMultiplexing() { lock (gate) owners++; }
    internal NativeEndpoint Get(NativeConnection connection)
    {
        connection = connection with { Address = connection.Address ?? "" };
        using RuntimeTimelineScope? observation = timeline?.Begin("connection.resource");
        observation?.SetAttribute("connection.endpoint", connection.ToString());
        observation?.SetAttribute("connection.implementation", connection.Kind.ToString());
        lock (gate)
        {
            if (entries.TryGetValue(connection, out NativeEndpoint? entry))
            {
                observation?.SetMetric("resource.reused", 1);
                return entry;
            }
            observation?.SetMetric("resource.created", 1);
            entry = connection.Kind switch
            {
                ConnectionKind.Ssh => new SshEndpoint(connection, this),
                ConnectionKind.Wsl => new WslEndpoint(connection),
                ConnectionKind.Environment => new EnvironmentEndpoint(connection),
                ConnectionKind.SearchPath => new SearchEndpoint(connection),
                _ => new LocalEndpoint(connection)
            };
            entries.Add(connection, entry);
            return entry;
        }
    }

    internal string? Socket(int identity)
    {
        lock (gate)
        {
            if (owners == 0) return null;
            sockets ??= Directory.CreateTempSubdirectory("msync-ssh-").FullName;
            return Path.Combine(sockets, identity.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal async ValueTask ReleaseMultiplexingAsync(NativeRuntimeContext context)
    {
        NativeEndpoint[] closed;
        string? directory;
        lock (gate)
        {
            if (--owners != 0) return;
            closed = entries.Values.ToArray();
            entries.Clear();
            directory = sockets;
            sockets = null;
        }
        foreach (NativeEndpoint endpoint in closed) await endpoint.CloseAsync(context);
        if (directory is not null) Directory.Delete(directory, recursive: true);
    }

    internal abstract class NativeEndpoint(NativeConnection connection) : NativeConnectionResource(connection)
    {
        internal virtual NativePathValue Qualify(NativePathValue path, NativeRuntimeContext context) => PathSemantics.Qualify(path, Connection);
        internal virtual ProcessStartInfo Shell(string script, IReadOnlyList<string> arguments, bool input) =>
            throw new MsyncException($"Connection {Connection} does not support a remote shell.");
        internal virtual ValueTask CloseAsync(NativeRuntimeContext context) => ValueTask.CompletedTask;
        protected static ProcessStartInfo Start(string name, bool input) => new(name)
        { UseShellExecute = false, RedirectStandardInput = input, RedirectStandardOutput = true };
    }

    private sealed class LocalEndpoint(NativeConnection connection) : NativeEndpoint(connection)
    {
        internal override NativePathValue Qualify(NativePathValue path, NativeRuntimeContext context) => NativeBuiltins.QualifyLocal(path, Connection, context);
    }
    private sealed class EnvironmentEndpoint(NativeConnection connection) : NativeEndpoint(connection)
    {
        internal override NativePathValue Qualify(NativePathValue path, NativeRuntimeContext context) =>
            new(Environment.GetEnvironmentVariable(path.Raw)
                ?? throw new MsyncException($"Environment variable '{path.Raw}' is not set"),
                context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
    }
    private sealed class SearchEndpoint(NativeConnection connection) : NativeEndpoint(connection)
    {
        internal override NativePathValue Qualify(NativePathValue path, NativeRuntimeContext context) =>
            new(NativeBuiltins.ResolvePathName(path.Raw, context), context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
    }
    private sealed class WslEndpoint(NativeConnection connection) : NativeEndpoint(connection)
    {
        internal override ProcessStartInfo Shell(string script, IReadOnlyList<string> arguments, bool input)
        {
            ProcessStartInfo info = Start("wsl.exe", input);
            if (Connection.Address.Length != 0) { info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(Connection.Address); }
            foreach (string value in new[] { "--exec", "sh", "-c", script, "sh" }) info.ArgumentList.Add(value);
            foreach (string value in arguments) info.ArgumentList.Add(value);
            return info;
        }
    }
    private sealed class SshEndpoint : NativeEndpoint
    {
        private readonly NativeConnections table;
        private readonly string address;
        private readonly string? port;
        private readonly string? configuration;
        private string? socket;
        private readonly int identity;
        private static int nextIdentity;

        internal SshEndpoint(NativeConnection connection, NativeConnections table) : base(connection)
        {
            this.table = table;
            identity = Interlocked.Increment(ref nextIdentity);
            configuration = Environment.GetEnvironmentVariable("MSYNC_SSH_CONFIG");
            address = connection.Address.StartsWith("ssh://", StringComparison.Ordinal) ? connection.Address[6..] : connection.Address;
            if (Uri.TryCreate("ssh://" + address, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
            {
                port = uri.Port.ToString(CultureInfo.InvariantCulture);
                address = (uri.UserInfo.Length > 0 ? uri.UserInfo + "@" : "") + uri.Host;
            }
        }

        private ProcessStartInfo Client(bool input)
        {
            ProcessStartInfo info = Start("ssh", input);
            info.ArgumentList.Add("-T");
            if (!string.IsNullOrEmpty(configuration)) { info.ArgumentList.Add("-F"); info.ArgumentList.Add(configuration); }
            if (port is not null) { info.ArgumentList.Add("-p"); info.ArgumentList.Add(port); }
            return info;
        }

        internal override ProcessStartInfo Shell(string script, IReadOnlyList<string> arguments, bool input)
        {
            using RuntimeTimelineScope? observation = table.timeline?.Begin("connection.command.prepare");
            observation?.SetAttribute("connection.endpoint", Connection.ToString());
            ProcessStartInfo info = Client(input);
            if (OperatingSystem.IsWindows())
            {
                // Win32 OpenSSH cannot carry redirected streams over the Unix mux.
                // Override ControlPath from ssh_config as well as our own defaults.
                info.ArgumentList.Add("-oControlMaster=no");
                info.ArgumentList.Add("-oControlPersist=no");
                info.ArgumentList.Add("-oControlPath=none");
                observation?.SetAttribute("ssh.session.strategy", "windows_direct");
            }
            else if ((socket ??= table.Socket(identity)) is { } controlPath)
            {
                info.ArgumentList.Add("-oControlMaster=auto");
                info.ArgumentList.Add("-oControlPersist=60");
                info.ArgumentList.Add("-S"); info.ArgumentList.Add(controlPath);
                observation?.SetAttribute("ssh.session.strategy", "owned_mux");
            }
            else observation?.SetAttribute("ssh.session.strategy", "native_config");
            info.ArgumentList.Add("--"); info.ArgumentList.Add(address);
            info.ArgumentList.Add("sh -c " + NativeTransferProtocol.Quote(script) + " sh"
                + string.Concat(arguments.Select(value => " " + NativeTransferProtocol.Quote(value))));
            return info;
        }

        internal override async ValueTask CloseAsync(NativeRuntimeContext context)
        {
            if (socket is null || !File.Exists(socket)) return;
            using var observation = context.Timeline?.Begin("connection.close");
            observation?.SetAttribute("connection.endpoint", Connection.ToString());
            ProcessStartInfo info = Client(false);
            info.RedirectStandardError = true;
            info.ArgumentList.Add("-S"); info.ArgumentList.Add(socket);
            info.ArgumentList.Add("-O"); info.ArgumentList.Add("exit");
            info.ArgumentList.Add("--"); info.ArgumentList.Add(address);
            using Process process = Process.Start(info)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            string diagnostic = await error;
            await output;
            observation?.SetMetric("process.exit_code", process.ExitCode);
            if (process.ExitCode != 0) await context.Error.WriteAsync(diagnostic);
        }
    }
}
