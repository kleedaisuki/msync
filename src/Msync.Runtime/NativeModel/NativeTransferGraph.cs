using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

internal sealed class NativeTransferGraph
{
    internal sealed record Node(NativePlanValue Source, NativePathValue Destination, PlannedTransferKind Kind,
        IReadOnlyList<PlanResource> Reads, IReadOnlyList<PlanResource> Writes,
        SourceSpan? Span, bool CompleteWrite, bool Observed, int Instruction,
        TransferDestinationMode Mode = TransferDestinationMode.Exact);

    private readonly record struct Definition(int Node, PlanResource Resource);
    private readonly List<Node> nodes = [];
    private readonly List<int[]> uses = [];
    private readonly List<Definition> definitions = [];
    private readonly Dictionary<PlanResource, int> latest = new(ResourceIdentity.Instance);
    private readonly HashSet<int> surviving = [];
    private readonly PlanResourceIndex writes = new();
    private HashSet<int>? scratch;
    private int namespaceDefinitions;
    private Func<int, bool>? isNamespaceDefinition;

    internal int Count => nodes.Count;
    internal IReadOnlyList<Node> Nodes => nodes;
    internal bool HasWrites(PlanResource resource) => writes.AnyOverlap(resource);
    internal bool HasAncestorWrites(PlanResource resource) => writes.AnyAncestor(resource);
    internal IReadOnlyList<Node> Overlapping(PlanResource resource) => writes.Overlapping(resource)
        .Select(index => definitions[index].Node).Distinct().Order().Select(index => nodes[index]).ToArray();
    internal bool NamespaceMayChange(PlanResource resource) => namespaceDefinitions != 0
        && writes.AnyOverlap(resource, isNamespaceDefinition ??= definition => ChangesNamespace(nodes[definitions[definition].Node]));

    private static bool ChangesNamespace(Node node) => node.Kind is PlannedTransferKind.CopyDirectory
        or PlannedTransferKind.CopyLink or PlannedTransferKind.GenericCopy or PlannedTransferKind.EnsureDirectory;

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
        int[] reaching = [];
        if (node.Reads.Count != 0 && surviving.Count != 0)
        {
            var overlap = scratch ??= [];
            foreach (var read in node.Reads) writes.CollectOverlapping(read, overlap);
            if (overlap.Count != 0) reaching = overlap.Select(prior => definitions[prior].Node).Distinct().ToArray();
            overlap.Clear();
        }
        nodes.Add(node);
        uses.Add(reaching);
        bool namespaceChange = ChangesNamespace(node);
        foreach (var write in node.Writes)
        {
            if (node.CompleteWrite && surviving.Count != 0)
            {
                var overlap = scratch ??= [];
                writes.CollectOverlapping(write, overlap);
                foreach (int prior in overlap)
                {
                    Definition definition = definitions[prior];
                    PlanResource previous = definition.Resource;
                    bool covered = write.Subtree ? Contains(write, previous)
                        : !previous.Subtree && ResourceIdentity.Instance.Equals(write, previous);
                    if (!covered) continue;
                    surviving.Remove(prior);
                    if (ChangesNamespace(nodes[definition.Node])) namespaceDefinitions--;
                    writes.Remove(previous, prior);
                    if (latest.GetValueOrDefault(previous, -1) == prior) latest.Remove(previous);
                }
                overlap.Clear();
            }
            int identity = definitions.Count;
            definitions.Add(new(index, write));
            surviving.Add(identity);
            if (namespaceChange) namespaceDefinitions++;
            writes.Add(write, identity);
            latest[write] = identity;
        }
        return index;
    }

    internal NativeExecutionPlan Build(int function, int start, int end, out IReadOnlyDictionary<int, int> identities)
    {
        Dictionary<int, int> mapped = [];
        identities = mapped;
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
        List<NativePlannedTransfer> transfers = [];
        List<int> results = [];
        HashSet<int> predecessors = [];
        foreach (int old in live.Order())
        {
            Node node = nodes[old];
            foreach (var write in node.Writes)
            {
                writers.CollectOverlapping(write, predecessors);
                readers.CollectOverlapping(write, predecessors);
            }
            foreach (var read in node.Reads) writers.CollectOverlapping(read, predecessors);
            int id = transfers.Count;
            mapped.Add(old, id);
            int[] incoming = predecessors.ToArray();
            Array.Sort(incoming);
            predecessors.Clear();
            transfers.Add(new(id, node.Source, node.Destination, node.Kind, node.Reads, node.Writes,
                incoming, node.Span, node.Mode));
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

    private static IReadOnlyList<ExecutionBatch> Batches(IReadOnlyList<NativePlannedTransfer> transfers)
    {
        Dictionary<(string Endpoint, string Dependencies), List<int>> groups = [];
        foreach (var transfer in transfers)
        {
            string endpoint = transfer.Destination.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote
                ? remote.ToString() : transfer.Source.Path is { Connection.Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } path
                    ? path.Connection!.Value.ToString() : "local";
            var key = (endpoint, transfer.Kind is PlannedTransferKind.CopyDirectory or PlannedTransferKind.CopyLink or PlannedTransferKind.GenericCopy
                or PlannedTransferKind.EnsureDirectory or PlannedTransferKind.ApplyDirectoryMetadata
                ? "namespace:" + transfer.Id : string.Join(',', transfer.Predecessors));
            if (!groups.TryGetValue(key, out var items)) groups.Add(key, items = []);
            items.Add(transfer.Id);
        }
        return groups.Select(group => new ExecutionBatch(group.Value, group.Key.Endpoint)).ToArray();
    }
}
