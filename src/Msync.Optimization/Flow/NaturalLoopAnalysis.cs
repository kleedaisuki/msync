namespace Msync.Optimization;

internal sealed record NaturalLoop(int Header, int Latch, int Preheader, IReadOnlySet<int> Blocks);

internal static class NaturalLoopAnalysis
{
    internal static IEnumerable<NaturalLoop> Discover(ControlFlowGraph graph, int maximumBlocks = int.MaxValue)
    {
        foreach (int header in graph.ReversePostOrder)
        {
            int latch = -1, preheader = -1;
            foreach (int predecessor in graph.Predecessors(header))
            {
                if (!graph.IsReachable(predecessor)) continue;
                if (graph.Dominates(header, predecessor))
                {
                    if (latch >= 0) { latch = -1; break; }
                    latch = predecessor;
                }
                else
                {
                    if (preheader >= 0) { latch = -1; break; }
                    preheader = predecessor;
                }
            }
            if (latch < 0 || preheader < 0 || graph.Blocks[preheader].Successors.Count != 1) continue;
            var members = CollectMembers(graph, header, latch, maximumBlocks);
            if (members is not null) yield return new(header, latch, preheader, members);
        }
    }

    private static HashSet<int>? CollectMembers(ControlFlowGraph graph, int header, int latch, int maximumBlocks)
    {
        // A natural loop is the predecessor closure of its latch, stopping at
        // its dominating header. Reject an irreducible side entry.
        HashSet<int> members = [header];
        Stack<int> pending = new();
        if (members.Add(latch)) pending.Push(latch);
        if (members.Count > maximumBlocks) return null;
        while (pending.TryPop(out int block))
            foreach (int predecessor in graph.Predecessors(block))
            {
                if (!graph.IsReachable(predecessor) || members.Contains(predecessor)) continue;
                if (!graph.Dominates(header, predecessor)) return null;
                members.Add(predecessor);
                if (members.Count > maximumBlocks) return null;
                pending.Push(predecessor);
            }
        return members;
    }
}
