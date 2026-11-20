using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    private static readonly AsyncLocal<SelectedQuery?> Query = new();

    public static async Task<string> RunCommandAsync(NativeConnection connection, string script,
        IReadOnlyList<string> arguments, NativeRuntimeContext? context, CancellationToken token = default)
    {
        if (Query.Value is { } query && query.Connection == connection)
            return await query.InvokeAsync(script, arguments, token);
        using RemoteOperation remote = StartRemote(connection, script, arguments, false, context);
        using CancellationTokenRegistration cancel = token.Register(() => Stop(remote.Process));
        try
        {
            string output = await remote.Process.StandardOutput.ReadToEndAsync(token);
            await remote.FinishAsync(token);
            return output;
        }
        catch { await remote.AbortAsync(); throw; }
    }

    private static RemoteOperation StartRemote(NativeConnection connection, string script,
        IReadOnlyList<string> arguments, bool input, NativeRuntimeContext? context)
    {
        ProcessStartInfo info = NativeTransferProtocol.CreateShellProcessStartInfo(connection.Kind, connection.Address,
            script, arguments, redirectInput: true, context);
        info.RedirectStandardError = true;
        if (context is not null) info.WorkingDirectory = context.WorkingDirectory;
        Process process = Process.Start(info) ?? throw new IOException($"Cannot start native transport for {connection}.");
        if (!input) process.StandardInput.Close();
        return new(process, connection, context?.Timeline, context?.Error);
    }

    private sealed class RemoteOperation : IDisposable
    {
        private readonly NativeConnection connection;
        private readonly Task<string> errors;
        private readonly RuntimeTimeline? timeline;
        private readonly TextWriter? diagnosticOutput;
        private bool ended;
        internal RemoteOperation(Process process, NativeConnection connection, RuntimeTimeline? timeline, TextWriter? diagnosticOutput = null)
        {
            Process = process;
            this.connection = connection;
            this.timeline = timeline;
            this.diagnosticOutput = diagnosticOutput;
            errors = process.StandardError.ReadToEndAsync();
        }
        internal Process Process { get; }
        internal async Task<string> ReadReceiptAsync(CancellationToken token)
        {
            using var span = timeline?.Begin("transport.receipt_wait");
            span?.SetMetric("transport.pid", Process.Id);
            try { return await Process.StandardOutput.ReadToEndAsync(token); }
            catch (Exception error) { span?.Fail(error); throw; }
        }

        internal async Task FinishAsync(CancellationToken token)
        {
            using var span = timeline?.Begin("transport.exit_wait");
            span?.SetMetric("transport.pid", Process.Id);
            try
            {
                await Process.WaitForExitAsync(token);
                string diagnostic = await errors;
                ended = true;
                span?.SetMetric("transport.exit_code", Process.ExitCode);
                if (Process.ExitCode != 0)
                    throw new IOException($"Native transport for {connection} failed with exit status {Process.ExitCode}: {diagnostic.TrimEnd()}");
            }
            catch (Exception error) { span?.Fail(error); throw; }
        }
        internal async Task AbortAsync()
        {
            if (ended) return;
            Stop(Process);
            await Process.WaitForExitAsync(CancellationToken.None);
            string diagnostic = await errors;
            if (diagnostic.Length != 0 && diagnosticOutput is not null)
                await diagnosticOutput.WriteAsync($"Native transport {connection}: {diagnostic}");
            ended = true;
        }
        public void Dispose()
        {
            if (!ended) Stop(Process);
            Process.Dispose();
        }
    }

    private static async Task CopyRemoteAsync(IReadOnlyList<NativePathValue> sources, NativePathValue target,
        NativeRuntimeContext context, bool container, bool replace, bool cohort, CancellationToken token,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, string? recordedMode = null)
    {
        List<string> args = [target.Raw, recordedMode ?? Mode(container, replace)];
        if (!cohort) args.Add("single");
        args.AddRange(sources.Select(path => path.Raw));
        string program = recordedMode is null ? NativeTransferProtocol.NativeCopyProgram(cohort)
            : NativeTransferProtocol.NativeRecordedCopyProgram(kind);
        string output = await RunCommandAsync(Remote(target)!.Value, ObserveInstall(program, context),
            args, context, token);
        RequireReceipt(output, "copy", context.Timeline);
    }

    private static async Task UploadAsync(IReadOnlyList<NativeValue> sources, NativePathValue target,
        NativeRuntimeContext context, bool container, bool replace, bool cohort, CancellationToken token,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, string? recordedMode = null)
    {
        string name = sources[0].Kind == NativeKind.Path ? Basename(sources[0].AsPath()) : "";
        string[] args = [target.Raw, recordedMode ?? Mode(container, replace), name, cohort ? "cohort" : "single",
            sources[0].Kind == NativeKind.String ? "literal" : "filesystem"];
        string program = recordedMode is null ? NativeTransferProtocol.NativeUploadProgram
            : NativeTransferProtocol.NativeRecordedUploadProgram;
        using RemoteOperation remote = StartRemote(Remote(target)!.Value, ObserveInstall(program, context), args, true, context);
        using CancellationTokenRegistration cancel = token.Register(() => Stop(remote.Process));
        try
        {
            await using (TarWriter writer = new(remote.Process.StandardInput.BaseStream, TarEntryFormat.Pax, leaveOpen: true))
            {
                if (cohort) await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "payload"), token);
                foreach (NativeValue source in sources)
                {
                    if (source.Kind == NativeKind.Path)
                    {
                        NativePathValue path = source.AsPath();
                        await NativeTransferProtocol.NativeWriteArchiveNodeAsync(writer, context.LocalPath(path),
                            cohort ? "payload/" + Basename(path) : "payload", token, context.Timeline, kind);
                    }
                    else if (source.Kind == NativeKind.String && !cohort)
                    {
                        using MemoryStream bytes = new(Encoding.UTF8.GetBytes(source.AsString()), false);
                        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "payload") { DataStream = bytes }, token);
                    }
                    else throw new MsyncException($"Cannot transfer {source.Type}.");
                }
            }
            remote.Process.StandardInput.Close();
            string output = await remote.ReadReceiptAsync(token);
            await remote.FinishAsync(token);
            RequireReceipt(output, "upload", context.Timeline);
        }
        catch { await remote.AbortAsync(); throw; }
    }

    private static async Task DownloadAsync(IReadOnlyList<NativePathValue> sources, NativePathValue target,
        NativeRuntimeContext context, bool container, bool replace, CancellationToken token,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, TransferDestinationMode destinationMode = TransferDestinationMode.Exact)
    {
        NativeConnection connection = Remote(sources[0])!.Value;
        bool receipt = RequiresReceipt(connection);
        using RemoteOperation remote = StartRemote(connection, kind == PlannedTransferKind.GenericCopy
            ? NativeTransferProtocol.NativeDownloadProgram(sources.Count, receipt)
            : NativeTransferProtocol.NativeRecordedDownloadProgram(kind, receipt),
            sources.Select(path => path.Raw).ToArray(), receipt, context);
        using CancellationTokenRegistration cancel = token.Register(() => Stop(remote.Process));
        try
        {
            await ReadArchiveAsync(remote.Process.StandardOutput.BaseStream, target, context, container, replace, token, destinationMode);
            if (receipt) await NativeTransferProtocol.NativeAcknowledgeArchiveAsync(remote.Process, token);
            await remote.Process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
            await remote.FinishAsync(token);
        }
        catch { await remote.AbortAsync(); throw; }
    }

    private static async Task RelayAsync(IReadOnlyList<NativeValue> sources, NativePathValue target,
        NativeRuntimeContext context, bool container, bool replace, bool cohort, CancellationToken token,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, string? recordedMode = null)
    {
        if (sources.Any(value => value.Kind != NativeKind.Path))
            throw new MsyncException("A cross-endpoint finite transfer requires Path sources on one endpoint.");
        NativePathValue[] paths = sources.Select(value => value.AsPath()).ToArray();
        NativeConnection sourceConnection = Remote(paths[0])
            ?? throw new MsyncException("A cross-endpoint finite transfer requires sources on one remote endpoint.");
        if (paths.Any(path => Remote(path) != sourceConnection))
            throw new MsyncException("A cross-endpoint finite transfer requires sources on one endpoint.");
        bool receipt = RequiresReceipt(sourceConnection);
        using RemoteOperation source = StartRemote(sourceConnection, kind == PlannedTransferKind.GenericCopy
            ? NativeTransferProtocol.NativeDownloadProgram(paths.Length, receipt)
            : NativeTransferProtocol.NativeRecordedDownloadProgram(kind, receipt),
            paths.Select(path => path.Raw).ToArray(), receipt, context);
        string program = recordedMode is null ? NativeTransferProtocol.NativeRelayProgram
            : NativeTransferProtocol.NativeRecordedRelayProgram;
        using RemoteOperation destination = StartRemote(Remote(target)!.Value, ObserveInstall(program, context),
            [target.Raw, recordedMode ?? Mode(container, replace), Basename(paths[0]), cohort ? "cohort" : "single", "filesystem"], true, context);
        using CancellationTokenRegistration cancel = token.Register(() => { Stop(source.Process); Stop(destination.Process); });
        try
        {
            await NativeTransferProtocol.NativePumpArchiveAsync(source.Process, destination.Process.StandardInput.BaseStream,
                receipt, context.BufferSize, token);
            destination.Process.StandardInput.Close();
            string output = await destination.ReadReceiptAsync(token);
            await source.FinishAsync(token);
            await destination.FinishAsync(token);
            RequireReceipt(output, "relay", context.Timeline);
        }
        catch { await Task.WhenAll(source.AbortAsync(), destination.AbortAsync()); throw; }
    }

    private static async ValueTask<bool> TransferSelectionAsync(NativePathSelection selection, NativePathValue target,
        NativeRuntimeContext context, CancellationToken token)
    {
        NativeConnection connection = Remote(selection.Directory)!.Value;
        NativeConnection? targetConnection = Remote(target);
        bool same = connection == targetConnection;
        bool replace = IsReplacement(target);
        bool receipt = !same && RequiresReceipt(connection);
        string program = NativeTransferProtocol.NativeSelectionProgram(same, receipt);
        using RemoteOperation source = StartRemote(connection, same ? ObserveInstall(program, context) : program,
            [selection.Directory.Raw, target.Raw, Mode(true, replace), selection.Recursive ? "recursive" : "flat"], true, context);
        using CancellationTokenRegistration cancel = token.Register(() => Stop(source.Process));
        try
        {
            IReadOnlyList<string> names = await NativeTransferProtocol.NativeReadSelectionNamesAsync(source.Process.StandardOutput.BaseStream, selection.Pattern, token);
            List<NativeValue> selected = [];
            SelectedQuery? previous = Query.Value;
            using (SelectedQuery query = new(connection, source.Process))
            {
                Query.Value = query;
                try
                {
                    NativeRange discovered = NativeRange.Snapshot(names.Select(name => NativeValue.FromPath(
                        selection.Directory.WithRaw(selection.Directory.Raw.TrimEnd('/') + "/" + name))), TypeRef.Path);
                    IAsyncEnumerable<NativeValue> items = selection.Apply?.Invoke(discovered.Items, token) ?? discovered.Items;
                    await foreach (NativeValue value in items.WithCancellation(token)) selected.Add(value);
                }
                finally { Query.Value = previous; }
            }
            bool sameSources = selected.All(value => value.Kind == NativeKind.Path && Remote(value.AsPath()) == connection);
            if (sameSources) ValidateNames(selected, true);
            string[] paths = sameSources ? selected.Select(value => value.AsPath().Raw).ToArray() : [];
            await NativeTransferProtocol.NativeWriteSelectionNamesAsync(source.Process.StandardInput.BaseStream, paths, token);
            if (!receipt) source.Process.StandardInput.Close();
            if (!sameSources)
            {
                await NativeTransferProtocol.NativePumpArchiveAsync(source.Process, Stream.Null, receipt, context.BufferSize, token);
                await source.FinishAsync(token);
                return await TransferAsync(NativeValue.FromRange(NativeRange.Snapshot(selected,
                    selected.Aggregate(new TypeRef(TypeKind.Never), (type, value) => TypeRef.Union(type, value.Type)))), target, context, token);
            }
            if (same)
            {
                string output = await source.ReadReceiptAsync(token);
                await source.FinishAsync(token);
                RequireReceipt(output, "selector copy", context.Timeline);
                return true;
            }
            if (targetConnection is null)
            {
                await ReadArchiveAsync(source.Process.StandardOutput.BaseStream, target, context, true, replace, token);
                if (receipt) await NativeTransferProtocol.NativeAcknowledgeArchiveAsync(source.Process, token);
                await source.Process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token);
                await source.FinishAsync(token);
                return true;
            }
            if (paths.Length == 0)
            {
                await NativeTransferProtocol.NativePumpArchiveAsync(source.Process, Stream.Null, receipt, context.BufferSize, token);
                await source.FinishAsync(token);
                return true;
            }
            using RemoteOperation destination = StartRemote(targetConnection.Value,
                ObserveInstall(NativeTransferProtocol.NativeRelayProgram, context),
                [target.Raw, Mode(true, replace), "", "cohort", "filesystem"], true, context);
            using CancellationTokenRegistration cancelTarget = token.Register(() => Stop(destination.Process));
            try
            {
                await NativeTransferProtocol.NativePumpArchiveAsync(source.Process, destination.Process.StandardInput.BaseStream,
                    receipt, context.BufferSize, token);
                destination.Process.StandardInput.Close();
                string output = await destination.ReadReceiptAsync(token);
                await source.FinishAsync(token);
                await destination.FinishAsync(token);
                RequireReceipt(output, "selector relay", context.Timeline);
                return true;
            }
            catch { await destination.AbortAsync(); throw; }
        }
        catch { await source.AbortAsync(); throw; }
    }

    private static bool RequiresReceipt(NativeConnection connection) => OperatingSystem.IsWindows() && connection.Kind == ConnectionKind.Wsl;

    private static string ObserveInstall(string program, NativeRuntimeContext context) =>
        ("msync_observe_install=" + (context.Timeline is null ? "no\n" : "yes\n")) + program;

    private static void RequireReceipt(string output, string operation, RuntimeTimeline? timeline)
    {
        const string prefix = "MSYNC-INSTALL ";
        const string suffix = "\nMSYNC-DONE\n";
        using var span = timeline?.Begin("transport.install.result");
        span?.SetAttribute("transport.operation", operation);
        if (timeline is null ? output == "MSYNC-DONE\n" :
            output.StartsWith(prefix, StringComparison.Ordinal) && output.EndsWith(suffix, StringComparison.Ordinal))
        {
            if (timeline is null) return;
            string[] fields = output[prefix.Length..^suffix.Length].Split(' ');
            if (fields.Length == 4
                && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long leaves)
                && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long directories)
                && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long subtrees)
                && long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long fallbacks))
            {
                span?.SetMetric("install.leaves_moved", leaves);
                span?.SetMetric("install.directories_merged", directories);
                span?.SetMetric("install.subtrees_renamed", subtrees);
                span?.SetMetric("install.subtree_fallbacks", fallbacks);
                return;
            }
        }
        IOException error = new($"Remote {operation} returned no completion: {output}");
        span?.Fail(error);
        throw error;
    }

    private sealed class SelectedQuery(NativeConnection connection, Process process) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1);
        internal NativeConnection Connection => connection;
        internal async Task<string> InvokeAsync(string script, IReadOnlyList<string> arguments, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                Stream input = process.StandardInput.BaseStream;
                await input.WriteAsync("MSYNC-QUERY\n"u8.ToArray(), token);
                await NativeTransferProtocol.NativeWriteEncodedLineAsync(input, script, token);
                await input.WriteAsync(Encoding.ASCII.GetBytes(arguments.Count.ToString(CultureInfo.InvariantCulture) + "\n"), token);
                foreach (string argument in arguments) await NativeTransferProtocol.NativeWriteEncodedLineAsync(input, argument, token);
                await input.FlushAsync(token);
                string header = await NativeTransferProtocol.NativeReadHeaderAsync(process.StandardOutput.BaseStream, token);
                string[] fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || fields[0] != "MSYNC-QUERY")
                    throw new IOException($"Invalid selected metadata response from {connection}: {header}");
                int status = int.Parse(fields[1], CultureInfo.InvariantCulture);
                byte[] output = new byte[int.Parse(fields[2], CultureInfo.InvariantCulture)];
                await process.StandardOutput.BaseStream.ReadExactlyAsync(output, token);
                if (status != 0) throw new IOException($"Selected native operation on {connection} failed with exit status {status}.");
                return Encoding.UTF8.GetString(output);
            }
            finally { gate.Release(); }
        }
        public void Dispose() => gate.Dispose();
    }
}
