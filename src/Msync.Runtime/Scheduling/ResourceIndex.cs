namespace Msync.Runtime;

internal sealed record ResourceKey(string Endpoint, string Path);

internal sealed class ResourceIndex
{
    private sealed class EndpointWrites
    {
        private readonly HashSet<Task> tasks = [];
        private Task? active;
        internal void Add(Task task) { tasks.Add(task); active = task; }
        internal void Remove(Task task) { tasks.Remove(task); if (ReferenceEquals(active, task)) active = null; }
        internal bool Any()
        {
            if (active is not null && !active.IsCompletedSuccessfully) return true;
            tasks.RemoveWhere(static task => task.IsCompletedSuccessfully);
            active = tasks.FirstOrDefault();
            return active is not null;
        }
    }
    public sealed class Node(StringComparer comparer, Node? parent = null, string? name = null)
    {
        public readonly Dictionary<string, Node> Children = new(comparer);
        public readonly HashSet<Task> Readers = [];
        public readonly HashSet<Task> Writers = [];
        public readonly Node? Parent = parent;
        public readonly string? Name = name;
    }
    public readonly record struct Registration(Node? Reader, Node? Writer);
    private readonly Dictionary<string, Node> roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EndpointWrites> endpointWrites = new(StringComparer.Ordinal);
    private Node? unknown;
    public long NodesVisited { get; private set; }
    internal bool HasPendingWriters(string endpoint) =>
        endpointWrites.TryGetValue("*", out var unknownWrites) && unknownWrites.Any()
        || endpointWrites.TryGetValue(endpoint, out var writes) && writes.Any();
    internal void RetireTask(Task task)
    {
        foreach (var writes in endpointWrites.Values) writes.Remove(task);
    }
    private static StringComparer Comparer(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private Node Root(ResourceKey key)
    {
        if (key.Endpoint == "*") return unknown ??= new(StringComparer.Ordinal);
        if (!roots.TryGetValue(key.Endpoint, out var root)) roots.Add(key.Endpoint, root = new(Comparer(key.Endpoint)));
        return root;
    }
    private static IEnumerable<string> Parts(ResourceKey key) => key.Path.Split(key.Endpoint == "local" && OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
    public HashSet<Task> Dependencies(IReadOnlyList<ResourceKey> reads, IReadOnlyList<ResourceKey> writes,
        bool readDescendants = true)
    {
        HashSet<Task> tasks = [];
        foreach (var key in reads) Collect(key, false, tasks, readDescendants);
        foreach (var key in writes) Collect(key, true, tasks);
        return tasks;
    }
    private static void AddActive(HashSet<Task> source, HashSet<Task> target)
    {
        source.RemoveWhere(task => task.IsCompletedSuccessfully);
        target.UnionWith(source);
    }
    private void Collect(ResourceKey key, bool writing, HashSet<Task> tasks, bool descendants = true)
    {
        if (unknown is not null)
        {
            AddActive(unknown.Writers, tasks);
            if (writing) AddActive(unknown.Readers, tasks);
        }
        if (key.Endpoint == "*")
        {
            foreach (var root in roots.Values)
            {
                NodesVisited++;
                AddActive(root.Writers, tasks);
                if (writing) AddActive(root.Readers, tasks);
                CollectDescendants(root, writing, tasks);
            }
            return;
        }
        var node = Root(key);
        NodesVisited++;
        AddActive(node.Writers, tasks);
        if (writing) AddActive(node.Readers, tasks);
        foreach (var part in Parts(key))
        {
            if (!node.Children.TryGetValue(part, out node!)) return;
            NodesVisited++;
            AddActive(node.Writers, tasks);
            if (writing) AddActive(node.Readers, tasks);
        }
        if (descendants) CollectDescendants(node, writing, tasks);
    }
    private void CollectDescendants(Node node, bool writing, HashSet<Task> tasks)
    {
        foreach (var child in node.Children.Values)
        {
            NodesVisited++;
            AddActive(child.Writers, tasks);
            if (writing) AddActive(child.Readers, tasks);
            CollectDescendants(child, writing, tasks);
        }
    }
    public Registration Add(Task completion, ResourceKey? read, ResourceKey write)
    {
        if (!endpointWrites.TryGetValue(write.Endpoint, out var writes)) endpointWrites.Add(write.Endpoint, writes = new());
        writes.Add(completion);
        var reader = read is null ? null : Leaf(read);
        var writer = Leaf(write);
        reader?.Readers.Add(completion);
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
        registration.Reader?.Readers.Remove(completion);
        registration.Writer?.Writers.Remove(completion);
        if (registration.Reader is { } reader) Prune(reader);
        if (registration.Writer is { } writer) Prune(writer);
    }
    private static void Prune(Node node)
    {
        while (node.Parent is { } parent && node.Children.Count == 0 && node.Readers.Count == 0 && node.Writers.Count == 0)
        {
            // A query can clear completed tasks before their owner's lease is retired.
            if (!parent.Children.TryGetValue(node.Name!, out var child) || !ReferenceEquals(child, node)) return;
            parent.Children.Remove(node.Name!);
            node = parent;
        }
    }
    private Node Leaf(ResourceKey key)
    {
        var node = Root(key);
        NodesVisited++;
        foreach (var part in Parts(key))
        {
            if (!node.Children.TryGetValue(part, out var child)) node.Children.Add(part, child = new(Comparer(key.Endpoint), node, part));
            node = child;
            NodesVisited++;
        }
        return node;
    }
}
