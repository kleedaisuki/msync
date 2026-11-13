using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal sealed record NativeTransferItem(NativeValue Source, NativeValue Destination,
    PlannedTransferKind Kind, TransferDestinationMode Mode = TransferDestinationMode.Exact);

internal sealed record NativeTransferOutcome(bool Success, Exception? Error = null);

internal static class NativeTransfers
{
    internal static async Task<IReadOnlyList<NativeTransferOutcome>> CopyBatchAsync(
        IEnumerable<NativeTransferItem> items, NativeRuntimeContext context, CancellationToken token = default)
    {
        List<NativeTransferOutcome> outcomes = [];
        foreach (NativeTransferItem item in items)
        {
            NativePlanValue input = item.Source.Kind switch
            {
                NativeKind.Path => NativePlanValue.FromPath(item.Source.AsPath()),
                NativeKind.String => NativePlanValue.Scalar(new(NativeKind.String, Text: item.Source.AsString())),
                NativeKind.Null => NativePlanValue.Scalar(new(NativeKind.Null)),
                _ => throw new InvalidOperationException("The prepared transfer fixture requires a portable input.")
            };
            NativePlannedTransfer transfer = new(outcomes.Count, input, item.Destination.AsPath(), item.Kind,
                [], [], [], Mode: item.Mode);
            try { outcomes.Add(new(await NativeHostIO.ExecutePlannedAsync(transfer, item.Source, context, token))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or MsyncException)
            { outcomes.Add(new(false, error)); }
        }
        return outcomes;
    }

    internal static ValueTask<NativeValue> FileSystemAsync(string name, NativeValue path, NativeRuntimeContext context) =>
        NativeBuiltins.InvokeAsync("fs." + name, [path], context,
            static (_, _, _) => throw new InvalidOperationException("Filesystem fixture has no source callback."));

    internal static async Task<IReadOnlyList<NativeValue>> DirectoryAsync(NativeValue path,
        NativeRuntimeContext context, bool recursive = false)
    {
        NativeValue range = await FileSystemAsync(recursive ? "recursive_directory_iterator" : "directory_iterator", path, context);
        List<NativeValue> entries = [];
        await foreach (NativeValue entry in range.AsRange().Items) entries.Add(entry);
        return entries;
    }
}
