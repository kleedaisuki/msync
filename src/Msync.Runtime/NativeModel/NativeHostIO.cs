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
    public static void Attach(NativeRuntimeContext context)
    {
        context.TransferAsync ??= TransferAsync;
        context.ProcessCallAsync ??= ProcessCallAsync;
    }

    public static NativeRange SelectPath(NativePathValue directory, string pattern, NativeRuntimeContext context)
    {
        NativeRuntimeContext origin = context.CaptureDirectory();
        return new(SelectedPaths(directory, pattern, origin), TypeRef.Path,
            selection: new(directory, pattern));
    }

    private static async IAsyncEnumerable<NativeValue> SelectedPaths(NativePathValue directory, string pattern,
        NativeRuntimeContext context, [EnumeratorCancellation] CancellationToken token = default)
    {
        NativeValue values = await NativeBuiltins.InvokeAsync("fs.directory_iterator", [NativeValue.FromPath(directory)],
            context, static (_, _, _) => throw new InvalidOperationException("Directory iteration cannot invoke source callbacks."), token);
        Regex expression = new(pattern, RegexOptions.CultureInvariant);
        await foreach (NativeValue item in values.AsRange().Items.WithCancellation(token))
            if (expression.IsMatch(Basename(item.AsPath()))) yield return item;
    }

    public static async ValueTask<bool> TransferAsync(NativeValue source, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"{source} -> {destination}");
            return true;
        }
        while (source.Kind == NativeKind.Range && source.AsRange().Resolve is { } resolve)
            source = NativeValue.FromRange(await resolve(token));
        if (source.Kind == NativeKind.Range && source.AsRange().Selection is { Directory.Connection.Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } selection)
            return await TransferSelectionAsync(selection, destination, context, token);
        bool cohort = source.Kind == NativeKind.Range;
        List<NativeValue> sources = [];
        if (cohort)
        {
            await foreach (NativeValue item in source.AsRange().Items.WithCancellation(token)) sources.Add(item);
        }
        else sources.Add(source);
        if (sources.Count == 0) return true;
        bool container = cohort || EndsInSeparator(destination);
        bool replace = IsReplacement(destination);
        ValidateNames(sources, container);
        NativeConnection? remote = Remote(destination);
        NativePathValue[] remoteSources = sources.Where(value => value.Kind == NativeKind.Path)
            .Select(value => value.AsPath()).Where(path => Remote(path).HasValue).ToArray();
        if (remote is { } targetConnection)
        {
            if (remoteSources.Length == sources.Count && remoteSources.All(path => path.Connection == targetConnection))
                await CopyRemoteAsync(remoteSources, destination, context, container, replace, cohort, token);
            else if (remoteSources.Length == 0)
                await UploadAsync(sources, destination, context, container, replace, cohort, token);
            else await RelayAsync(sources, destination, context, container, replace, cohort, token);
            return true;
        }
        if (cohort && replace && remoteSources.Length == 0)
        {
            await ReplaceLocalCohortAsync(sources, destination, context, token);
            return true;
        }
        if (remoteSources.Length == sources.Count && remoteSources.All(path => path.Connection == remoteSources[0].Connection))
        {
            await DownloadAsync(remoteSources, destination, context, container, replace, token);
            return true;
        }
        if (cohort && remoteSources.Length != 0)
        {
            await CopyMixedLocalCohortAsync(sources, destination, context, replace, token);
            return true;
        }
        foreach (NativeValue item in sources)
        {
            string target = TargetPath(destination, item, context, container);
            if (item.Kind == NativeKind.Path && Remote(item.AsPath()).HasValue)
                await DownloadAsync([item.AsPath()], new(target), context, false, replace, token);
            else await CopyLocalItemAsync(item, target, context, container, replace, token);
        }
        return true;
    }

    private static async Task CopyMixedLocalCohortAsync(IReadOnlyList<NativeValue> values, NativePathValue destination,
        NativeRuntimeContext context, bool replace, CancellationToken token)
    {
        string target = TargetPath(destination, values[0], context, false);
        List<(NativePathValue Path, string Source)> local = [];
        Dictionary<NativeConnection, List<NativePathValue>> remote = [];
        foreach (NativeValue value in values)
        {
            NativePathValue path = value.AsPath();
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

    private static async Task CopyLocalItemAsync(NativeValue source, string destination, NativeRuntimeContext context,
        bool container, bool replace, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Kind == NativeKind.String)
        {
            await TransferFiles.WriteLiteralAsync(source.AsString(), destination, token, context.BufferSize, context.Timeline);
            return;
        }
        if (source.Kind != NativeKind.Path) throw new MsyncException($"Cannot transfer a value of type {source.Type}.");
        string input = context.LocalPath(source.AsPath());
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

    private static async Task ReplaceLocalCohortAsync(IReadOnlyList<NativeValue> values, NativePathValue destination,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (values.Count == 0) return;
        string target = TargetPath(destination, values[0], context, false);
        NativePathValue[] paths = values.Select(value => value.AsPath()).ToArray();
        string[] sources = paths.Select(context.LocalPath).ToArray();
        IReadOnlyList<TransferOverlap.Node>?[] snapshots = new IReadOnlyList<TransferOverlap.Node>?[paths.Length];
        bool related = false;
        for (int index = 0; index < paths.Length; index++)
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
                for (int index = 0; index < paths.Length; index++)
                    if (snapshots[index] is { } nodes)
                        TransferOverlap.Materialize(nodes, Path.Combine(stage, Basename(paths[index])), token, context.BufferSize);
            }
            token.ThrowIfCancellationRequested();
            TransferFiles.RemoveNode(target);
            Directory.CreateDirectory(target);
            for (int index = 0; index < paths.Length; index++)
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
                for (int index = 0; index < paths.Length; index++)
                    if (snapshots[index] is { } nodes)
                        TransferOverlap.MakeSnapshotWritable(nodes, Path.Combine(stage, Basename(paths[index])));
                Directory.Delete(stage, true);
            }
        }
    }

    private static void ValidateNames(IReadOnlyList<NativeValue> sources, bool container)
    {
        if (!container) return;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (NativeValue value in sources)
        {
            if (value.Kind != NativeKind.Path)
                throw new MsyncException("A container transfer requires Path sources with basenames; a String source is literal data.");
            string name = Basename(value.AsPath());
            if (!names.Add(name)) throw new MsyncException($"Selected sources collide at destination name '{name}'.");
        }
    }

    private static string TargetPath(NativePathValue target, NativeValue source, NativeRuntimeContext context, bool container)
    {
        string path = context.LocalPath(target);
        if (IsReplacement(target)) path = Path.GetDirectoryName(path)!;
        return container && source.Kind == NativeKind.Path ? Path.Combine(path, Basename(source.AsPath())) : path;
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
