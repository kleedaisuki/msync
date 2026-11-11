using Msync.Core;

namespace Msync.Runtime;

internal static class FileVersionForwarding
{
    internal static TransferGraph Apply(TransferGraph graph, PlanFileSystem filesystem)
    {
        if (graph.Count < 2 || !HasCandidate(graph, filesystem)) return graph;
        var allWrites = new PlanResourceIndex();
        for (int index = 0; index < graph.Nodes.Count; index++)
            foreach (var write in graph.Nodes[index].Writes) allWrites.Add(write, index);
        Dictionary<PlanResource, (int Index, TransferGraph.Node Node)> current = new(ResourceIdentity.Instance);
        // A forwarding producer is an exact one-file version. Only a subtree
        // overwrite needs the hierarchical index to retire covered producers.
        PlanResourceIndex? subtrees = graph.Nodes.Any(node => node.Writes.Any(write => write.Subtree)) ? new() : null;
        HashSet<int>? covered = null;
        List<TransferGraph.Node>? changed = null;
        int originalIndex = 0;
        foreach (var original in graph.Nodes)
        {
            TransferGraph.Node node = original;
            if (node.Kind == PlannedTransferKind.CopyFile && node.Source is PathValue input)
            {
                PlanResource read = filesystem.Resource(input);
                if (current.TryGetValue(read, out var candidate)
                    && TryForward(candidate.Node, node, allWrites, filesystem, out var forwarded))
                {
                    node = forwarded!;
                    changed ??= graph.Nodes.Take(originalIndex).ToList();
                }
            }
            changed?.Add(node);
            int identity = originalIndex++;
            foreach (var write in node.Writes)
            {
                if (write.Subtree)
                {
                    var overlap = covered ??= [];
                    subtrees!.CollectOverlapping(write, overlap);
                    foreach (int previous in overlap)
                    {
                        PlanResource definition = graph.Nodes[previous].Writes[0];
                        if (!TransferGraph.Contains(write, definition)) continue;
                        current.Remove(definition);
                        subtrees.Remove(definition, previous);
                    }
                    overlap.Clear();
                }
                if (current.Remove(write, out var replaced)) subtrees?.Remove(write, replaced.Index);
            }
            if (node.Kind != PlannedTransferKind.CopyFile || node.Writes.Count != 1) continue;
            current.Add(node.Writes[0], (identity, node));
            subtrees?.Add(node.Writes[0], identity);
        }
        if (changed is null) return graph;
        var output = new TransferGraph();
        foreach (var node in changed) output.Add(node);
        return output;
    }

    private static bool HasCandidate(TransferGraph graph, PlanFileSystem filesystem)
    {
        HashSet<PlanResource> written = new(ResourceIdentity.Instance);
        foreach (var node in graph.Nodes)
        {
            if (node.Kind != PlannedTransferKind.CopyFile) continue;
            if (node.Source is PathValue source && written.Contains(filesystem.Resource(source))) return true;
            if (!node.Observed) written.UnionWith(node.Writes);
        }
        return false;
    }

    private static bool TryForward(TransferGraph.Node producer, TransferGraph.Node consumer,
        PlanResourceIndex writes, PlanFileSystem filesystem, out TransferGraph.Node? forwarded)
    {
        forwarded = null;
        if (producer.Observed || !producer.CompleteWrite || producer.Source is not PathValue source
            || consumer.Source is not PathValue previous || source.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            || previous.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
            || producer.Writes[0].Endpoint != "local" || consumer.Writes.Any(write => write.Endpoint != "local")
            || filesystem.InitialKind(source) != "regular") return false;
        PlanResource[] sourceReads = [filesystem.Resource(source), .. filesystem.NamespaceResources(source)];
        if (sourceReads.Any(writes.AnyOverlap) || !filesystem.CopyFileSystem(source, producer.Writes[0])) return false;
        HashSet<PlanResource> prior = new(filesystem.NamespaceResources(previous), ResourceIdentity.Instance)
        { filesystem.Resource(previous) };
        HashSet<PlanResource> reads = new(consumer.Reads.Where(read => !prior.Contains(read)), ResourceIdentity.Instance);
        reads.UnionWith(sourceReads);
        reads.UnionWith(filesystem.NamespaceResources(consumer.Destination));
        forwarded = consumer with { Source = source, Reads = reads.ToArray() };
        return true;
    }
}
