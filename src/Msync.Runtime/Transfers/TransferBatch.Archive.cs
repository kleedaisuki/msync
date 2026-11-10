using System.Formats.Tar;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static async Task WriteUploadsAsync(Stream stream, IReadOnlyList<TransferBatchItem> items,
        RuntimeContext context, TransferBatchOutcome?[] outcomes, CancellationToken token)
    {
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            await WriteEncodedLineAsync(stream, item.Destination.Raw, token);
            await WriteEncodedLineAsync(stream, BatchMode(item), token);
            await WriteEncodedLineAsync(stream, item.Source is PathValue path ? Basename(path) : "", token);
            await WriteEncodedLineAsync(stream, item.Source is StringValue ? "literal" : "filesystem", token);
            Exception? failure = null;
            await using (TarWriter writer = new(stream, TarEntryFormat.Pax, leaveOpen: true))
            {
                try
                {
                    if (item.Source is PathValue source)
                        await WriteArchiveNodeAsync(writer, source.LocalPath(context.WorkingDirectory, context.HomeDirectory), "payload", token, item.Kind);
                    else
                    {
                        using MemoryStream contents = new(Encoding.UTF8.GetBytes(((StringValue)item.Source).Data), writable: false);
                        try { await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "payload") { DataStream = contents }, token); }
                        catch (IOException error) { throw new ArchiveStreamException(error); }
                    }
                }
                catch (Exception error) when (error is not ArchiveStreamException
                    && error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    failure = error;
                    outcomes[index] = new(false, error);
                    // An untouched TarWriter emits no archive, so a missing source
                    // still needs one entry to delimit this failed item. The source
                    // status trailer prevents publication of its owned scratch.
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, ".msync-source-failed"), token);
                }
            }
            await WriteEncodedLineAsync(stream, failure?.ToString() ?? "", token);
        }
        await stream.FlushAsync(token);
    }

    private static async Task ReadDownloadsAsync(Stream stream, IReadOnlyList<TransferBatchItem> items,
        RuntimeContext context, TransferBatchOutcome?[] outcomes, CancellationToken token)
    {
        byte[] archiveEnd = new byte[512];
        for (int index = 0; index < items.Count; index++)
        {
            var begin = await ReadResponseAsync(stream, index, token);
            if (begin.Status != 0)
            {
                outcomes[index] = new(false, new IOException($"Cannot transfer {items[index].Source} -> {items[index].Destination}: {begin.Detail}"));
                continue;
            }
            Exception? failure = null;
            ArchiveInstallation installation = new(items[index].Destination, context,
                items[index].Mode == TransferDestinationMode.Exact && EndsInSeparator(items[index].Destination),
                items[index].Mode == TransferDestinationMode.Replace
                    || items[index].Mode == TransferDestinationMode.Exact && IsReplacement(items[index].Destination), items[index].Mode);
            using (TarReader reader = new(stream, leaveOpen: true))
            {
                while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
                {
                    RestoreArchiveNames(entry);
                    if (failure is not null) continue;
                    try { await installation.WriteAsync(entry, token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { failure = error; }
                }
            }
            // .NET consumes the first zero record; GNU tar -b1 emits exactly two.
            await stream.ReadExactlyAsync(archiveEnd, token);
            if (archiveEnd.AsSpan().ContainsAnyExcept((byte)0))
                throw new IOException("Native batch archive ended without its second zero record.");
            if (failure is null)
            {
                try { installation.Complete(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { failure = error; }
            }
            var end = await ReadResponseAsync(stream, index, token);
            if (end.Status != 0)
                failure = new IOException($"Cannot read {items[index].Source}: {end.Detail}", failure);
            outcomes[index] = failure is null ? new(true) : new(false, failure);
        }
    }
}
