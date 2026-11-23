using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

// One straight-line effect region: nominal/physical aliases overlap by path,
// while an unclassified tree write may change an unenumerated namespace.
internal sealed class HpirTransferFrontier
{
    private readonly PlanResourceIndex readers = new(), writers = new();
    private readonly Dictionary<string, PlanResource[]> paths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<int> predecessors = [];
    private readonly List<int> openNamespaceWrites = [];

    internal bool MayChangeSelection(string directory) =>
        openNamespaceWrites.Count != 0 || Resources(directory).Any(writers.AnyOverlap);

    internal HpirPreparedTransfer Add(int index, HpirPreparedTransfer transfer, CompilationTrace? trace)
    {
        if (transfer.Kind == HpirTransferSource.Selection && transfer.Pattern is null && transfer.Paths.Count == 0)
            return transfer;

        PlanResource[] writes = Resources(transfer.Destination);
        if (transfer.Kind == HpirTransferSource.Selection && transfer.Pattern is not null)
            predecessors.UnionWith(openNamespaceWrites);
        foreach (string path in transfer.Paths)
            foreach (PlanResource read in Resources(path))
                writers.CollectOverlapping(read, predecessors);
        foreach (PlanResource write in writes)
        {
            writers.CollectOverlapping(write, predecessors);
            readers.CollectOverlapping(write, predecessors);
        }
        int[] edges = predecessors.Order().ToArray();
        predecessors.Clear();

        foreach (string path in transfer.Paths)
            foreach (PlanResource read in Resources(path))
                readers.Add(read, index);
        foreach (PlanResource write in writes) writers.Add(write, index);
        // The destination root suffices for admission. Without its live
        // tree shape, a nested destination link can invalidate observations.
        if (transfer.Kind != HpirTransferSource.Text) openNamespaceWrites.Add(index);
        trace?.Metric("dependencies.edges", edges.Length);
        trace?.Metric("resources.write_keys", writes.Length);
        return transfer with { Predecessors = edges };
    }

    private PlanResource[] Resources(string path)
    {
        if (paths.TryGetValue(path, out PlanResource[]? known)) return known;
        PlanResource[] keys = Resolve(path);
        paths.Add(path, keys);
        return keys;
    }

    private static PlanResource[] Resolve(string path)
    {
        try
        {
            string lexical = Path.GetFullPath(path);
            string canonical = ResourcePath.Resolve(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return lexical.Equals(canonical, comparison) ? [new("local", lexical, true)]
                : [new("local", lexical, true), new("local", canonical, true)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A failed optional resolution is an unknown resource, not an
            // early transfer failure. Runtime still owns the actual path.
            return [new("local", "", true)];
        }
    }
}
