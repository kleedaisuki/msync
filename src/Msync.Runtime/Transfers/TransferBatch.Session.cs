using System.Diagnostics;
using System.Globalization;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static readonly AsyncLocal<RemoteSessionScope?> RemoteSessions = new();

    private sealed class RemoteSessionScope(RemoteTransferSession session, RemoteSessionScope? previous) : IDisposable
    {
        internal RemoteTransferSession Session { get; } = session;
        internal RemoteSessionScope? Previous { get; } = previous;
        public void Dispose() => RemoteSessions.Value = Previous;
    }

    public static RemoteTransferSession OpenSession(ConnectionValue connection, CancellationToken cancellationToken = default)
        => new(connection, cancellationToken, null);

    internal static RemoteTransferSession OpenSession(ConnectionValue connection, CancellationToken cancellationToken, RuntimeTimeline? timeline)
        => new(connection, cancellationToken, timeline);

    public static IDisposable UseSession(RemoteTransferSession session)
    {
        var scope = new RemoteSessionScope(session, RemoteSessions.Value);
        RemoteSessions.Value = scope;
        return scope;
    }

    private static RemoteTransferSession? CurrentSession(ConnectionValue connection)
    {
        for (var scope = RemoteSessions.Value; scope is not null; scope = scope.Previous)
            if (scope.Session.Connection == connection) return scope.Session;
        return null;
    }

    private static async Task<IReadOnlyList<TransferBatchOutcome>> CopyRemoteBatchAsync(
        IReadOnlyList<TransferBatchItem> items, BatchDirection direction, ConnectionValue connection,
        RuntimeContext context, CancellationToken cancellationToken)
    {
        if (CurrentSession(connection) is { } selected)
            return await selected.CopyBatchAsync(items, context, cancellationToken);
        try
        {
            await using var session = OpenSession(connection, cancellationToken, context.Timeline);
            return await session.CopyBatchAsync(items, context, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return items.Select(item => new TransferBatchOutcome(false,
                new IOException($"Cannot transfer {item.Source} -> {item.Destination} using {connection}: {error.Message}", error))).ToArray();
        }
    }

    public sealed partial class RemoteTransferSession : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Stream input;
        private readonly Stream output;
        private readonly SemaphoreSlim serial = new(1);
        private readonly CancellationTokenRegistration cancellation;
        private readonly RuntimeTimeline? timeline;
        private volatile bool aborted;
        private volatile bool disposed;
        public ConnectionValue Connection { get; }
        public bool IsActive => !disposed && !aborted && !process.HasExited;

        internal RemoteTransferSession(ConnectionValue connection, CancellationToken cancellationToken, RuntimeTimeline? timeline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connection = connection;
            this.timeline = timeline;
            process = Start(connection, BatchServer, [], redirectInput: true, timeline, "batch-session");
            input = new BufferedStream(process.StandardInput.BaseStream, 4096);
            output = new BufferedStream(process.StandardOutput.BaseStream, 4096);
            cancellation = cancellationToken.Register(Abort);
        }

        public async Task<string> QueryAsync(string script, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            using var activity = BeginSession("transport.query", timeline);
            activity?.SetMetric("query.arguments", arguments.Count);
            await EnterSerialAsync(cancellationToken, activity);
            using var registration = cancellationToken.Register(Abort);
            try
            {
                BatchResponse response;
                try
                {
                    await WriteCommandAsync(input, "QUERY", arguments.Count, cancellationToken);
                    await WriteEncodedLineAsync(input, script, cancellationToken);
                    foreach (string argument in arguments) await WriteEncodedLineAsync(input, argument, cancellationToken);
                    await input.FlushAsync(cancellationToken);
                    response = await ReadResponseAsync(output, -1, cancellationToken);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or FormatException or OverflowException)
                {
                    Abort();
                    throw;
                }
                activity?.SetAttribute("transport.response_status", response.Status.ToString(CultureInfo.InvariantCulture));
                if (response.Status != 0)
                    throw new IOException($"Selected native operation on {Connection} failed: {response.Detail}");
                return response.Detail;
            }
            catch (Exception error) { activity?.Fail(error); throw; }
            finally { serial.Release(); }
        }

        public async Task<IReadOnlyList<TransferBatchOutcome>> CopyBatchAsync(IReadOnlyList<TransferBatchItem> items,
            RuntimeContext context, CancellationToken cancellationToken = default)
        {
            if (items.Count == 0) return [];
            BatchRoute route = Route(items[0]);
            if (route.Direction is BatchDirection.DirectoryActions or BatchDirection.Relay || items.Any(item => Route(item) != route))
            {
                using var selected = UseSession(this);
                return await TransferEngine.CopyBatchAsync(items, context, cancellationToken);
            }
            if (route.Connection != Connection || route.Direction is BatchDirection.Local or BatchDirection.Other)
                throw new ArgumentException($"The transfer batch is not owned by {Connection}.", nameof(items));
            using var activity = BeginSession("transport.batch", context.Timeline ?? timeline);
            if (activity is not null)
            {
                activity.SetAttribute("transfer.direction", route.Direction.ToString());
                activity.SetMetric("transfer.items", items.Count);
            }
            await EnterSerialAsync(cancellationToken, activity);
            using var registration = cancellationToken.Register(Abort);
            var outcomes = new TransferBatchOutcome?[items.Count];
            try
            {
                await WriteCommandAsync(input, route.Direction.ToString().ToUpperInvariant(), items.Count, cancellationToken);
                Task writer = route.Direction == BatchDirection.Upload
                    ? WriteUploadsAsync(input, items, context, outcomes, cancellationToken)
                    : WritePathsAsync(input, items, route.Direction, cancellationToken);
                Task reader = route.Direction == BatchDirection.Download
                    ? ReadDownloadsAsync(output, items, context, outcomes, cancellationToken)
                    : ReadOutcomesAsync(output, items, outcomes, cancellationToken, context.Timeline ?? timeline);
                await CompleteDuplexAsync(writer, reader);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                activity?.Fail(error);
                Abort();
                await process.WaitForExitAsync(CancellationToken.None);
                for (int index = 0; index < outcomes.Length; index++)
                    outcomes[index] ??= new(false, new IOException($"Native batch on {Connection} failed for {items[index].Source} -> {items[index].Destination}: {error.Message}", error));
            }
            catch (Exception error) { activity?.Fail(error); throw; }
            finally { serial.Release(); }
            var results = outcomes.Select(outcome => outcome!).ToArray();
            if (activity is not null)
            {
                var succeeded = results.Count(result => result.Success);
                activity.SetMetric("transfer.succeeded", succeeded);
                activity.Complete(succeeded == results.Length);
            }
            return results;
        }

        private RuntimeTimelineScope? BeginSession(string name, RuntimeTimeline? recorder)
        {
            var activity = BeginTransport(recorder, name, Connection);
            activity?.SetMetric("transport.pid", process.Id);
            return activity;
        }

        private Task EnterSerialAsync(CancellationToken token, RuntimeTimelineScope? activity)
        {
            Task wait = serial.WaitAsync(token);
            return activity is null ? wait : ObserveWait(wait, activity);

            static async Task ObserveWait(Task wait, RuntimeTimelineScope activity)
            {
                activity.SetAttribute("wait.pending_on_entry", wait.IsCompleted ? "false" : "true");
                try { await wait; }
                catch (Exception error) { activity.Fail(error); throw; }
            }
        }

        private async Task CompleteDuplexAsync(Task writer, Task reader)
        {
            try
            {
                Task first = await Task.WhenAny(writer, reader);
                await first;
                await Task.WhenAll(writer, reader);
            }
            catch
            {
                Abort();
                await process.WaitForExitAsync(CancellationToken.None);
                try { await Task.WhenAll(writer, reader); } catch { }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            using var activity = BeginSession("transport.session.close", timeline);
            disposed = true;
            await EnterSerialAsync(CancellationToken.None, activity);
            try
            {
                // Commands flush before their response; disposal must not turn an
                // already reported failure/cancellation into a second pipe error.
                try { await input.DisposeAsync(); } catch (IOException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                activity?.SetAttribute("transport.exit_code", process.ExitCode.ToString(CultureInfo.InvariantCulture));
                activity?.SetAttribute("transport.aborted", aborted ? "true" : "false");
                activity?.Complete(!aborted && process.ExitCode == 0);
            }
            catch (Exception error) { activity?.Fail(error); throw; }
            finally
            {
                cancellation.Dispose();
                await output.DisposeAsync();
                process.Dispose();
                serial.Release();
                serial.Dispose();
            }
        }

        private void Abort()
        {
            aborted = true;
            Kill(process);
        }
    }

    private readonly record struct BatchResponse(int Status, string Detail);

    private static async ValueTask WriteCommandAsync(Stream stream, string command, int count, CancellationToken token)
        => await stream.WriteAsync(Encoding.ASCII.GetBytes(command + " " + count.ToString(CultureInfo.InvariantCulture) + "\n"), token);

    private static async Task WritePathsAsync(Stream stream, IReadOnlyList<TransferBatchItem> items,
        BatchDirection direction, CancellationToken token)
    {
        foreach (var item in items)
        {
            await WriteEncodedLineAsync(stream, ((PathValue)item.Source).Raw, token);
            await WriteEncodedLineAsync(stream, item.Kind.ToString(), token);
            if (direction == BatchDirection.Download) continue;
            await WriteEncodedLineAsync(stream, item.Mode == TransferDestinationMode.Replace
                ? item.Destination.Raw.TrimEnd('/') + "/." : item.Destination.Raw, token);
            await WriteEncodedLineAsync(stream, item.Mode == TransferDestinationMode.Replace ? "replace" : BatchMode(item), token);
        }
        await stream.FlushAsync(token);
    }

    private static async Task<BatchResponse> ReadResponseAsync(Stream stream, int index, CancellationToken token)
    {
        string header = await ReadHeaderAsync(stream, token);
        string[] fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 4 || fields[0] != "MSYNC-BATCH"
            || int.Parse(fields[1], CultureInfo.InvariantCulture) != index)
            throw new IOException($"Invalid native batch response: {header}");
        int status = int.Parse(fields[2], CultureInfo.InvariantCulture);
        int length = int.Parse(fields[3], CultureInfo.InvariantCulture);
        if (length == 0) return new(status, "");
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return new(status, Encoding.UTF8.GetString(bytes));
    }

    private static async Task ReadOutcomesAsync(Stream stream, IReadOnlyList<TransferBatchItem> items,
        TransferBatchOutcome?[] outcomes, CancellationToken token, RuntimeTimeline? timeline = null)
    {
        for (int index = 0; index < items.Count; index++)
        {
            using var activity = BeginBatchItem(timeline, "transport.batch.response", items[index], index);
            try
            {
                var response = await ReadResponseAsync(stream, index, token);
                outcomes[index] ??= response.Status == 0 ? new(true)
                    : new(false, new IOException($"Cannot transfer {items[index].Source} -> {items[index].Destination}: {response.Detail}"));
                if (activity is not null)
                {
                    activity.SetAttribute("transport.response_status", response.Status.ToString(CultureInfo.InvariantCulture));
                    if (outcomes[index]?.Error is { } failure) activity.Fail(failure);
                }
            }
            catch (Exception error) { activity?.Fail(error); throw; }
        }
    }

    private static RuntimeTimelineScope? BeginBatchItem(RuntimeTimeline? timeline, string name, TransferBatchItem item, int index)
    {
        var activity = timeline?.Begin(name);
        if (activity is null) return null;
        TransferTimeline.Source(activity, item.Source);
        TransferTimeline.Path(activity, "destination", item.Destination);
        activity.SetMetric("transfer.item_index", index);
        activity.SetAttribute("transfer.kind", item.Kind.ToString());
        activity.SetAttribute("transfer.mode", item.Mode.ToString());
        return activity;
    }
}
