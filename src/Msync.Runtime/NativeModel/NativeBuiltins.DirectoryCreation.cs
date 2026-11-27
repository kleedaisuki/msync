using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    internal static async ValueTask<bool> CreateDirectories(NativePathValue path, NativeRuntimeContext context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun) return true;
        if (path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } endpoint)
        {
            string result = await NativeHostIO.RunCommandAsync(endpoint, RemotePrelude + "\n" + RemoteScript("create_directories"),
                [path.Raw], context, token).ConfigureAwait(false);
            return result == "true";
        }

        string local = context.LocalPath(path);
        if (Directory.Exists(local)) return false;
        Directory.CreateDirectory(local);
        return true;
    }
}
