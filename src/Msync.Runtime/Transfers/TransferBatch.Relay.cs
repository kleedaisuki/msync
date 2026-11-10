using System.Buffers;
using System.Formats.Tar;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static async Task<IReadOnlyList<TransferBatchOutcome>> CopyRelayBatchAsync(
        IReadOnlyList<TransferBatchItem> items, ConnectionValue source, ConnectionValue target, int bufferSize, CancellationToken token)
    {
        RemoteTransferSession? ownedSource = null;
        RemoteTransferSession? ownedTarget = null;
        try
        {
            var producer = CurrentSession(source) ?? (ownedSource = OpenSession(source, token));
            var consumer = CurrentSession(target) ?? (ownedTarget = OpenSession(target, token));
            return await producer.RelayBatchAsync(consumer, items, bufferSize, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return items.Select(item => new TransferBatchOutcome(false,
                new IOException($"Cannot relay {item.Source} -> {item.Destination}: {error.Message}", error))).ToArray();
        }
        finally
        {
            if (ownedTarget is not null) await ownedTarget.DisposeAsync();
            if (ownedSource is not null) await ownedSource.DisposeAsync();
        }
    }

    public sealed partial class RemoteTransferSession
    {
        internal async Task<IReadOnlyList<TransferBatchOutcome>> RelayBatchAsync(RemoteTransferSession target,
            IReadOnlyList<TransferBatchItem> items, int bufferSize, CancellationToken token)
        {
            int order = ((int)Connection.Kind).CompareTo((int)target.Connection.Kind);
            if (order == 0) order = string.CompareOrdinal(Connection.Address, target.Connection.Address);
            var first = order < 0 ? this : target;
            var second = order < 0 ? target : this;
            await first.serial.WaitAsync(token);
            try
            {
                await second.serial.WaitAsync(token);
                try { return await RelayOwnedBatchAsync(target, items, bufferSize, token); }
                finally { second.serial.Release(); }
            }
            finally { first.serial.Release(); }
        }

        private async Task<IReadOnlyList<TransferBatchOutcome>> RelayOwnedBatchAsync(RemoteTransferSession target,
            IReadOnlyList<TransferBatchItem> items, int bufferSize, CancellationToken token)
        {
            using var registration = token.Register(() => { Abort(); target.Abort(); });
            var outcomes = new TransferBatchOutcome?[items.Count];
            Task[] work = [];
            try
            {
                await WriteCommandAsync(input, "DOWNLOAD", items.Count, token);
                await WriteCommandAsync(target.input, "UPLOAD", items.Count, token);
                work = [WritePathsAsync(input, items, BatchDirection.Download, token),
                    ForwardRelayAsync(output, target.input, items, outcomes, bufferSize, token),
                    ReadOutcomesAsync(target.output, items, outcomes, token)];
                List<Task> remaining = [.. work];
                while (remaining.Count != 0)
                {
                    Task completed = await Task.WhenAny(remaining);
                    await completed;
                    remaining.Remove(completed);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException
                or UnauthorizedAccessException or InvalidOperationException or FormatException
                or OverflowException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                Abort(); target.Abort();
                await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), target.process.WaitForExitAsync(CancellationToken.None));
                try { await Task.WhenAll(work); } catch { }
                token.ThrowIfCancellationRequested();
                for (int index = 0; index < items.Count; index++)
                    outcomes[index] ??= new(false, new IOException($"Cannot relay {items[index].Source} -> {items[index].Destination}: {error.Message}", error));
            }
            return outcomes.Select(outcome => outcome!).ToArray();
        }
    }

    private static async Task ForwardRelayAsync(Stream source, Stream target, IReadOnlyList<TransferBatchItem> items,
        TransferBatchOutcome?[] outcomes, int bufferSize, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try { await ForwardRelayItemsAsync(source, target, items, outcomes, buffer.AsMemory(0, bufferSize), token); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task ForwardRelayItemsAsync(Stream source, Stream target, IReadOnlyList<TransferBatchItem> items,
        TransferBatchOutcome?[] outcomes, Memory<byte> buffer, CancellationToken token)
    {
        byte[] archiveEnd = new byte[512];
        for (int index = 0; index < items.Count; index++)
        {
            var begin = await ReadResponseAsync(source, index, token);
            await WriteEncodedLineAsync(target, items[index].Destination.Raw, token);
            await WriteEncodedLineAsync(target, BatchMode(items[index]), token);
            await WriteEncodedLineAsync(target, Basename((PathValue)items[index].Source), token);
            await WriteEncodedLineAsync(target, "relay", token);
            string failure = begin.Detail;
            if (begin.Status == 0)
            {
                using var forwarding = new ArchiveForwardStream(source, target);
                using (TarReader reader = new(forwarding, leaveOpen: true))
                    while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
                        if (entry.DataStream is { } contents)
                            while (await contents.ReadAsync(buffer, token) != 0) { }
                await source.ReadExactlyAsync(archiveEnd, token);
                if (archiveEnd.AsSpan().ContainsAnyExcept((byte)0))
                    throw new IOException("Native relay archive ended without its second zero record.");
                await target.WriteAsync(archiveEnd, token);
                var end = await ReadResponseAsync(source, index, token);
                failure = end.Status == 0 ? "" : end.Detail;
            }
            else
            {
                await using TarWriter writer = new(target, TarEntryFormat.Pax, leaveOpen: true);
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, ".msync-source-failed"), token);
            }
            if (failure.Length != 0)
                outcomes[index] = new(false, new IOException($"Cannot read relay source {items[index].Source}: {failure}"));
            await WriteEncodedLineAsync(target, failure, token);
        }
        await target.FlushAsync(token);
    }
}
