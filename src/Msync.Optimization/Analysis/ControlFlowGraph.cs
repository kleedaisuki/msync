namespace Msync.Optimization;

internal enum ControlFlowEdgeKind
{
    Jump,
    Taken,
    Fallthrough,
    IteratorExhausted,
    IteratorYield
}

internal readonly record struct ControlFlowEdge(int Source, int Target, ControlFlowEdgeKind Kind);

internal sealed class ControlFlowGraph
{
    private readonly IReadOnlyList<Instruction> instructions;
    private readonly int[] blockAt;
    private readonly int[][] predecessors;
    private readonly int[][] successors;
    private TypedEdges? typedEdges;
    private Traversal? traversal;
    private DomTree? dominators;

    internal ControlFlowGraph(FunctionCode function)
    {
        instructions = function.Instructions;
        Blocks = BlockBuilder.ForFunction(function, 0);
        blockAt = new int[instructions.Count + 1];
        blockAt[^1] = -1;
        for (int block = 0; block < Blocks.Count; block++)
        {
            var range = Blocks[block];
            Array.Fill(blockAt, block, range.Start, range.End - range.Start);
        }

        var inputCount = new int[Blocks.Count];
        successors = new int[Blocks.Count][];
        for (int block = 0; block < Blocks.Count; block++)
        {
            var targets = Blocks[block].Successors;
            // Bytecode terminators have at most a target and a fallthrough.
            successors[block] = targets.Count switch
            {
                0 => [],
                1 => UniqueTargets(blockAt[targets[0]], -1),
                _ => UniqueTargets(blockAt[targets[0]], blockAt[targets[1]])
            };
            foreach (int target in successors[block]) inputCount[target]++;
        }
        predecessors = new int[Blocks.Count][];
        for (int block = 0; block < Blocks.Count; block++)
            predecessors[block] = inputCount[block] == 0 ? [] : new int[inputCount[block]];
        for (int block = Blocks.Count - 1; block >= 0; block--)
            foreach (int target in successors[block])
                predecessors[target][--inputCount[target]] = block;
    }

    private static int[] UniqueTargets(int first, int second) => first < 0
        ? second < 0 ? [] : [second]
        : second < 0 || second == first ? [first] : [first, second];

    private sealed record TypedEdges(ControlFlowEdge[][] Incoming, ControlFlowEdge[][] Outgoing);

    private TypedEdges ClassifyEdges()
    {
        var inputs = new List<ControlFlowEdge>?[Blocks.Count];
        var outgoing = new ControlFlowEdge[Blocks.Count][];
        for (int block = 0; block < Blocks.Count; block++)
        {
            int end = Blocks[block].End;
            Instruction last = instructions[end - 1];
            ControlFlowEdge Edge(int target, ControlFlowEdgeKind kind) => new(block, blockAt[target], kind);
            var edges = last.Op switch
            {
                OpCode.Return => [],
                OpCode.Jump => [Edge(last.A, ControlFlowEdgeKind.Jump)],
                OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady =>
                    [Edge(last.A, ControlFlowEdgeKind.Taken), Edge(end, ControlFlowEdgeKind.Fallthrough)],
                OpCode.IteratorNext =>
                    [Edge(last.B, ControlFlowEdgeKind.IteratorExhausted), Edge(end, ControlFlowEdgeKind.IteratorYield)],
                _ => new[] { Edge(end, ControlFlowEdgeKind.Fallthrough) }
            };
            outgoing[block] = edges;
            foreach (var edge in edges)
                if (edge.Target >= 0) (inputs[edge.Target] ??= []).Add(edge);
        }
        var incoming = new ControlFlowEdge[Blocks.Count][];
        for (int block = 0; block < Blocks.Count; block++) incoming[block] = inputs[block]?.ToArray() ?? [];
        return new(incoming, outgoing);
    }

