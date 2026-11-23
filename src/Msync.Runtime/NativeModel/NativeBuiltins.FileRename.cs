using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

internal readonly record struct NativeFileRename(NativeConnection? Endpoint, string Source, string Destination);

public static partial class NativeBuiltins
{
    internal static NativeFileRename SelectFileRename(NativePathValue source, NativePathValue destination,
        NativeRuntimeContext context)
    {
        // Rename dispatches on the source endpoint; the local kernel has
        // always interpreted the destination's spelling locally.
        if (source.Connection is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } endpoint)
            return new(null, context.LocalPath(source), context.LocalPath(destination));
        if (!context.DryRun && destination.Connection is { } other && other != endpoint)
            throw new MsyncException($"fs.rename requires paths on the same connection, received '{endpoint}' and '{other}'");
        return new(endpoint, source.Raw, destination.Raw);
    }

    internal static ValueTask RenameFile(NativeFileRename rename, NativeRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun) return ValueTask.CompletedTask;
        if (rename.Endpoint is { } endpoint) return RenameRemoteFile(endpoint, rename, context, token);

        if (Directory.Exists(rename.Source) && !NativeFileMetadata.IsLink(rename.Source, File.GetAttributes(rename.Source)))
            Directory.Move(rename.Source, rename.Destination);
        else File.Move(rename.Source, rename.Destination, true);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask RenameRemoteFile(NativeConnection endpoint, NativeFileRename rename,
        NativeRuntimeContext context, CancellationToken token)
    {
        await NativeHostIO.RunCommandAsync(endpoint, RemotePrelude + "\n" + RemoteScript("rename"),
            [rename.Source, rename.Destination], context, token).ConfigureAwait(false);
    }
}
