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
    public static async Task<string> RunCommandAsync(NativeConnection connection, string script,
        IReadOnlyList<string> arguments, NativeRuntimeContext? context, CancellationToken token = default)
    {
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

    private static async Task UploadPayloadAsync(IReadOnlyList<NativePathValue>? paths, string? literal, NativePathValue target,
        NativeRuntimeContext context, bool container, bool replace, bool cohort, CancellationToken token,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, string? recordedMode = null)
    {
        string name = paths is null ? "" : Basename(paths[0]);
        string[] args = [target.Raw, recordedMode ?? Mode(container, replace), name, cohort ? "cohort" : "single",
            paths is null ? "literal" : "filesystem"];
        string program = recordedMode is null ? NativeTransferProtocol.NativeUploadProgram
            : NativeTransferProtocol.NativeRecordedUploadProgram;
        using RemoteOperation remote = StartRemote(Remote(target)!.Value, ObserveInstall(program, context), args, true, context);
        using CancellationTokenRegistration cancel = token.Register(() => Stop(remote.Process));
        try
        {
            await using (TarWriter writer = new(remote.Process.StandardInput.BaseStream, TarEntryFormat.Pax, leaveOpen: true))
            {
                if (cohort) await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "payload"), token);
                if (paths is not null)
                {
                    foreach (var path in paths)
                        await NativeTransferProtocol.NativeWriteArchiveNodeAsync(writer, context.LocalPath(path),
                            cohort ? "payload/" + Basename(path) : "payload", token, context.Timeline, kind);
                }
                else
                {
                    using MemoryStream bytes = new(Encoding.UTF8.GetBytes(literal!), false);
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "payload") { DataStream = bytes }, token);
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

    private static async Task RelayPathsAsync(IReadOnlyList<NativePathValue> paths, NativePathValue target,
        NativeRuntimeContext context, bool container, bool replace, bool cohort, CancellationToken token,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, string? recordedMode = null)
    {
        NativeConnection sourceConnection = Remote(paths[0])
            ?? throw new MsyncException("A cross-endpoint finite transfer requires sources on one remote endpoint.");
        if (paths.Any(path => Remote(path) != sourceConnection))
            throw new MsyncException("A cross-endpoint finite transfer requires sources on one endpoint.");
        bool receipt = RequiresReceipt(sourceConnection);
        using RemoteOperation source = StartRemote(sourceConnection, kind == PlannedTransferKind.GenericCopy
            ? NativeTransferProtocol.NativeDownloadProgram(paths.Count, receipt)
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

    private static async ValueTask<bool> TransferSelectionAsync(NativePathValue directory, string pattern, NativePathValue target,
        NativeRuntimeContext context, CancellationToken token)
    {
        NativeConnection connection = Remote(directory)!.Value;
        NativeConnection? targetConnection = Remote(target);
        bool same = connection == targetConnection;
        bool replace = IsReplacement(target);
        bool receipt = !same && RequiresReceipt(connection);
        string program = NativeTransferProtocol.NativeSelectionProgram(same, receipt);
        using RemoteOperation source = StartRemote(connection, same ? ObserveInstall(program, context) : program,
            [directory.Raw, target.Raw, Mode(true, replace), "flat"], true, context);
        using CancellationTokenRegistration cancel = token.Register(() => Stop(source.Process));
        try
        {
            var names = await NativeTransferProtocol.NativeReadSelectionNamesAsync(source.Process.StandardOutput.BaseStream, pattern, token);
            string prefix = directory.Raw.TrimEnd('/') + "/";
            var selected = names.Select(name => directory.WithRaw(prefix + name)).ToArray();
            ValidatePathNames(selected);
            string[] paths = selected.Select(path => path.Raw).ToArray();
            await NativeTransferProtocol.NativeWriteSelectionNamesAsync(source.Process.StandardInput.BaseStream, paths, token);
            if (!receipt) source.Process.StandardInput.Close();
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
}
