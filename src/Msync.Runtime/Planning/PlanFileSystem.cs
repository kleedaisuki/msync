using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

internal sealed class PlanFileSystem(RuntimeContext context, ContextualResources resources,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<(ConnectionValue? Connection, string Path), string> kinds = [];
    private readonly Dictionary<(ConnectionValue? Connection, string Path, bool Follow), PlanResource> footprints = [];
    private readonly Dictionary<(ConnectionValue? Connection, string Path), string> projected = [];
    private readonly Dictionary<(ConnectionValue? Connection, string Path), PlanResource> projectedFootprints = [];
    private readonly Dictionary<(ConnectionValue? Connection, string Path), IReadOnlyList<RemotePathMetadata>> trees = [];
    private readonly Dictionary<(ConnectionValue Connection, string Parent), string> parents = [];
    private readonly Dictionary<(ConnectionValue? Connection, string Path), IReadOnlyList<PlanResource>> namespaces = [];
    private readonly Dictionary<PathValue, string> spellings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> localParents = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, NativeFileIdentity.FileSystemIdentity?> devices = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, bool> stableMetadata = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal string Kind(PathValue path)
    {
        var key = (path.Connection, Spelling(path));
        return projected.TryGetValue(key, out string? found) ? found : InitialKind(path);
    }

    internal string InitialKind(PathValue path)
    {
        var key = (path.Connection, Spelling(path));
        if (kinds.TryGetValue(key, out string? found)) return found;
        string kind;
        try { kind = ((StringValue)Invoke("fs.symlink_status", [path])).Data; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { kind = "unknown"; }
        kinds.Add(key, kind);
        return kind;
    }

    internal PlanResource Resource(PathValue path, bool subtree = false, bool followLeaf = false)
    {
        var projectedKey = (path.Connection, Spelling(path));
        if (projectedFootprints.TryGetValue(projectedKey, out var future)
            && (!followLeaf || Kind(path) != "symlink")) return future with { Subtree = subtree };
        var key = (path.Connection, Spelling(path), followLeaf);
        if (!footprints.TryGetValue(key, out var resource))
        {
            string endpoint = path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote
                ? remote.ToString() : "local";
            string name;
            if (endpoint == "local")
            {
                string local = path.LocalPath(context.WorkingDirectory, context.HomeDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (local.Length == 0) local = Path.GetPathRoot(context.WorkingDirectory)!;
                string? parent = Path.GetDirectoryName(local);
                if (followLeaf || string.IsNullOrEmpty(parent)) name = ResolveLocal(local);
                else name = Path.Combine(ResolveLocal(parent), Path.GetFileName(local));
            }
            else
            {
                string raw = path.Raw.TrimEnd('/');
                int separator = raw.LastIndexOf('/');
                string parent = separator < 0 ? "." : separator == 0 ? "/" : raw[..separator];
                string leaf = separator < 0 ? raw : raw[(separator + 1)..];
                if (followLeaf) name = ((PathValue)Invoke("fs.weakly_canonical", [path])).Raw;
                else
                {
                    if (!parents.TryGetValue((path.Connection!, parent), out string? canonical))
                    {
                        canonical = ((PathValue)Invoke("fs.weakly_canonical", [path.WithRaw(parent)])).Raw;
                        parents.Add((path.Connection!, parent), canonical);
                    }
                    name = canonical.TrimEnd('/') + "/" + leaf;
                }
            }
            footprints.Add(key, resource = new(endpoint, name));
        }
        return subtree ? resource with { Subtree = true } : resource;
    }

    internal IReadOnlyList<PlanResource> ObservationResources(PathValue path, bool followLeaf = true)
    {
        PlanResource entry = Resource(path, true);
        HashSet<PlanResource> observed = new(NamespaceResources(path, followLeaf), ResourceIdentity.Instance) { entry };
        if (followLeaf) observed.Add(Resource(path, true, true));
        return observed.ToArray();
    }

    internal IReadOnlyList<PlanResource> NamespaceResources(PathValue path, bool followLeaf = false)
    {
        bool windows = path.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl) && OperatingSystem.IsWindows();
        string spelling = Spelling(path);
        string inspected = followLeaf ? spelling : path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            ? RemoteParent(spelling) : Path.GetDirectoryName(spelling) ?? spelling;
        var key = (path.Connection, inspected);
        if (namespaces.TryGetValue(key, out var found)) return found;
        List<PathValue> prefixes = [];
        string root = path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            ? inspected.StartsWith('/') ? "/" : "" : Path.GetPathRoot(inspected) ?? "";
        string current = root;
        foreach (string part in inspected[root.Length..].Split(windows ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : current.EndsWith('/') || windows && current.EndsWith('\\')
                ? current + part : current + (windows ? "\\" : "/") + part;
            prefixes.Add(path.WithRaw(current));
        }
        Prefetch(prefixes, includeKinds: true);
        PlanResource[] entries = prefixes.Where(prefix => InitialKind(prefix) == "symlink")
            .Select(prefix => Resource(prefix)).Distinct(ResourceIdentity.Instance).ToArray();
        namespaces.Add(key, entries);
        return entries;
    }

    internal IReadOnlyList<RemotePathMetadata> Tree(PathValue directory)
    {
        var key = (directory.Connection, Spelling(directory));
        if (trees.TryGetValue(key, out var known)) return known;
        if (directory.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
        {
            var entries = TransferEngine.HasSelectedQuery(remote)
                ? TransferEngine.ReadSelectedDirectoryMetadataAsync(directory, true, cancellationToken).GetAwaiter().GetResult()
                : resources.GetSession(remote, cancellationToken)
                    .ReadDirectoryMetadataAsync(directory, recursive: true, cancellationToken).GetAwaiter().GetResult();
            foreach (var item in entries) Remember(item);
            var ordered = entries.OrderBy(item => item.Path.Raw, TextSemantics.Ordinal).ToArray();
            trees.Add(key, ordered);
            return ordered;
        }
        List<RemotePathMetadata> result = [];
        Stack<PathValue> pending = new(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            PathValue[] children = Directory.GetFileSystemEntries(current.LocalPath(context.WorkingDirectory, context.HomeDirectory))
                .Order(TextSemantics.Ordinal).Select(child => PathSemantics.JoinValue(current,
                    new StringValue(Path.GetFileName(child)))).ToArray();
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string kind = Kind(child);
                result.Add(new(child, kind, Resource(child).Path));
                if (kind == "directory") pending.Push(child);
            }
        }
        var snapshot = result.OrderBy(item => item.Path.Raw, TextSemantics.Ordinal).ToArray();
        trees.Add(key, snapshot);
        return snapshot;
    }

    internal PlanResource LogicalResource(PathValue path, bool subtree = false)
    {
        string endpoint = path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote ? remote.ToString() : "local";
        string raw = Spelling(path);
        bool windows = endpoint == "local" && OperatingSystem.IsWindows();
        char separator = windows ? '\\' : '/';
        string prefix = endpoint != "local" ? raw.StartsWith('/') ? "/" : "" : Path.GetPathRoot(raw) ?? "";
        string suffix = string.Join(separator, raw[prefix.Length..]
            .Split(windows ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries).Where(part => part != "."));
        return new(endpoint, prefix + suffix, subtree);
    }

    internal void RememberProjected(PathValue path, string kind, PlanResource? resource = null)
    {
        var key = (path.Connection, Spelling(path));
        projected[key] = kind;
        if (resource is not null) projectedFootprints[key] = resource;
    }

    internal bool IsProjected(PathValue path) => projected.ContainsKey((path.Connection, Spelling(path)));

    internal bool CopyFileSystem(PathValue source, PlanResource intermediate)
    {
        var origin = CopySourceFileSystem(source);
        if (origin is null) return false;
        string? parent = Path.GetDirectoryName(intermediate.Path);
        while (!string.IsNullOrEmpty(parent))
        {
            if (FileSystem(parent) is { } target)
            {
                if (OperatingSystem.IsWindows()
                    && (File.GetAttributes(parent) & (FileAttributes.Compressed | FileAttributes.Encrypted)) != 0) return false;
                return origin == target;
            }
            string? next = Path.GetDirectoryName(parent);
            if (next == parent) break;
            parent = next;
        }
        return false;
    }

    private NativeFileIdentity.FileSystemIdentity? CopySourceFileSystem(PathValue source)
    {
        string path = Resource(source).Path;
        if (!stableMetadata.TryGetValue(path, out bool stable))
        {
            try
            {
                stable = OperatingSystem.IsWindows()
                    ? (File.GetAttributes(path) & (FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)) == 0
                    : (NativeFileMetadata.Capture(path).Mode & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit)) == 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { stable = false; }
            stableMetadata.Add(path, stable);
        }
        return stable ? FileSystem(path) : null;
    }

    private NativeFileIdentity.FileSystemIdentity? FileSystem(string path)
    {
        if (devices.TryGetValue(path, out var device)) return device;
        device = NativeFileIdentity.TryFileSystem(path, out var identity) ? identity : null;
        devices.Add(path, device);
        return device;
    }

    internal RangeValue Select(PathValue directory, string pattern, bool recursive = false)
    {
        using var selectedSession = SelectSession(directory);
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        if (directory.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection)
        {
            var metadata = TransferEngine.HasSelectedQuery(connection)
                ? TransferEngine.ReadSelectedDirectoryMetadataAsync(directory, recursive, cancellationToken).GetAwaiter().GetResult()
                : resources.GetSession(connection, cancellationToken)
                    .ReadDirectoryMetadataAsync(directory, recursive, cancellationToken).GetAwaiter().GetResult();
            Value[] paths = metadata.Where(item => regex.IsMatch(PathSemantics.FileName(item.Path)))
                .OrderBy(item => item.Path.Raw, TextSemantics.Ordinal).Select(item =>
                {
                    Remember(item);
                    return (Value)item.Path;
                }).ToArray();
            return new(paths, TypeRef.Path);
        }
        if (recursive)
            return new(Tree(directory).Where(item => regex.IsMatch(PathSemantics.FileName(item.Path)))
                .Select(item => (Value)item.Path).ToArray(), TypeRef.Path);
        var range = (RangeValue)Invoke("fs.directory_iterator", [directory]);
        List<Value> selected = [];
        var iterator = range.Items.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (iterator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                if (iterator.Current is PathValue path && regex.IsMatch(PathSemantics.FileName(path)))
                    selected.Add(path);
        }
        finally { iterator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return new(selected.ToArray(), TypeRef.Path);
    }

    internal PathValue Target(PathValue destination, Value source, bool container)
    {
        if (!container || source is not PathValue path) return destination;
        return PathSemantics.JoinValue(destination, new StringValue(PathSemantics.FileName(path)));
    }

    internal void Prefetch(IEnumerable<PathValue> paths, bool includeKinds)
    {
        var pending = paths.Where(path => path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            .Where(path => !IsProjected(path))
            .Where(path => !footprints.ContainsKey((path.Connection, Spelling(path), false))
                || includeKinds && !kinds.ContainsKey((path.Connection, Spelling(path))))
            .Distinct().GroupBy(path => path.Connection!);
        foreach (var endpoint in pending)
        {
            if (TransferEngine.HasSelectedQuery(endpoint.Key))
            {
                foreach (var path in endpoint)
                {
                    if (includeKinds) _ = InitialKind(path);
                    _ = Resource(path);
                }
                continue;
            }
            var session = resources.GetSession(endpoint.Key, cancellationToken);
            List<PathValue> batch = [];
            foreach (var group in endpoint.GroupBy(path => RemoteParent(path.Raw)))
            {
                PathValue[] selected = group.ToArray();
                if (includeKinds && selected.Length > 1 && selected.All(path =>
                    PathSemantics.FileName(path) is not ("" or "." or ".." or "~")))
                {
                    try
                    {
                        PathValue directory = selected[0].WithRaw(group.Key);
                        var entries = session.ReadDirectoryMetadataAsync(directory, cancellationToken: cancellationToken)
                            .GetAwaiter().GetResult();
                        var names = entries.ToDictionary(item => PathSemantics.FileName(item.Path), StringComparer.Ordinal);
                        string canonical = entries.Count == 0 ? Resource(directory, true, true).Path : RemoteParent(entries[0].CanonicalPath);
                        foreach (var path in selected)
                        {
                            string name = PathSemantics.FileName(path);
                            Remember(names.TryGetValue(name, out var entry) ? entry with { Path = path }
                                : new(path, "not_found", canonical.TrimEnd('/') + "/" + name));
                        }
                        continue;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                batch.AddRange(selected);
            }
            foreach (var item in session.ReadMetadataBatchAsync(batch, cancellationToken, includeKinds)
                .GetAwaiter().GetResult()) Remember(item);
        }
    }

    internal IReadOnlyList<PlanResource> DirectoryWrites(PathValue source, PathValue destination,
        TransferDestinationMode mode)
    {
        PlanResource root = Resource(destination, true, mode != TransferDestinationMode.Replace);
        if (mode == TransferDestinationMode.Replace) return [root];
        if (Kind(destination) is not ("directory" or "symlink")) return [root];
        List<PlanResource> result = [root];
        PathValue[] targets = Directories(source)
            .Select(relative => PathSemantics.JoinValue(destination, new StringValue(relative))).ToArray();
        Prefetch(targets, includeKinds: true);
        foreach (var target in targets)
        {
            PlanResource physical = Resource(target, true, true);
            if (!result.Any(resource => TransferGraph.Contains(resource, physical))) result.Add(physical);
        }
        return result;
    }

    private IEnumerable<string> Directories(PathValue source)
    {
        if (source.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
        {
            var entries = Tree(source);
            foreach (var item in entries)
            {
                Remember(item);
                if (item.Kind == "directory") yield return item.Path.Raw[(source.Raw.TrimEnd('/').Length + 1)..];
            }
            yield break;
        }
        string root = source.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        Stack<string> pending = new(); pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                yield return Path.GetRelativePath(root, directory);
                pending.Push(directory);
            }
        }
    }

    private void Remember(RemotePathMetadata metadata)
    {
        var key = (Connection: metadata.Path.Connection, Path: Spelling(metadata.Path));
        if (metadata.Kind.Length != 0) kinds[key] = metadata.Kind;
        if (metadata.Error is not null || metadata.CanonicalPath.Length == 0) return;
        string endpoint = metadata.Path.Connection!.ToString();
        var resource = new PlanResource(endpoint, metadata.CanonicalPath);
        footprints[(key.Connection, key.Path, false)] = resource;
        if (metadata.Kind.Length != 0 && metadata.Kind != "symlink")
            footprints[(key.Connection, key.Path, true)] = resource;
        if (metadata.Path.Raw != "~" && PathSemantics.FileName(metadata.Path) is not ("" or "." or ".."))
            parents[(metadata.Path.Connection!, RemoteParent(metadata.Path.Raw))] = RemoteParent(metadata.CanonicalPath);
    }

    private static string RemoteParent(string path)
    {
        int slash = path.TrimEnd('/').LastIndexOf('/');
        return slash < 0 ? "." : slash == 0 ? "/" : path[..slash];
    }

    internal Value Invoke(string name, IReadOnlyList<Value> values)
    {
        var operation = NativeRegistry.GetOperation(name);
        using var selectedSession = operation.ImmediateExternal && values.OfType<PathValue>().FirstOrDefault(path =>
            path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl) is { } remote ? SelectSession(remote) : null;
        return NativeRegistry.InvokeAsync(operation, values, context,
            static (_, _, _) => throw new InvalidOperationException("Contextual evaluation cannot invoke a source callback."),
            cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    internal IDisposable? SelectSession(PathValue path) => path.Connection is
    { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection ? resources.Enter(connection, cancellationToken) : null;

    private string Spelling(PathValue path)
    {
        if (spellings.TryGetValue(path, out string? spelling)) return spelling;
        spelling = path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            ? path.Raw : path.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        spellings.Add(path, spelling);
        return spelling;
    }

    private string ResolveLocal(string path)
    {
        if (localParents.TryGetValue(path, out string? resolved)) return resolved;
        resolved = ResourcePath.Resolve(path);
        localParents.Add(path, resolved);
        return resolved;
    }
}
