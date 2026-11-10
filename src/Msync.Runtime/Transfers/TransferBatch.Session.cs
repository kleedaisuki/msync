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
        => new(connection, cancellationToken);

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
            await using var session = OpenSession(connection, cancellationToken);
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
        private volatile bool aborted;
        private volatile bool disposed;
        public ConnectionValue Connection { get; }
        public bool IsActive => !disposed && !aborted && !process.HasExited;

        internal RemoteTransferSession(ConnectionValue connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connection = connection;
            process = Start(connection, BatchServer, [], redirectInput: true);
            input = new BufferedStream(process.StandardInput.BaseStream, 4096);
            output = new BufferedStream(process.StandardOutput.BaseStream, 4096);
            cancellation = cancellationToken.Register(Abort);
        }

        public async Task<string> QueryAsync(string script, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            await serial.WaitAsync(cancellationToken);
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
                if (response.Status != 0)
                    throw new IOException($"Selected native operation on {Connection} failed: {response.Detail}");
                return response.Detail;
            }
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
            await serial.WaitAsync(cancellationToken);
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
                    : ReadOutcomesAsync(output, items, outcomes, cancellationToken);
                await CompleteDuplexAsync(writer, reader);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Abort();
                await process.WaitForExitAsync(CancellationToken.None);
                for (int index = 0; index < outcomes.Length; index++)
                    outcomes[index] ??= new(false, new IOException($"Native batch on {Connection} failed for {items[index].Source} -> {items[index].Destination}: {error.Message}", error));
            }
            finally { serial.Release(); }
            return outcomes.Select(outcome => outcome!).ToArray();
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
            disposed = true;
            await serial.WaitAsync();
            try
            {
                // Commands flush before their response; disposal must not turn an
                // already reported failure/cancellation into a second pipe error.
                try { await input.DisposeAsync(); } catch (IOException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
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
        TransferBatchOutcome?[] outcomes, CancellationToken token)
    {
        for (int index = 0; index < items.Count; index++)
        {
            var response = await ReadResponseAsync(stream, index, token);
            outcomes[index] ??= response.Status == 0 ? new(true)
                : new(false, new IOException($"Cannot transfer {items[index].Source} -> {items[index].Destination}: {response.Detail}"));
        }
    }
}
