using Msync.Core;

namespace Msync.Runtime;

internal sealed class PlanResourceIndex
{
    private sealed class Node(StringComparer comparer)
    {
        internal readonly Dictionary<string, Node> Children = new(comparer);
        internal readonly HashSet<int> Entries = [];
    }

    private readonly Dictionary<string, Node> roots = new(StringComparer.Ordinal);

    internal void Add(PlanResource resource, int entry)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node))
            roots.Add(resource.Endpoint, node = new(Comparer(resource.Endpoint)));
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            var children = node.Children.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!children.TryGetValue(segment, out var child))
                children[segment] = child = new(Comparer(resource.Endpoint));
            node = child;
        }
        node.Entries.Add(entry);
    }

    internal void Remove(PlanResource resource, int entry)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node)) return;
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            if (!node.Children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out node!)) return;
        }
        node.Entries.Remove(entry);
    }

    internal HashSet<int> Overlapping(PlanResource resource)
    {
        HashSet<int> result = [];
        if (!roots.TryGetValue(resource.Endpoint, out var node)) return result;
        result.UnionWith(node.Entries);
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            if (!node.Children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out node!)) return result;
            result.UnionWith(node.Entries);
        }
        Descendants(node, result);
        return result;
    }

    internal bool AnyOverlap(PlanResource resource)
    {
        if (!roots.TryGetValue(resource.Endpoint, out var node)) return false;
        if (node.Entries.Count != 0) return true;
        ReadOnlySpan<char> path = resource.Path;
        foreach (Range part in path.SplitAny(Separators(resource)))
        {
            ReadOnlySpan<char> segment = path[part];
            if (segment.IsEmpty) continue;
            if (!node.Children.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out node!)) return false;
            if (node.Entries.Count != 0) return true;
        }
        return AnyDescendant(node);
    }

    internal static string Identity(PlanResource resource) => resource.Endpoint + "\0"
        + (resource.Endpoint == "local" && OperatingSystem.IsWindows()
            ? resource.Path.ToUpperInvariant() : resource.Path);

    private static void Descendants(Node node, HashSet<int> result)
    {
        foreach (var child in node.Children.Values)
        {
            result.UnionWith(child.Entries);
            Descendants(child, result);
        }
    }

    private static bool AnyDescendant(Node node)
    {
        foreach (var child in node.Children.Values)
            if (child.Entries.Count != 0 || AnyDescendant(child)) return true;
        return false;
    }

    private static StringComparer Comparer(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static ReadOnlySpan<char> Separators(PlanResource resource) =>
        resource.Endpoint == "local" && OperatingSystem.IsWindows() ? "/\\" : "/";
}
