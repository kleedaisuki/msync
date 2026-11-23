using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    internal static async ValueTask<double> ReadFileSize(NativePathValue path, NativeRuntimeContext context, CancellationToken token)
    {
        if (path.Connection is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection)
            return new FileInfo(context.LocalPath(path)).Length;
        string size = await NativeHostIO.RunCommandAsync(connection, IOPrelude + "\nstat -L -c %s -- \"$p\"", [path.Raw], context, token).ConfigureAwait(false);
        return double.Parse(size, CultureInfo.InvariantCulture);
    }

    internal static async ValueTask<byte> ReadFilePredicate(HpirFileMetadata operation, NativePathValue path,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (path.Connection is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection)
            return LocalFilePredicate(operation, context.LocalPath(path)) ? (byte)1 : (byte)0;
        string name = HpirFileMetadataImports.Name(operation);
        string result = await NativeHostIO.RunCommandAsync(connection, RemotePrelude + "\n" + RemoteScript(name), [path.Raw], context, token).ConfigureAwait(false);
        return result == "true" ? (byte)1 : (byte)0;
    }

    internal static bool LocalFilePredicate(HpirFileMetadata operation, string path)
    {
        if (operation == HpirFileMetadata.IsReadonly)
            return OperatingSystem.IsWindows() ? (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0
                : (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        string kind = Status(path, operation == HpirFileMetadata.IsSymlink);
        return operation switch
        {
            HpirFileMetadata.Exists => kind != "not_found",
            HpirFileMetadata.IsRegularFile => kind == "regular",
            HpirFileMetadata.IsDirectory => kind == "directory",
            HpirFileMetadata.IsSymlink => kind == "symlink",
            HpirFileMetadata.IsBlockFile => kind == "block",
            HpirFileMetadata.IsCharacterFile => kind == "character",
            HpirFileMetadata.IsFifo => kind == "fifo",
            HpirFileMetadata.IsSocket => kind == "socket",
            HpirFileMetadata.IsOther => kind is not ("regular" or "directory" or "symlink" or "not_found"),
            HpirFileMetadata.IsEmpty => kind switch
            {
                "directory" => !Directory.EnumerateFileSystemEntries(path).Any(),
                "regular" => new FileInfo(path).Length == 0,
                _ => throw new IOException($"fs.is_empty requires an existing regular file or directory: '{path}' ({kind})")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}
