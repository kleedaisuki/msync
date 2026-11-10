using System.Buffers;
using Msync.Core;

namespace Msync.Runtime;

public sealed record TransferBatchItem(Value Source, PathValue Destination,
    PlannedTransferKind Kind = PlannedTransferKind.GenericCopy,
    TransferDestinationMode Mode = TransferDestinationMode.Exact);

public sealed record TransferBatchOutcome(bool Success, Exception? Error = null, bool Executed = true);

public static partial class TransferEngine
{
    private enum BatchDirection { Local, Upload, Download, RemoteCopy, Relay, DirectoryActions, Other }
    private readonly record struct BatchRoute(BatchDirection Direction, ConnectionValue? Connection,
        ConnectionValue? Target = null);

    // The caller supplies an independent graph frontier and owns its global I/O permit.
    // No additional worker pool or per-batch concurrency multiplier is created here.
    public static async Task<IReadOnlyList<TransferBatchOutcome>> CopyBatchAsync(
        IReadOnlyList<TransferBatchItem> items, RuntimeContext context, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) return [];
        var outcomes = new TransferBatchOutcome[items.Count];
        Dictionary<BatchRoute, List<int>> groups = [];
        for (int index = 0; index < items.Count; index++)
        {
            var route = Route(items[index]);
            if (!groups.TryGetValue(route, out var group)) groups.Add(route, group = []);
            group.Add(index);
        }
        foreach (var (route, indices) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.DryRun || route.Direction == BatchDirection.Other)
            {
                foreach (int index in indices)
                    outcomes[index] = await CopyBatchItemAsync(items[index], context, cancellationToken);
                continue;
            }
            var selected = indices.Select(index => items[index]).ToArray();
            var results = route.Direction == BatchDirection.Local
                ? await CopyLocalBatchAsync(selected, context, cancellationToken)
                : route.Direction == BatchDirection.DirectoryActions
                    ? await CopyDirectoryActionsAsync(selected, context, cancellationToken)
                : route.Direction == BatchDirection.Relay
                    ? await CopyRelayBatchAsync(selected, route.Connection!, route.Target!, context.BufferSize, cancellationToken)
                : await CopyRemoteBatchAsync(selected, route.Direction, route.Connection!, context, cancellationToken);
            for (int index = 0; index < indices.Count; index++) outcomes[indices[index]] = results[index];
        }
        return outcomes;
    }

    private static BatchRoute Route(TransferBatchItem item)
    {
        ConnectionValue? source = item.Source is PathValue path ? Remote(path) : null;
        ConnectionValue? target = Remote(item.Destination);
        if (item.Kind is PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata)
            return new(source is null && target is null ? BatchDirection.Local : BatchDirection.DirectoryActions, source ?? target);
        if (item.Source is not (PathValue or StringValue)) return new(BatchDirection.Other, null);
        if (source is null && target is null) return new(BatchDirection.Local, null);
        if (source is null) return new(BatchDirection.Upload, target);
        if (target is null) return new(BatchDirection.Download, source);
        return source == target ? new(BatchDirection.RemoteCopy, source) : new(BatchDirection.Relay, source, target);
    }

    private static async Task<TransferBatchOutcome> CopyBatchItemAsync(TransferBatchItem item,
        RuntimeContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.DryRun || item.Kind == PlannedTransferKind.GenericCopy || Route(item).Direction != BatchDirection.Local)
                return new(await CopyAsync(item.Source, item.Destination, context, cancellationToken));
            return (await CopyLocalBatchAsync([item], context, cancellationToken))[0];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(false, error);
        }
    }

    private static Task<IReadOnlyList<TransferBatchOutcome>> CopyLocalBatchAsync(IReadOnlyList<TransferBatchItem> items,
        RuntimeContext context, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<TransferBatchOutcome>>(() =>
    {
        var outcomes = new TransferBatchOutcome[items.Count];
        bool needsBuffer = items.Any(item => item.Source is StringValue || !OperatingSystem.IsWindows()
            && item.Kind is PlannedTransferKind.GenericCopy or PlannedTransferKind.CopyFile or PlannedTransferKind.CopyDirectory);
        byte[]? rented = needsBuffer ? ArrayPool<byte>.Shared.Rent(context.BufferSize) : null;
        try
        {
            Span<byte> buffer = rented is null ? [] : rented.AsSpan(0, context.BufferSize);
            for (int index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    CopyLocalBatchItem(items[index], context, buffer, cancellationToken);
                    outcomes[index] = new(true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { outcomes[index] = new(false, error); }
            }
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
        return outcomes;
    }, cancellationToken);

    private static void CopyLocalBatchItem(TransferBatchItem item, RuntimeContext context, Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (item.Kind is PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata)
        {
            FileMetadata metadata = item.Source is PathValue directorySource
                ? DirectoryMetadata(directorySource.LocalPath(context.WorkingDirectory, context.HomeDirectory))
                : item.Kind == PlannedTransferKind.EnsureDirectory && item.Source is NullValue
                    ? default : throw new IOException("Directory metadata requires a Path source.");
            ApplyDirectoryAction(item, metadata, context);
            return;
        }
        bool shorthand = item.Mode == TransferDestinationMode.Exact;
        bool container = shorthand && EndsInSeparator(item.Destination);
        bool replace = item.Mode == TransferDestinationMode.Replace || shorthand && IsReplacement(item.Destination);
        string destination = shorthand ? TargetPath(item.Destination, item.Source, context, container)
            : item.Destination.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        if (item.Source is StringValue text)
        {
            TransferFiles.WriteLiteral(text.Data, destination, buffer, cancellationToken);
            return;
        }
        string source = ((PathValue)item.Source).LocalPath(context.WorkingDirectory, context.HomeDirectory);
        if (TransferOverlap.SameNode(source, destination)) return;
        bool directory = item.Kind == PlannedTransferKind.CopyDirectory
            || item.Kind == PlannedTransferKind.GenericCopy && Directory.Exists(source);
        if (replace && !directory) throw new IOException($"Tree replacement requires a directory source: '{source}'.");
        if (!container && !replace && item.Mode != TransferDestinationMode.Merge && Directory.Exists(destination)
            && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0)
            throw new IOException($"Exact destination '{destination}' is an existing directory; append '/' for container insertion or '/.' for replacement.");
        if (directory && TransferOverlap.NeedsSnapshot(source, destination, replace))
        {
            TransferOverlap.CopyAsync(source, destination, replace, cancellationToken, context.BufferSize).GetAwaiter().GetResult();
            return;
        }
        if (replace) TransferFiles.RemoveNode(destination);
        TransferFiles.CopyPreparedNode(source, destination, item.Kind, buffer, cancellationToken);
    }

    private static string BatchMode(TransferBatchItem item) => item.Mode switch
    {
        TransferDestinationMode.Merge => "merge",
        TransferDestinationMode.Replace => "replace-final",
        _ => Mode(EndsInSeparator(item.Destination), IsReplacement(item.Destination))
    };
}
