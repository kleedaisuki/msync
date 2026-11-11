using Msync.Core;

namespace Msync.Runtime;

internal sealed class PlanResourceIndex
{
    private sealed class Node
    {
        internal Dictionary<string, Node>? Children;
        private int entry = -1;
        private HashSet<int>? additional;

        internal void Add(int id)
        {
            if (entry < 0) entry = id;
            else if (entry != id) (additional ??= []).Add(id);
        }

        internal void Remove(int id)
        {
            if (entry != id) { additional?.Remove(id); return; }
            if (additional is not { Count: > 0 }) { entry = -1; return; }
            using var entries = additional.GetEnumerator();
            entries.MoveNext();
            entry = entries.Current;
            additional.Remove(entry);
        }

        internal void Collect(HashSet<int> result)
        {
            if (entry < 0) return;
            result.Add(entry);
            if (additional is not null) result.UnionWith(additional);
        }

        internal bool Any(Func<int, bool>? accept)
        {
            if (entry < 0) return false;
            if (accept is null || accept(entry)) return true;
            if (additional is not null)
                foreach (int id in additional)
                    if (accept(id)) return true;
            return false;
        }
    }

    private readonly Dictionary<string, Node> roots = new(StringComparer.Ordinal);

    internal void Add(PlanResource resource, int entry)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node))
            roots.Add(resource.Endpoint, node = new());
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            var children = (node.Children ??= new(Comparer(resource.Endpoint))).GetAlternateLookup<ReadOnlySpan<char>>();
            if (!children.TryGetValue(segment, out var child)) children[segment] = child = new();
            node = child;
        }
        node.Add(entry);
    }

    internal void Remove(PlanResource resource, int entry)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node)) return;
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            if (node.Children is not { } children
                || !children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out node!)) return;
        }
        node.Remove(entry);
    }

    internal void CollectOverlapping(PlanResource resource, HashSet<int> result)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node)) return;
        node.Collect(result);
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            if (node.Children is not { } children
                || !children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out node!)) return;
            node.Collect(result);
        }
        CollectDescendants(node, result);
    }

    internal HashSet<int> Overlapping(PlanResource resource)
    {
        HashSet<int> result = [];
        CollectOverlapping(resource, result);
        return result;
    }

    internal bool AnyOverlap(PlanResource resource) => AnyOverlap(resource, null);

    internal bool AnyOverlap(PlanResource resource, Func<int, bool>? accept)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node)) return false;
        if (node.Any(accept)) return true;
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            if (node.Children is not { } children
                || !children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out node!)) return false;
            if (node.Any(accept)) return true;
        }
        return AnyDescendant(node, accept);
    }

    internal static string Identity(PlanResource resource) => resource.Endpoint + "\0"
        + (resource.Endpoint == "local" && OperatingSystem.IsWindows()
            ? resource.Path.ToUpperInvariant() : resource.Path);

    private static void CollectDescendants(Node node, HashSet<int> result)
    {
        if (node.Children is null) return;
        foreach (var child in node.Children.Values)
        {
            child.Collect(result);
            CollectDescendants(child, result);
        }
    }

    private static bool AnyDescendant(Node node, Func<int, bool>? accept)
    {
        if (node.Children is not { } children) return false;
        foreach (var child in children.Values)
            if (child.Any(accept) || AnyDescendant(child, accept)) return true;
        return false;
    }

    private static StringComparer Comparer(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static ReadOnlySpan<char> Separators(PlanResource resource) =>
        resource.Endpoint == "local" && OperatingSystem.IsWindows() ? "/\\" : "/";
}
