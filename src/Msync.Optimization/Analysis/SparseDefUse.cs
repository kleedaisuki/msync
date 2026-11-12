namespace Msync.Optimization;

internal readonly record struct DefinitionUse(int Instruction, int Operand);

/// <summary>May-reaching operand definitions for one immutable function body.</summary>
internal sealed class SparseDefUse
{
    private readonly record struct Site(int Start, int Count);
    private readonly record struct StackExit(int Prefix, int[] Suffix)
    {
        internal int Depth => Prefix + Suffix.Length;
        internal int At(int column) => column < Prefix ? -column - 1 : Suffix[column - Prefix];
    }

    private readonly FunctionCode body;
    private readonly Site[] sites;
    private readonly StackExit[] exits;
    private readonly int[] symbols;
    private readonly DefinitionSet[] operands;
    private readonly bool[] resolved;
    private readonly Dictionary<int, List<int>> writes = [];
    private readonly Dictionary<(int Block, int Key), DefinitionSet> localEntries = [];
    private readonly Dictionary<(int Block, int Key), DefinitionSet> stackEntries = [];
    private readonly uint[] visited;
    private readonly Stack<int> work = new();
    private uint epoch;
    private Dictionary<int, List<DefinitionUse>>? uses;

    internal SparseDefUse(Module module, FunctionCode body, ControlFlowGraph graph)
    {
        this.body = body;
        Graph = graph;
        sites = new Site[body.Instructions.Count];
        exits = new StackExit[graph.Blocks.Count];
        visited = new uint[exits.Length];
        resolved = new bool[sites.Length];
        symbols = ProjectStack(module);
        operands = new DefinitionSet[symbols.Length];
        for (int pc = 0; pc < sites.Length; pc++)
        {
            var instruction = body.Instructions[pc];
            if (instruction.Op is not (OpCode.StoreLocal or OpCode.InitializeLocal)) continue;
            if (!writes.TryGetValue(instruction.A, out var positions))
                writes.Add(instruction.A, positions = []);
            positions.Add(pc);
        }
    }

    internal ControlFlowGraph Graph { get; }
    internal bool Describes(FunctionCode function) => Graph.Describes(function)
        && function.LocalCount == body.LocalCount;

    // Nonnegative definitions name the instruction and output. A negative
    // local entry token names an unknown incoming value (including raw Cell).
    internal static int Output(int pc, int output = 0) => pc * 2 + output;
    internal static int Entry(int slot) => -slot - 1;
    internal bool IsReachable(int pc)
    {
        int block = Graph.BlockAt(pc);
        return block >= 0 && Graph.IsReachable(block);
    }
    internal int StackInputCount(int pc) => HasLocalOperand(body.Instructions[pc].Op) ? 0 : sites[pc].Count;

    internal ReadOnlySpan<DefinitionSet> Inputs(int pc) => InputMemory(pc).Span;

    internal ReadOnlyMemory<DefinitionSet> InputMemory(int pc)
    {
        var site = sites[pc];
        if (site.Count == 0) return default;
        if (!resolved[pc])
        {
            var instruction = body.Instructions[pc];
            if (HasLocalOperand(instruction.Op))
                operands[site.Start] = DefinitionsBefore(pc, instruction.A);
            else
            {
                int block = Graph.BlockAt(pc);
                for (int index = site.Start; index < site.Start + site.Count; index++)
                {
                    int symbol = symbols[index];
                    operands[index] = symbol >= 0 ? new(symbol) : ResolveEntry(block, -symbol - 1, local: false);
                }
            }
            resolved[pc] = true;
        }
        return operands.AsMemory(site.Start, site.Count);
    }

    // A write defines the slot transport, not a private payload. Clients
    // must separately prove that dereferencing the slot cannot observe alias
    // writes, closure effects, or a change to raw storage identity.
    internal DefinitionSet DefinitionsBefore(int pc, int slot)
    {
        int block = Graph.BlockAt(pc);
        if (block < 0 || !Graph.IsReachable(block)) return default;
        if (!writes.TryGetValue(slot, out var stores)) return new(Entry(slot));
        int writer = Writer(block, pc, stores);
        return writer >= 0 ? new(Output(writer)) : ResolveEntry(block, slot, local: true);
    }

    internal IReadOnlyList<DefinitionUse> Uses(int definition)
    {
        var index = uses ??= IndexUses();
        return index.TryGetValue(definition, out var found) ? found : [];
    }