    internal IReadOnlyList<BasicBlock> Blocks { get; }
    internal IReadOnlyList<int> ReversePostOrder => ReachableTraversal.Order;
    internal bool Describes(FunctionCode function) => ReferenceEquals(instructions, function.Instructions);
    internal int BlockAt(int pc) => blockAt[pc];
    internal IReadOnlyList<int> Predecessors(int block) => predecessors[block];
    internal IReadOnlyList<int> Successors(int block) => successors[block];
    internal ReadOnlySpan<ControlFlowEdge> IncomingEdges(int block) =>
        (typedEdges ??= ClassifyEdges()).Incoming[block];
    internal ReadOnlySpan<ControlFlowEdge> OutgoingEdges(int block) =>
        (typedEdges ??= ClassifyEdges()).Outgoing[block];
    internal bool IsReachable(int block) => ReachableTraversal.Rank[block] >= 0;

    internal int ImmediateDominator(int block) => (dominators ??= ComputeDominators()).Parents[block];

    internal bool Dominates(int dominator, int block)
    {
        if (!IsReachable(dominator) || !IsReachable(block)) return false;
        var tree = dominators ??= ComputeDominators();
        return tree.Entry[dominator] <= tree.Entry[block] && tree.Exit[block] <= tree.Exit[dominator];
    }

    private sealed record Traversal(int[] Order, int[] Rank);

    private Traversal ReachableTraversal => traversal ??= ComputeTraversal();

    private Traversal ComputeTraversal()
    {
        if (Blocks.Count == 0) return new([], []);
        var order = new int[Blocks.Count];
        int first = order.Length;
        var seen = new bool[Blocks.Count];
        var pending = new Stack<(int Block, int Next)>();
        seen[0] = true;
        pending.Push((0, 0));
        while (pending.TryPop(out var current))
        {
            var next = successors[current.Block];
            if (current.Next == next.Length)
            {
                order[--first] = current.Block;
                continue;
            }
            pending.Push((current.Block, current.Next + 1));
            int child = next[current.Next];
            if (seen[child]) continue;
            seen[child] = true;
            pending.Push((child, 0));
        }
        var rank = new int[Blocks.Count];
        Array.Fill(rank, -1);
        for (int index = first; index < order.Length; index++) rank[order[index]] = index - first;
        return new(first == 0 ? order : order[first..], rank);
    }

    private sealed record DomTree(int[] Parents, int[] Entry, int[] Exit);

    private DomTree ComputeDominators()
    {
        int count = Blocks.Count;
        var parents = new int[count];
        var entry = new int[count];
        var exit = new int[count];
        Array.Fill(parents, -1);
        if (count == 0) return new(parents, entry, exit);
        var (order, rank) = ReachableTraversal;

        // Cooper–Harvey–Kennedy: intersect the known dominator chains in RPO.
        parents[0] = 0;
        bool changed;
        do
        {
            changed = false;
            for (int index = 1; index < order.Length; index++)
            {
                int block = order[index];
                int candidate = -1;
                foreach (int pred in predecessors[block])
                {
                    if (parents[pred] < 0) continue;
                    candidate = candidate < 0 ? pred : Intersect(candidate, pred);
                }
                if (parents[block] == candidate) continue;
                parents[block] = candidate;
                changed = true;
            }
        } while (changed);
        parents[0] = -1;

        var child = new int[count];
        var sibling = new int[count];
        Array.Fill(child, -1);
        for (int block = 1; block < count; block++)
        {
            int parent = parents[block];
            if (parent < 0) continue;
            sibling[block] = child[parent];
            child[parent] = block;
        }
        int time = 0;
        for (int block = 0; block >= 0;)
        {
            entry[block] = time++;
            if (child[block] >= 0) { block = child[block]; continue; }
            while (block > 0 && sibling[block] < 0)
            {
                exit[block] = time;
                block = parents[block];
            }
            exit[block] = time;
            block = block == 0 ? -1 : sibling[block];
        }
        return new(parents, entry, exit);

        int Intersect(int left, int right)
        {
            while (left != right)
            {
                if (rank[left] > rank[right]) left = parents[left];
                else right = parents[right];
            }
            return left;
        }
    }
}
