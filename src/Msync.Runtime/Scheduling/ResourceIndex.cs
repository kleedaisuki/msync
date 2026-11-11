namespace Msync.Runtime;

internal readonly record struct ResourceKey(string Endpoint, string Path);

internal sealed class ResourceIndex
{
    public sealed class Node(Node? parent = null, string? name = null)
    {
        // Only branch points need a table. Most common path prefixes are unary.
        internal object? Children;
        internal TaskSet Readers;
        internal TaskSet Writers;
        internal readonly Node? Parent = parent;
        internal readonly string? Name = name;

        internal Node? FindChild(ReadOnlySpan<char> name, bool windows)
        {
            if (Children is Node child)
                return name.Equals(child.Name, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    ? child : null;
            return Children is Dictionary<string, Node> branches
                && branches.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var match) ? match : null;
        }

        internal Node AddChild(ReadOnlySpan<char> name, bool windows)
        {
            string key = name.ToString();
            Node child = new(this, key);
            if (Children is null) Children = child;
            else
            {
                if (Children is Node first)
                    Children = new Dictionary<string, Node>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    { [first.Name!] = first };
                ((Dictionary<string, Node>)Children).Add(key, child);
            }
            return child;
        }

        internal bool RemoveChild(Node child)
        {
            if (ReferenceEquals(Children, child)) { Children = null; return true; }
            if (Children is not Dictionary<string, Node> branches
                || !branches.TryGetValue(child.Name!, out var match) || !ReferenceEquals(match, child)) return false;
            branches.Remove(child.Name!);
            if (branches.Count == 1)
            {
                using var remaining = branches.Values.GetEnumerator();
                remaining.MoveNext();
                Children = remaining.Current;
            }
            return true;
        }
    }

    // Most leaves represent one active transfer; shared sources retain set semantics.
    internal struct TaskSet
    {
        private Task? first;
        private HashSet<Task>? shared;

        internal readonly bool IsEmpty => first is null && shared is null;

        internal void Add(Task task)
        {
            if (shared is { } tasks) tasks.Add(task);
            else if (first is null) first = task;
            else if (!ReferenceEquals(first, task))
            {
                shared = [first, task];
                first = null;
            }
        }

        internal void Remove(Task task)
        {
            if (ReferenceEquals(first, task)) first = null;
            if (shared is { } tasks && tasks.Remove(task)) Compact(tasks);
        }

        internal void Collect(HashSet<Task> target)
        {
            if (first is { } task)
            {
                if (task.IsCompletedSuccessfully) first = null;
                else target.Add(task);
            }
            if (shared is not { } tasks) return;
            tasks.RemoveWhere(static item => item.IsCompletedSuccessfully);
            target.UnionWith(tasks);
            Compact(tasks);
        }

        private void Compact(HashSet<Task> tasks)
        {
            if (tasks.Count > 1) return;
            using var remaining = tasks.GetEnumerator();
            first = remaining.MoveNext() ? remaining.Current : null;
            shared = null;
        }
    }

    public readonly record struct Registration(Node? Reader, Node? Writer);
    private readonly Dictionary<string, Node> roots = new(StringComparer.Ordinal);
    private Node? unknown;
    public long NodesVisited { get; private set; }
    internal bool HasRegistrations
    {
        get
        {
            if (unknown is { } wildcard && !Vacant(wildcard)) return true;
            foreach (var (_, root) in roots)
                if (!Vacant(root)) return true;
            return false;
        }
    }

    private static bool Vacant(Node node) => node.Children is null && node.Readers.IsEmpty && node.Writers.IsEmpty;

    private static bool Windows(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows();

    public HashSet<Task> Dependencies(IReadOnlyList<ResourceKey> reads, IReadOnlyList<ResourceKey> writes)
    {
        HashSet<Task> tasks = [];
        foreach (var key in reads) Collect(key, false, tasks);
        foreach (var key in writes) Collect(key, true, tasks);
        return tasks;
    }

    internal void Collect(ResourceKey key, bool writing, HashSet<Task> tasks)
    {
        if (unknown is not null) CollectNode(unknown, writing, tasks);
        if (key.Endpoint == "*")
        {
            foreach (var root in roots.Values)
            {
                NodesVisited++;
                CollectNode(root, writing, tasks);
                CollectDescendants(root, writing, tasks);
            }
            return;
        }
        if (!roots.TryGetValue(key.Endpoint, out var node)) return;
        NodesVisited++;
        CollectNode(node, writing, tasks);
        bool windows = Windows(key.Endpoint);
        var path = key.Path.AsSpan();
        while (TryNextPart(ref path, windows, out var part))
        {
            if (node.FindChild(part, windows) is not { } next) return;
            node = next;
            NodesVisited++;
            CollectNode(node, writing, tasks);
        }
        CollectDescendants(node, writing, tasks);
    }

    private static void CollectNode(Node node, bool writing, HashSet<Task> tasks)
    {
        node.Writers.Collect(tasks);
        if (writing) node.Readers.Collect(tasks);
    }

    private void CollectDescendants(Node node, bool writing, HashSet<Task> tasks)
    {
        if (node.Children is Node single) CollectChild(single, writing, tasks);
        else if (node.Children is Dictionary<string, Node> branches)
            foreach (var child in branches.Values) CollectChild(child, writing, tasks);
    }

    private void CollectChild(Node child, bool writing, HashSet<Task> tasks)
    {
        NodesVisited++;
        CollectNode(child, writing, tasks);
        CollectDescendants(child, writing, tasks);
    }

    public Registration Add(Task completion, ResourceKey? read, ResourceKey write)
    {
        var reader = read is { } input ? Leaf(input) : null;
        var writer = Leaf(write);
        if (reader is not null) reader.Readers.Add(completion);
        writer.Writers.Add(completion);
        return new(reader, writer);
    }

    public Registration AddRead(Task completion, ResourceKey read)
    {
        var node = Leaf(read);
        node.Readers.Add(completion);
        return new(node, null);
    }

    public void Remove(Task completion, Registration registration)
    {
        if (registration.Reader is { } reader)
        {
            reader.Readers.Remove(completion);
            Prune(reader);
        }
        if (registration.Writer is { } writer)
        {
            writer.Writers.Remove(completion);
            Prune(writer);
        }
    }

    private static void Prune(Node node)
    {
        while (node.Parent is { } parent && Vacant(node))
        {
            // Several leases can still refer to a pruned child.
            if (!parent.RemoveChild(node)) return;
            node = parent;
        }
    }

    private Node Leaf(ResourceKey key)
    {
        NodesVisited++;
        if (key.Endpoint == "*") return unknown ??= new();
        if (!roots.TryGetValue(key.Endpoint, out var node)) roots.Add(key.Endpoint, node = new());
        var windows = Windows(key.Endpoint);
        var path = key.Path.AsSpan();
        while (TryNextPart(ref path, windows, out var part))
        {
            node = node.FindChild(part, windows) ?? node.AddChild(part, windows);
            NodesVisited++;
        }
        return node;
    }

    private static bool TryNextPart(ref ReadOnlySpan<char> path, bool windows, out ReadOnlySpan<char> part)
    {
        while (!path.IsEmpty && (path[0] == '/' || windows && path[0] == '\\')) path = path[1..];
        if (path.IsEmpty) { part = default; return false; }
        int end = windows ? path.IndexOfAny('/', '\\') : path.IndexOf('/');
        if (end < 0) { part = path; path = default; }
        else { part = path[..end]; path = path[(end + 1)..]; }
        return true;
    }
}
