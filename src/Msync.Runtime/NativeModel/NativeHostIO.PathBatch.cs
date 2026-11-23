using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    internal static async ValueTask TransferPathBatchAsync(NativePathValue[] sources, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"Range<Path> -> {destination}");
            return;
        }
        if (sources.Length == 0) return;
        ValidatePathNames(sources);

        bool replace = IsReplacement(destination);
        var remote = sources.Where(path => Remote(path).HasValue).ToArray();
        if (Remote(destination) is { } target)
        {
            if (remote.Length == sources.Length && remote.All(path => Remote(path) == target))
                await CopyRemoteAsync(remote, destination, context, true, replace, true, token);
            else if (remote.Length == 0)
                await UploadPayloadAsync(sources, null, destination, context, true, replace, true, token);
            else
                await RelayPathsAsync(sources, destination, context, true, replace, true, token);
            return;
        }
        if (remote.Length == 0)
        {
            if (replace) await ReplaceLocalCohortAsync(sources, destination, context, token);
            else
                foreach (var source in sources)
                    await CopyLocalPathAsync(source, PhysicalTargetPath(destination, source, context, true),
                        context, true, false, token);
            return;
        }
        if (remote.Length == sources.Length && remote.All(path => path.Connection == remote[0].Connection))
            await DownloadAsync(remote, destination, context, true, replace, token);
        else
            await CopyMixedLocalCohortAsync(sources, destination, context, replace, token);
    }

    private static void ValidatePathNames(IReadOnlyList<NativePathValue> sources)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            string name = Basename(source);
            if (!names.Add(name)) throw new MsyncException($"Selected sources collide at destination name '{name}'.");
        }
    }
}
