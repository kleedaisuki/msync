using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using NativePathValue = Msync.Core.Native.NativePath;

internal sealed record NativeTransferItem(NativePathValue? Source, NativePathValue Destination,
    PlannedTransferKind Kind, TransferDestinationMode Mode = TransferDestinationMode.Exact, string? Text = null);

internal sealed record NativeTransferOutcome(bool Success, Exception? Error = null);

internal static class NativeTransfers
{
    internal static async Task<IReadOnlyList<NativeTransferOutcome>> CopyBatchAsync(
        IEnumerable<NativeTransferItem> items, NativeRuntimeContext context, CancellationToken token = default)
    {
        List<NativeTransferOutcome> outcomes = [];
        foreach (NativeTransferItem item in items)
        {
            NativeHostIO.RecordedTransfer transfer = new(item.Destination, item.Kind, item.Mode);
            try { outcomes.Add(new(await NativeHostIO.ExecuteRecordedAsync(transfer, item.Source, item.Text, context, token))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException)
            { outcomes.Add(new(false, error)); }
        }
        return outcomes;
    }

    internal static async Task<IReadOnlyList<NativePathValue>> DirectoryAsync(NativePathValue path,
        NativeRuntimeContext context, bool recursive = false)
    {
        List<NativePathValue> entries = [];
        await foreach (var entry in NativeBuiltins.OpenDirectory(path, recursive, context, default)) entries.Add(entry);
        return entries;
    }
}