    private Dictionary<int, List<DefinitionUse>> IndexUses()
    {
        Dictionary<int, List<DefinitionUse>> index = [];
        for (int pc = 0; pc < sites.Length; pc++)
        {
            var inputs = Inputs(pc);
            for (int operand = 0; operand < inputs.Length; operand++)
                foreach (int definition in inputs[operand])
                {
                    if (!index.TryGetValue(definition, out var found)) index.Add(definition, found = []);
                    found.Add(new(pc, operand));
                }
        }
        return index;
    }

    private int Writer(int block, int before, List<int> stores)
    {
        int position = stores.BinarySearch(before);
        position = (position < 0 ? ~position : position) - 1;
        return position >= 0 && stores[position] >= Graph.Blocks[block].Start ? stores[position] : -1;
    }

    private DefinitionSet ResolveEntry(int block, int key, bool local)
    {
        var cache = local ? localEntries : stackEntries;
        if (cache.TryGetValue((block, key), out var known)) return known;
        var stores = local ? writes[key] : null;
        if (++epoch == 0)
        {
            Array.Clear(visited);
            epoch = 1;
        }
        DefinitionSet result = default;
        work.Push(block);
        while (work.TryPop(out int at))
        {
            if (visited[at] == epoch) continue;
            visited[at] = epoch;
            if (cache.TryGetValue((at, key), out var previous))
            {
                result = result.Union(previous);
                continue;
            }
            if (local && at == 0) result = result.Union(new(Entry(key)));
            foreach (var edge in Graph.IncomingEdges(at))
            {
                int predecessor = edge.Source;
                if (!Graph.IsReachable(predecessor)) continue;
                if (local)
                {
                    int writer = Writer(predecessor, Graph.Blocks[predecessor].End, stores!);
                    if (writer >= 0) result = result.Union(new(Output(writer)));
                    else work.Push(predecessor);
                    continue;
                }
                var exit = exits[predecessor];
                int depth = exit.Depth - (edge.Kind == ControlFlowEdgeKind.IteratorExhausted ? 1 : 0);
                if (key >= depth) continue;
                int symbol = exit.At(key);
                if (symbol >= 0) result = result.Union(new(symbol));
                else work.Push(predecessor);
            }
        }
        cache.Add((block, key), result);
        return result;
    }

    private int[] ProjectStack(Module module)
    {
        if (exits.Length == 0) return [];
        List<int> symbols = [];
        List<int> tail = [];
        int[] depths = new int[exits.Length];
        Array.Fill(depths, -1);
        depths[0] = 0;
        Queue<int> pending = new();
        pending.Enqueue(0);
        while (pending.TryDequeue(out int block))
        {
            int prefix = depths[block];
            tail.Clear();
            var range = Graph.Blocks[block];
            for (int pc = range.Start; pc < range.End; pc++)
            {
                var instruction = body.Instructions[pc];
                if (!ExecutionStackEffect.TryGet(module, instruction, prefix + tail.Count, out var effect))
                    throw new InvalidOperationException($"No stack effect for {instruction.Op}.");
                int consumed = effect.Inputs;
                int start = symbols.Count;
                if (HasLocalOperand(instruction.Op))
                {
                    sites[pc] = new(start, 1);
                    symbols.Add(0);
                }
                else if (consumed != 0)
                {
                    sites[pc] = new(start, consumed);
                    int startColumn = prefix + tail.Count - consumed;
                    for (int column = startColumn; column < prefix; column++) symbols.Add(-column - 1);
                    int tailStart = Math.Max(0, startColumn - prefix);
                    for (int index = tailStart; index < tail.Count; index++) symbols.Add(tail[index]);
                    tail.RemoveRange(tailStart, tail.Count - tailStart);
                    prefix = Math.Min(prefix, startColumn);
                }
                int pushed = instruction.Op == OpCode.Dup ? 2 : effect.ProducesValue ? 1 : 0;
                for (int output = 0; output < pushed; output++) tail.Add(Output(pc, output));
            }
            var exit = exits[block] = new(prefix, tail.ToArray());
            foreach (var edge in Graph.OutgoingEdges(block))
            {
                if (edge.Target < 0 || depths[edge.Target] >= 0) continue;
                depths[edge.Target] = exit.Depth - (edge.Kind == ControlFlowEdgeKind.IteratorExhausted ? 1 : 0);
                pending.Enqueue(edge.Target);
            }
        }
        return symbols.ToArray();
    }

    private static bool HasLocalOperand(OpCode op) => op is OpCode.LoadLocal or OpCode.LoadCell
        or OpCode.LoadStorage or OpCode.IteratorNext;

}
