using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;
using LexicalPath = Msync.Runtime.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    internal static async IAsyncEnumerable<NativePathValue> OpenSelectedPaths(NativePathValue directory, string pattern,
        NativeRuntimeContext context, [EnumeratorCancellation] CancellationToken token = default)
    {
        Regex expression = new(pattern, RegexOptions.CultureInvariant);
        await foreach (var item in NativeBuiltins.OpenDirectory(directory, recursive: false, context, token).ConfigureAwait(false))
            if (expression.IsMatch(Basename(item))) yield return item;
    }

    private static async Task CopyMixedLocalCohortAsync(IReadOnlyList<NativePathValue> values, NativePathValue destination,
        NativeRuntimeContext context, bool replace, CancellationToken token)
    {
        string target = PhysicalTargetPath(destination, values[0], context, false);
        List<(NativePathValue Path, string Source)> local = [];
        Dictionary<NativeConnection, List<NativePathValue>> remote = [];
        foreach (NativePathValue path in values)
        {
            if (Remote(path) is { } connection)
            {
                if (!remote.TryGetValue(connection, out var group)) remote.Add(connection, group = []);
                group.Add(path);
            }
            else local.Add((path, context.LocalPath(path)));
        }
        string? stage = null;
        List<(IReadOnlyList<TransferOverlap.Node> Nodes, string Path)> snapshots = [];
        try
        {
            if (replace)
            {
                for (int index = 0; index < local.Count; index++)
                {
                    var item = local[index];
                    if (!TransferOverlap.Related(item.Source, target)) continue;
                    var nodes = TransferOverlap.CaptureTree(item.Source, token);
                    if (stage is null)
                    {
                        stage = Path.Combine(Path.GetDirectoryName(target) ?? ".", $".msync-{Guid.NewGuid():N}");
                        Directory.CreateDirectory(stage);
                    }
                    string snapshot = Path.Combine(stage, Basename(item.Path));
                    TransferOverlap.Materialize(nodes, snapshot, token, context.BufferSize);
                    snapshots.Add((nodes, snapshot));
                    local[index] = (item.Path, snapshot);
                }
                token.ThrowIfCancellationRequested();
                TransferFiles.RemoveNode(target);
            }
            Directory.CreateDirectory(target);
            foreach (var item in local)
                await TransferFiles.CopyNodeAsync(item.Source, Path.Combine(target, Basename(item.Path)), token, context.BufferSize, context.Timeline);
            NativePathValue directory = new(target, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
            foreach (var group in remote.Values)
                await DownloadAsync(group, directory, context, true, false, token);
        }
        finally
        {
            if (stage is not null)
            {
                foreach (var snapshot in snapshots) TransferOverlap.MakeSnapshotWritable(snapshot.Nodes, snapshot.Path);
                Directory.Delete(stage, true);
            }
        }
    }

    private static async Task CopyLocalPathAsync(NativePathValue source, string destination, NativeRuntimeContext context,
        bool container, bool replace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string input = context.LocalPath(source);
        if (TransferOverlap.SameNode(input, destination)) return;
        bool directory = Directory.Exists(input) && !NativeFileMetadata.IsLink(input, File.GetAttributes(input));
        if (replace && !directory) throw new IOException($"Tree replacement requires a directory source: '{input}'.");
        if (!container && !replace && Directory.Exists(destination)
            && !NativeFileMetadata.IsLink(destination, File.GetAttributes(destination)))
            throw new IOException($"Exact destination '{destination}' is an existing directory; append '/' for container insertion or '/.' for replacement.");
        if (TransferOverlap.NeedsSnapshot(input, destination, replace))
        {
            await TransferOverlap.CopyAsync(input, destination, replace, token, context.BufferSize, context.Timeline);
            return;
        }
        if (replace) TransferFiles.RemoveNode(destination);
        await TransferFiles.CopyNodeAsync(input, destination, token, context.BufferSize, context.Timeline);
    }

    private static async Task ReplaceLocalCohortAsync(IReadOnlyList<NativePathValue> paths, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (paths.Count == 0) return;
        string target = PhysicalTargetPath(destination, paths[0], context, false);
        string[] sources = paths.Select(context.LocalPath).ToArray();
        IReadOnlyList<TransferOverlap.Node>?[] snapshots = new IReadOnlyList<TransferOverlap.Node>?[paths.Count];
        bool related = false;
        for (int index = 0; index < paths.Count; index++)
        {
            if (!TransferOverlap.Related(sources[index], target)) continue;
            snapshots[index] = TransferOverlap.CaptureTree(sources[index], token);
            related = true;
        }
        string? stage = null;
        try
        {
            if (related)
            {
                stage = Path.Combine(Path.GetDirectoryName(target) ?? ".", $".msync-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stage);
                for (int index = 0; index < paths.Count; index++)
                    if (snapshots[index] is { } nodes)
                        TransferOverlap.Materialize(nodes, Path.Combine(stage, Basename(paths[index])), token, context.BufferSize);
            }
            token.ThrowIfCancellationRequested();
            TransferFiles.RemoveNode(target);
            Directory.CreateDirectory(target);
            for (int index = 0; index < paths.Count; index++)
            {
                string name = Basename(paths[index]);
                string input = snapshots[index] is not null ? Path.Combine(stage!, name) : sources[index];
                await TransferFiles.CopyNodeAsync(input, Path.Combine(target, name), token, context.BufferSize, context.Timeline);
            }
        }
        finally
        {
            if (stage is not null)
            {
                for (int index = 0; index < paths.Count; index++)
                    if (snapshots[index] is { } nodes)
                        TransferOverlap.MakeSnapshotWritable(nodes, Path.Combine(stage, Basename(paths[index])));
                Directory.Delete(stage, true);
            }
        }
    }

    private static string PhysicalTargetPath(NativePathValue target, NativePathValue? source, NativeRuntimeContext context, bool container)
    {
        string path = context.LocalPath(target);
        if (IsReplacement(target)) path = Path.GetDirectoryName(path)!;
        return container && source is { } input ? Path.Combine(path, Basename(input)) : path;
    }

    private static bool Windows(NativePathValue path) => OperatingSystem.IsWindows()
        && path.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl);
    private static string Basename(NativePathValue path) => new LexicalPath(path.Raw, Windows(path)).Filename;
    private static bool EndsInSeparator(NativePathValue path) => path.Raw.EndsWith('/') || Windows(path) && path.Raw.EndsWith('\\');
    private static bool IsReplacement(NativePathValue path) => path.Raw.EndsWith("/.", StringComparison.Ordinal)
        || Windows(path) && path.Raw.EndsWith("\\.", StringComparison.Ordinal);
    private static NativeConnection? Remote(NativePathValue path) => path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection ? connection : null;
    private static string Mode(bool container, bool replace) => replace ? "replace" : container ? "container" : "exact";

    internal static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) when (process.HasExited) { }
    }
}
