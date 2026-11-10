using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

internal static class PlanNamespaceProjection
{
    internal static bool TrySelect(TransferGraph graph, PathValue directory, string pattern,
        PlanFileSystem filesystem, out RangeValue? selected)
    {
        selected = null;
        string initial = filesystem.InitialKind(directory);
        PlanResource logical = filesystem.LogicalResource(directory, true);
        PlanResource physical = filesystem.Resource(directory, true, true);
        var observations = filesystem.ObservationResources(directory);
        HashSet<TransferGraph.Node> relevant = [];
        foreach (var resource in observations)
            relevant.UnionWith(graph.Overlapping(resource));
        if (relevant.Count == 0) return false;
        var replacement = graph.Nodes.LastOrDefault(node => relevant.Contains(node)
            && node.Kind == PlannedTransferKind.EnsureDirectory && node.Mode == TransferDestinationMode.Replace
            && TransferGraph.Contains(filesystem.LogicalResource(node.Destination), logical));
        bool cleared = replacement is not null;
        if (replacement is not null)
        {
            string relative = Relative(filesystem.LogicalResource(replacement.Destination), logical)!;
            if (relative.Split(logical.Endpoint == "local" && OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'])
                .Any(part => part == "..")) return false;
            physical = Append(replacement.Writes[0], relative);
            initial = "not_found";
        }
        if (initial is not ("directory" or "not_found")) return false;
        bool exists = initial == "directory";
        Dictionary<string, PathValue> names = new(StringComparer.Ordinal);
        if (initial == "directory")
            foreach (PathValue entry in filesystem.Select(directory, ".*").SnapshotValues!.Cast<PathValue>())
                names[PathSemantics.FileName(entry)] = entry;
        foreach (var node in graph.Nodes.Where(relevant.Contains))
        {
            if (node.Kind == PlannedTransferKind.ApplyDirectoryMetadata) continue;
            if (node.Kind is PlannedTransferKind.GenericCopy or PlannedTransferKind.CopyDirectory) return false;
            PlanResource destination = filesystem.LogicalResource(node.Destination, true);
            bool ancestor = TransferGraph.Contains(destination, logical);
            string? relative = Relative(logical, destination);
            if (relative is null)
                relative = node.Writes.Select(write => Relative(physical, write)).FirstOrDefault(value => value is not null);
            if (ancestor)
            {
                if (node.Kind != PlannedTransferKind.EnsureDirectory) return false;
                bool equal = ResourceIdentity.Instance.Equals(destination, logical);
                if (node.Mode == TransferDestinationMode.Replace) { names.Clear(); exists = equal; }
                else if (equal) exists = true;
                continue;
            }
            if (relative is null || relative.Length == 0) continue;
            bool windows = logical.Endpoint == "local" && OperatingSystem.IsWindows();
            string[] parts = relative.Split(windows ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(part => part is "." or "..")) return false;
            string name = parts[0];
            exists = true;
            if (parts.Length > 1 && names.ContainsKey(name)) continue;
            PathValue entryPath = PathSemantics.JoinValue(directory, new StringValue(name));
            string kind = parts.Length > 1 || node.Kind == PlannedTransferKind.EnsureDirectory ? "directory"
                : node.Kind switch
                {
                    PlannedTransferKind.CopyLink => "symlink",
                    PlannedTransferKind.CopyFile or PlannedTransferKind.WriteText => "regular",
                    _ => "unknown"
                };
            PlanResource? entryResource = parts.Length == 1 ? node.Writes.FirstOrDefault() : null;
            if (!cleared && kind == "directory" && node.Mode != TransferDestinationMode.Replace
                && filesystem.InitialKind(entryPath) == "symlink")
            {
                kind = "symlink";
                entryResource = filesystem.Resource(entryPath);
            }
            names[name] = entryPath;
            entryResource ??= Append(physical, name);
            filesystem.RememberProjected(entryPath, kind, entryResource);
        }
        if (!exists) return false;
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        Value[] values = names.OrderBy(pair => pair.Key, TextSemantics.Ordinal).Where(pair => regex.IsMatch(pair.Key))
            .Select(pair => (Value)pair.Value).ToArray();
        foreach (PathValue path in values.Cast<PathValue>())
            filesystem.RememberProjected(path, filesystem.Kind(path));
        selected = new(values, TypeRef.Path);
        filesystem.RememberProjected(directory, "directory", physical with { Subtree = false });
        return true;
    }

    private static PlanResource Append(PlanResource root, string relative)
    {
        if (relative.Length == 0) return root;
        bool windows = root.Endpoint == "local" && OperatingSystem.IsWindows();
        string prefix = windows ? root.Path.TrimEnd('/', '\\') : root.Path.TrimEnd('/');
        return root with
        {
            Path = prefix + (windows ? "\\" : "/")
            + (windows ? relative.Replace('/', '\\') : relative)
        };
    }

    internal static bool TryTree(TransferGraph graph, PathValue directory, PlanFileSystem filesystem,
        out IReadOnlyList<RemotePathMetadata>? tree)
    {
        List<RemotePathMetadata> entries = [];
        Stack<PathValue> pending = new(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            var observations = filesystem.ObservationResources(current);
            RangeValue? members;
            if (observations.Any(graph.HasWrites))
            {
                if (!TrySelect(graph, current, ".*", filesystem, out members)) { tree = null; return false; }
            }
            else members = filesystem.Select(current, ".*");
            foreach (PathValue member in members!.SnapshotValues!.Cast<PathValue>())
            {
                string kind = graph.ProducedKind(filesystem.Resource(member)) ?? filesystem.Kind(member);
                entries.Add(new(member, kind, filesystem.Resource(member).Path));
                if (kind == "directory") pending.Push(member);
            }
        }
        tree = entries.OrderBy(item => item.Path.Raw, TextSemantics.Ordinal).ToArray();
        return true;
    }

    private static string? Relative(PlanResource root, PlanResource child)
    {
        if (!TransferGraph.Contains(root, child)) return null;
        bool windows = root.Endpoint == "local" && OperatingSystem.IsWindows();
        string prefix = windows ? root.Path.TrimEnd('/', '\\') : root.Path.TrimEnd('/');
        return child.Path.Length == prefix.Length ? "" : child.Path[(prefix.Length + 1)..];
    }
}
