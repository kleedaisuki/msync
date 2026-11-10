using Msync.Core;

namespace Msync.Runtime;

internal sealed class TransferGraph
{
    internal sealed record Node(Value Source, PathValue Destination, PlannedTransferKind Kind,
        IReadOnlyList<PlanResource> Reads, IReadOnlyList<PlanResource> Writes,
        SourceSpan? Span, bool CompleteWrite, bool Observed, int Instruction,
        TransferDestinationMode Mode = TransferDestinationMode.Exact);

    private sealed record Definition(int Node, PlanResource Resource);
    private readonly List<Node> nodes = [];
    private readonly List<HashSet<int>> uses = [];
    private readonly List<Definition> definitions = [];
    private readonly Dictionary<PlanResource, int> latest = new(ResourceIdentity.Instance);
    private readonly HashSet<int> surviving = [];
    private readonly PlanResourceIndex writes = new();

    internal int Count => nodes.Count;
    internal IReadOnlyList<Node> Nodes => nodes;
    internal bool HasWrites(PlanResource resource) => writes.AnyOverlap(resource);
    internal IReadOnlyList<Node> Overlapping(PlanResource resource) => writes.Overlapping(resource)
        .Select(index => definitions[index].Node).Distinct().Order().Select(index => nodes[index]).ToArray();
    internal bool NamespaceMayChange(PlanResource resource) => Overlapping(resource).Any(node =>
        node.Kind is PlannedTransferKind.CopyDirectory or PlannedTransferKind.CopyLink or PlannedTransferKind.GenericCopy
            or PlannedTransferKind.EnsureDirectory);

    internal string? ProducedKind(PlanResource resource)
    {
        if (!latest.TryGetValue(resource, out int definition)) return null;
        return nodes[definitions[definition].Node].Kind switch
        {
            PlannedTransferKind.WriteText or PlannedTransferKind.CopyFile => "regular",
            PlannedTransferKind.CopyDirectory or PlannedTransferKind.EnsureDirectory
                or PlannedTransferKind.ApplyDirectoryMetadata => "directory",
            PlannedTransferKind.CopyLink => "symlink",
            _ => "unknown"
        };
    }

    internal int Add(Node node)
    {
        int index = nodes.Count;
        HashSet<int> reaching = [];
        foreach (var read in node.Reads)
            foreach (int predecessor in writes.Overlapping(read)) reaching.Add(definitions[predecessor].Node);
        nodes.Add(node);
        uses.Add(reaching);
        foreach (var write in node.Writes)
        {
            if (node.CompleteWrite)
            {
                foreach (int prior in writes.Overlapping(write))
                {
                    PlanResource previous = definitions[prior].Resource;
                    bool covered = write.Subtree ? Contains(write, previous)
                        : !previous.Subtree && ResourceIdentity.Instance.Equals(write, previous);
                    if (!covered) continue;
                    surviving.Remove(prior);
                    writes.Remove(previous, prior);
                    if (latest.GetValueOrDefault(previous, -1) == prior) latest.Remove(previous);
                }
            }
            int identity = definitions.Count;
            definitions.Add(new(index, write));
            surviving.Add(identity);
            writes.Add(write, identity);
            latest[write] = identity;
        }
        return index;
    }

    internal ExecutionPlan Build(int function, int start, int end)
    {
        HashSet<int> live = [];
        Stack<int> pending = new(surviving.Select(index => definitions[index].Node));
        for (int index = 0; index < nodes.Count; index++)
            if (nodes[index].Observed) pending.Push(index);
        while (pending.TryPop(out int current))
        {
            if (!live.Add(current)) continue;
            foreach (int predecessor in uses[current]) pending.Push(predecessor);
        }

        var readers = new PlanResourceIndex();
        var writers = new PlanResourceIndex();
        Dictionary<PlanResource, int> orderedWriters = new(ResourceIdentity.Instance);
        Dictionary<PlanResource, List<int>> orderedReaders = new(ResourceIdentity.Instance);
        List<PlannedTransfer> transfers = [];
        List<int> results = [];
        foreach (int old in live.Order())
        {
            Node node = nodes[old];
            HashSet<int> predecessors = [];
            foreach (var write in node.Writes)
            {
                predecessors.UnionWith(writers.Overlapping(write));
                predecessors.UnionWith(readers.Overlapping(write));
            }
            foreach (var read in node.Reads) predecessors.UnionWith(writers.Overlapping(read));
            int id = transfers.Count;
            transfers.Add(new(id, node.Source, node.Destination, node.Kind, node.Reads, node.Writes,
                predecessors.Order().ToArray(), node.Span, node.Mode));
            foreach (var write in node.Writes)
            {
                if (orderedWriters.TryGetValue(write, out int previous)) writers.Remove(write, previous);
                if (orderedReaders.Remove(write, out var obsoleteReads))
                    foreach (int prior in obsoleteReads) readers.Remove(write, prior);
                orderedWriters[write] = id;
                writers.Add(write, id);
            }
            foreach (var read in node.Reads)
            {
                readers.Add(read, id);
                if (!orderedReaders.TryGetValue(read, out var currentReads)) orderedReaders.Add(read, currentReads = []);
                currentReads.Add(id);
            }
            if (node.Observed) results.Add(id);
        }
        return new(function, start, end, transfers, Batches(transfers), nodes.Count - live.Count,
            results.Count == 0 ? null : results);
    }

    internal static bool Contains(PlanResource parent, PlanResource child)
    {
        if (parent.Endpoint != child.Endpoint) return false;
        bool windows = parent.Endpoint == "local" && OperatingSystem.IsWindows();
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = windows ? parent.Path.TrimEnd('/', '\\') : parent.Path.TrimEnd('/');
        return child.Path.Equals(root, comparison) || child.Path.Length > root.Length
            && child.Path.AsSpan().StartsWith(root, comparison)
            && (child.Path[root.Length] == '/' || windows && child.Path[root.Length] == '\\');
    }

    private static IReadOnlyList<ExecutionBatch> Batches(IReadOnlyList<PlannedTransfer> transfers)
    {
        Dictionary<(string Endpoint, string Dependencies), List<int>> groups = [];
        foreach (var transfer in transfers)
        {
            string endpoint = transfer.Destination.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote
                ? remote.ToString() : transfer.Source is PathValue { Connection.Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } path
                    ? path.Connection!.ToString() : "local";
            var key = (endpoint, transfer.Kind is PlannedTransferKind.CopyDirectory or PlannedTransferKind.CopyLink or PlannedTransferKind.GenericCopy
                or PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata
                ? "namespace:" + transfer.Id : string.Join(',', transfer.Predecessors));
            if (!groups.TryGetValue(key, out var items)) groups.Add(key, items = []);
            items.Add(transfer.Id);
        }
        return groups.Select(group => new ExecutionBatch(group.Value, group.Key.Endpoint)).ToArray();
    }
}

internal sealed class ResourceIdentity : IEqualityComparer<PlanResource>
{
    internal static readonly ResourceIdentity Instance = new();
    public bool Equals(PlanResource? left, PlanResource? right) => ReferenceEquals(left, right)
        || left is not null && right is not null && left.Endpoint == right.Endpoint
            && Comparer(left.Endpoint).Equals(left.Path, right.Path);
    public int GetHashCode(PlanResource resource) => HashCode.Combine(resource.Endpoint,
        Comparer(resource.Endpoint).GetHashCode(resource.Path));
    private static StringComparer Comparer(string endpoint) => endpoint == "local" && OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
