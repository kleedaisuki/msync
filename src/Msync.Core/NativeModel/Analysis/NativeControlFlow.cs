
namespace Msync.Core.Native;

internal sealed class NativeControlFlow
{
    internal sealed record Block(int Start, int End, int[] Successors);
    internal IReadOnlyList<Block> Blocks { get; }
    internal int[][] Successors { get; }
    private bool[]? cyclic;
    internal bool[] Cyclic => cyclic ??= FindCycles(Successors);

    internal NativeControlFlow(IReadOnlyList<NativeInstruction> code)
    {
        Successors = Enumerable.Range(0, code.Count).Select(pc => Next(code, pc)).ToArray();
        Blocks = FormBlocks(code, Successors);
    }

    private static int[] Next(IReadOnlyList<NativeInstruction> code, int pc)
    {
        NativeInstruction instruction = code[pc];
        int[] candidates = instruction.Op switch
        {
            NativeOp.Return or NativeOp.Unreachable => [],
            NativeOp.Jump => [instruction.A],
            NativeOp.Branch => [instruction.B, instruction.C],
            NativeOp.NextIterator => [pc + 1, instruction.B],
            _ => [pc + 1]
        };
        foreach (int target in candidates)
            if (target < 0 || target > code.Count)
                throw new InvalidOperationException($"Native control-flow target {target} at {pc} is outside 0..{code.Count}.");
        return candidates.Where(target => target < code.Count).Distinct().ToArray();
    }

    private static Block[] FormBlocks(IReadOnlyList<NativeInstruction> code, int[][] next)
    {
        if (code.Count == 0) return [];
        SortedSet<int> leaders = [0];
        for (int pc = 0; pc < code.Count; pc++)
        {
            if (code[pc].Op is not (NativeOp.Jump or NativeOp.Branch or NativeOp.Return or NativeOp.Unreachable or NativeOp.NextIterator)) continue;
            foreach (int target in next[pc]) leaders.Add(target);
            if (pc + 1 < code.Count) leaders.Add(pc + 1);
        }
        int[] starts = leaders.ToArray();
        int[] owners = new int[code.Count];
        for (int block = 0; block < starts.Length; block++)
            Array.Fill(owners, block, starts[block], (block + 1 < starts.Length ? starts[block + 1] : code.Count) - starts[block]);
        return starts.Select((start, block) =>
        {
            int end = block + 1 < starts.Length ? starts[block + 1] : code.Count;
            return new Block(start, end, next[end - 1].Select(pc => owners[pc]).Distinct().ToArray());
        }).ToArray();
    }

    private static bool[] FindCycles(int[][] next)
    {
        List<int>[] previous = Enumerable.Range(0, next.Length).Select(_ => new List<int>()).ToArray();
        for (int pc = 0; pc < next.Length; pc++)
            foreach (int target in next[pc]) previous[target].Add(pc);
        List<int> order = [];
        bool[] visited = new bool[next.Length];
        for (int start = 0; start < next.Length; start++) Finish(start, next, visited, order);
        Array.Clear(visited);
        bool[] cyclic = new bool[next.Length];
        foreach (int start in order.AsEnumerable().Reverse())
        {
            if (visited[start]) continue;
            List<int> component = [];
            Stack<int> pending = new(); pending.Push(start); visited[start] = true;
            while (pending.TryPop(out int current))
            {
                component.Add(current);
                foreach (int predecessor in previous[current])
                    if (!visited[predecessor]) { visited[predecessor] = true; pending.Push(predecessor); }
            }
            if (component.Count > 1 || next[start].Contains(start))
                foreach (int member in component) cyclic[member] = true;
        }
        return cyclic;
    }

    private static void Finish(int start, int[][] next, bool[] visited, List<int> order)
    {
        if (visited[start]) return;
        Stack<(int Node, bool Done)> pending = new(); pending.Push((start, false));
        while (pending.TryPop(out var item))
        {
            if (item.Done) { order.Add(item.Node); continue; }
            if (visited[item.Node]) continue;
            visited[item.Node] = true; pending.Push((item.Node, true));
            foreach (int target in next[item.Node]) if (!visited[target]) pending.Push((target, false));
        }
    }
}
