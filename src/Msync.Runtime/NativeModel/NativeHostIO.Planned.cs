using System.Buffers;
using System.Formats.Tar;
using System.Globalization;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    public static async ValueTask<bool> ExecutePlannedAsync(NativePlannedTransfer transfer, NativeValue source,
        NativeRuntimeContext context, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"{source} -> {transfer.Destination}");
            return true;
        }
        if (transfer.Kind is PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata)
        {
            await ExecuteDirectoryActionAsync(transfer, source, context, token);
            return true;
        }
        if (transfer.Kind == PlannedTransferKind.GenericCopy && transfer.Mode == TransferDestinationMode.Exact)
            return await TransferAsync(source, transfer.Destination, context, token);
        if (source.Kind == NativeKind.Range && transfer.Kind == PlannedTransferKind.GenericCopy)
        {
            NativePathValue target = transfer.Mode == TransferDestinationMode.Replace
                ? transfer.Destination.WithRaw(transfer.Destination.Raw.TrimEnd('/') + "/.") : transfer.Destination;
            return await TransferAsync(source, target, context, token);
        }
        if (source.Kind is not (NativeKind.Path or NativeKind.String))
            throw new MsyncException($"Recorded {transfer.Kind} requires Path or String, received {source.Type}.", transfer.Span);
        if (transfer.Kind == PlannedTransferKind.WriteText && source.Kind != NativeKind.String)
            throw new MsyncException("Recorded WriteText requires a String source.", transfer.Span);
        if (transfer.Kind is PlannedTransferKind.CopyFile or PlannedTransferKind.CopyDirectory or PlannedTransferKind.CopyLink
            && source.Kind != NativeKind.Path)
            throw new MsyncException($"Recorded {transfer.Kind} requires a Path source.", transfer.Span);

        bool shorthand = transfer.Mode == TransferDestinationMode.Exact;
        bool container = shorthand && EndsInSeparator(transfer.Destination);
        bool replace = transfer.Mode == TransferDestinationMode.Replace || shorthand && IsReplacement(transfer.Destination);
        NativeConnection? sourceConnection = source.Kind == NativeKind.Path ? Remote(source.AsPath()) : null;
        NativeConnection? targetConnection = Remote(transfer.Destination);
        if (sourceConnection is null && targetConnection is null)
        {
            await Task.Run(() => ExecuteLocalPlanned(transfer, source, context, container, replace, token), token);
            return true;
        }
        NativePathValue destination = targetConnection is null
            ? new(shorthand ? TargetPath(transfer.Destination, source, context, container) : context.LocalPath(transfer.Destination))
            : RemoteFinalTarget(transfer.Destination, source, shorthand, container, replace);
        string mode = replace && transfer.Kind != PlannedTransferKind.WriteText ? "replace-final"
            : transfer.Mode == TransferDestinationMode.Merge ? "merge" : "exact";
        if (targetConnection is null)
            await DownloadAsync([source.AsPath()], destination, context, false, replace, token, transfer.Kind,
                replace ? TransferDestinationMode.Replace : transfer.Mode);
        else if (sourceConnection == targetConnection)
            await CopyRemoteAsync([source.AsPath()], destination, context, false, replace, false, token, transfer.Kind, mode);
        else if (sourceConnection is null)
            await UploadAsync([source], destination, context, false, replace, false, token, transfer.Kind, mode);
        else
            await RelayAsync([source], destination, context, false, replace, false, token, transfer.Kind, mode);
        return true;
    }

    private static void ExecuteLocalPlanned(NativePlannedTransfer transfer, NativeValue source,
        NativeRuntimeContext context, bool container, bool replace, CancellationToken token)
    {
        string destination = transfer.Mode == TransferDestinationMode.Exact
            ? TargetPath(transfer.Destination, source, context, container) : context.LocalPath(transfer.Destination);
        byte[]? rented = source.Kind == NativeKind.String || !OperatingSystem.IsWindows()
            ? ArrayPool<byte>.Shared.Rent(context.BufferSize) : null;
        try
        {
            Span<byte> buffer = rented is null ? [] : rented.AsSpan(0, context.BufferSize);
            if (source.Kind == NativeKind.String)
            {
                TransferFiles.WriteLiteral(source.AsString(), destination, buffer, token, context.Timeline);
                return;
            }
            string input = context.LocalPath(source.AsPath());
            if (TransferOverlap.SameNode(input, destination)) return;
            bool directory = transfer.Kind == PlannedTransferKind.CopyDirectory
                || transfer.Kind == PlannedTransferKind.GenericCopy && Directory.Exists(input);
            if (replace && !directory) throw new IOException($"Tree replacement requires a directory source: '{input}'.");
            if (!container && !replace && transfer.Mode != TransferDestinationMode.Merge && Directory.Exists(destination)
                && !NativeFileMetadata.IsLink(destination, File.GetAttributes(destination)))
                throw new IOException($"Exact destination '{destination}' is an existing directory; append '/' for container insertion or '/.' for replacement.");
            if (directory && TransferOverlap.NeedsSnapshot(input, destination, replace))
            {
                TransferOverlap.CopyAsync(input, destination, replace, token, context.BufferSize, context.Timeline).GetAwaiter().GetResult();
                return;
            }
            if (replace) TransferFiles.RemoveNode(destination);
            TransferFiles.CopyPreparedNode(input, destination, transfer.Kind, buffer, token, context.Timeline);
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }

    private static NativePathValue RemoteFinalTarget(NativePathValue target, NativeValue source, bool shorthand, bool container, bool replace)
    {
        if (!shorthand) return target;
        if (replace)
        {
            string raw = target.Raw[..^2];
            return target.WithRaw(raw.Length == 0 ? "/" : raw);
        }
        return container && source.Kind == NativeKind.Path
            ? target.WithRaw(target.Raw.TrimEnd('/') + "/" + Basename(source.AsPath())) : target;
    }

    private static async Task ExecuteDirectoryActionAsync(NativePlannedTransfer transfer, NativeValue source,
        NativeRuntimeContext context, CancellationToken token)
    {
        NativePathValue? path = source.Kind == NativeKind.Path ? source.AsPath() : null;
        if (path is null && (source.Kind != NativeKind.Null || transfer.Kind != PlannedTransferKind.EnsureDirectory))
            throw new IOException("Directory metadata requires a Path source.");
        NativeConnection? sourceConnection = path is { } input ? Remote(input) : null;
        NativeConnection? targetConnection = Remote(transfer.Destination);
        if (targetConnection is { } same && sourceConnection == same)
        {
            await RunCommandAsync(same, SameEndpointDirectoryProgram,
                [path!.Value.Raw, transfer.Destination.Raw, transfer.Kind.ToString(), transfer.Mode.ToString()], context, token);
            return;
        }
        FileMetadata metadata = path is null ? default
            : sourceConnection is { } remote ? await ReadDirectoryMetadataAsync(remote, path.Value, context, token)
            : DirectoryMetadata(context.LocalPath(path.Value));
        if (targetConnection is { } target)
        {
            await RunCommandAsync(target, DirectoryActionProgram,
                [transfer.Destination.Raw, transfer.Kind.ToString(), transfer.Mode.ToString(),
                    Convert.ToString((int)metadata.Mode, 8), metadata.Modified.Pax], context, token);
            return;
        }
        string destination = context.LocalPath(transfer.Destination);
        if (transfer.Kind == PlannedTransferKind.ApplyDirectoryMetadata) { metadata.Apply(destination); return; }
        if (transfer.Mode == TransferDestinationMode.Replace) TransferFiles.RemoveNode(destination);
        else if (transfer.Mode == TransferDestinationMode.Exact && Directory.Exists(destination)
            && !NativeFileMetadata.IsLink(destination, File.GetAttributes(destination)))
            throw new IOException($"Exact destination '{destination}' is an existing directory; append '/' for container insertion or '/.' for replacement.");
        Directory.CreateDirectory(destination);
    }

    private static FileMetadata DirectoryMetadata(string source)
    {
        FileMetadata metadata = NativeFileMetadata.Capture(source);
        if (!metadata.IsDirectory || metadata.IsLink) throw new IOException($"Directory operation requires a directory source: '{source}'.");
        return OperatingSystem.IsWindows() ? metadata with { Mode = new GnuTarEntry(TarEntryType.Directory, "payload").Mode } : metadata;
    }

    private static async Task<FileMetadata> ReadDirectoryMetadataAsync(NativeConnection connection, NativePathValue path,
        NativeRuntimeContext context, CancellationToken token)
    {
        string data = await RunCommandAsync(connection, DirectoryAttributesProgram, [path.Raw], context, token);
        string[] fields = data.Split('\0');
        string fractional = fields[2][(fields[2].IndexOf('.') + 1)..].Split(' ')[0];
        return new(FileAttributes.Directory, 0, (UnixFileMode)Convert.ToInt32(fields[0], 8),
            new(long.Parse(fields[1], CultureInfo.InvariantCulture), long.Parse(fractional, CultureInfo.InvariantCulture)), false);
    }

    private const string DirectoryExpand = """
        set -eu
        expand() { case "$1" in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#??}";; /*) printf '%s' "$1";; *) printf '%s/%s' "$PWD" "$1";; esac; }
        """;
    private static string DirectoryAttributesProgram => DirectoryExpand + "\n" + """
        source=$(expand "$1"; printf .); source=${source%.}
        if [ ! -d "$source" ] || [ -L "$source" ]; then printf 'Directory operation requires a directory source: %s\n' "$source" >&2; exit 1; fi
        stat --printf='%a\0%Y\0%y' -- "$source"
        """;
    private static string DirectoryActionProgram => DirectoryExpand + "\n" + """
        target=$(expand "$1"; printf .); target=${target%.}
        if [ "$2" = EnsureDirectory ]; then
            if [ "$3" = Replace ]; then rm -rf -- "$target"
            elif [ "$3" = Exact ] && [ -d "$target" ] && [ ! -L "$target" ]; then printf 'Exact destination is an existing directory: %s; append / or /.\n' "$target" >&2; exit 1; fi
            mkdir -p -- "$target"
        else
            chmod "$4" -- "$target"
            touch -d "@$5" -- "$target"
        fi
        """;
    private static string SameEndpointDirectoryProgram => DirectoryExpand + "\n" + """
        source=$(expand "$1"; printf .); source=${source%.}
        target=$(expand "$2"; printf .); target=${target%.}
        if [ ! -d "$source" ] || [ -L "$source" ]; then printf 'Directory operation requires a directory source: %s\n' "$source" >&2; exit 1; fi
        if [ "$3" = EnsureDirectory ]; then
            if [ "$4" = Replace ]; then rm -rf -- "$target"
            elif [ "$4" = Exact ] && [ -d "$target" ] && [ ! -L "$target" ]; then printf 'Exact destination is an existing directory: %s; append / or /.\n' "$target" >&2; exit 1; fi
            mkdir -p -- "$target"
        else
            chmod --reference="$source" -- "$target"
            touch --reference="$source" -- "$target"
        fi
        """;
}
