namespace Msync.Core.Native;

// Slots identify storage. These immutable edges identify the values and location
// bindings reaching an instruction, including joins and loop-carried definitions.
internal sealed class NativeValueFlow
{
    private sealed record State(int[][] Values, int[][] Locations, int[][] Iterators)
    {
        internal State Copy() => new((int[][])Values.Clone(), (int[][])Locations.Clone(), (int[][])Iterators.Clone());
    }

    private readonly NativeFunctionCode function;
    private readonly State?[] incoming;
    private readonly NativeControlFlow flow;
    private readonly int[] owners;
    private readonly Dictionary<(int Pc, int Slot, byte Kind), int[]> edges = [];
    internal NativeControlFlow ControlFlow => flow;
    internal int Definition(int pc) => 2 * function.Slots.Count + pc;
    internal int[] Values(int pc, int slot) => Edge(pc, slot, kind: 0);
    internal int[] Locations(int pc, int slot) => Edge(pc, slot, kind: 1);

    internal int[] Iterators(int pc, int iterator) => Edge(pc, iterator, kind: 2);

    internal NativeValueFlow(NativeFunctionCode function)
    {
        this.function = function;
        flow = new(function.Instructions);
        incoming = new State?[flow.Blocks.Count];
        owners = new int[function.Instructions.Count];
        for (int block = 0; block < flow.Blocks.Count; block++)
            Array.Fill(owners, block, flow.Blocks[block].Start, flow.Blocks[block].End - flow.Blocks[block].Start);
        State entry = new(Enumerable.Range(0, function.Slots.Count).Select(slot => new[] { slot }).ToArray(),
            Enumerable.Range(0, function.Slots.Count).Select(slot => new[] { function.Slots.Count + slot }).ToArray(),
            Enumerable.Range(0, function.IteratorCount).Select(_ => Array.Empty<int>()).ToArray());
        State?[] outgoing = new State?[flow.Blocks.Count];
        State?[] failures = new State?[flow.Blocks.Count];
        List<int>[] previous = Enumerable.Range(0, flow.Blocks.Count).Select(_ => new List<int>()).ToArray();
        for (int block = 0; block < flow.Blocks.Count; block++)
            foreach (int successor in flow.Blocks[block].Successors) previous[successor].Add(block);
        bool changed;
        do
        {
            changed = false;
            for (int block = 0; block < flow.Blocks.Count; block++)
            {
                State? state = block == 0 ? entry.Copy() : null;
                foreach (int predecessor in previous[block])
                {
                    int last = flow.Blocks[predecessor].End - 1;
                    NativeInstruction branch = function.Instructions[last];
                    if (branch.Op == NativeOp.NextIterator && flow.Blocks[block].Start == branch.B && failures[predecessor] is { } failed)
                        state = Merge(state, failed);
                    if ((branch.Op != NativeOp.NextIterator || flow.Blocks[block].Start == last + 1) && outgoing[predecessor] is { } source)
                        state = Merge(state, source);
                }
                if (state is null) continue;
                NativeControlFlow.Block body = flow.Blocks[block];
                incoming[block] = state.Copy();
                State? failure = null;
                for (int pc = body.Start; pc < body.End; pc++)
                {
                    if (function.Instructions[pc].Op == NativeOp.NextIterator) failure = state.Copy();
                    Transfer(pc, state);
                }
                bool sameFailure = failure is null ? failures[block] is null : failures[block] is { } oldFailure && Same(oldFailure, failure);
                if (outgoing[block] is { } old && Same(old, state) && sameFailure) continue;
                outgoing[block] = state; failures[block] = failure;
                changed = true;
            }
        } while (changed);
        for (int block = 0; block < flow.Blocks.Count; block++)
        {
            if (incoming[block] is not { } start) continue;
            State state = start.Copy();
            NativeControlFlow.Block body = flow.Blocks[block];
            for (int pc = body.Start; pc < body.End; pc++)
            {
                NativeInstruction instruction = function.Instructions[pc];
                IEnumerable<int> used = NativeInstructionFacts.Reads(instruction).Concat(NativeInstructionFacts.Definitions(instruction));
                foreach (int slot in used.Where(slot => slot >= 0).Distinct())
                {
                    edges[(pc, slot, 0)] = state.Values[slot];
                    edges[(pc, slot, 1)] = state.Locations[slot];
                }
                if (instruction.Op is NativeOp.NextIterator or NativeOp.CloseIterator)
                    edges[(pc, instruction.A, 2)] = state.Iterators[instruction.A];
                Transfer(pc, state);
            }
        }
    }

    private int[] Edge(int pc, int slot, byte kind)
    {
        if (edges.TryGetValue((pc, slot, kind), out int[]? result)) return result;
        int block = owners[pc];
        if (incoming[block] is not { } start) return [];
        // Most queries are actual instruction uses, prelinked above. Ad-hoc diagnostic
        // queries replay one immutable block instead of retaining a whole frame per PC.
        State state = start.Copy();
        for (int at = flow.Blocks[block].Start; at < pc; at++) Transfer(at, state);
        result = kind == 2 ? state.Iterators[slot] : kind == 1 ? state.Locations[slot] : state.Values[slot];
        edges[(pc, slot, kind)] = result;
        return result;
    }

    private void Transfer(int pc, State state)
    {
        NativeInstruction instruction = function.Instructions[pc];
        if (instruction.Op == NativeOp.OpenIterator) state.Iterators[instruction.B] = [Definition(pc)];
        if (instruction.Op == NativeOp.CloseIterator) state.Iterators[instruction.A] = [];
        if (instruction.Op == NativeOp.BindLocation)
        {
            state.Locations[instruction.A] = state.Values[instruction.B];
            state.Values[instruction.A] = [Definition(pc)];
        }
        if (instruction.Op is NativeOp.Move or NativeOp.Drop) state.Values[instruction.A] = [Definition(pc)];
        if (instruction.Result < 0) return;
        state.Values[instruction.Result] = [Definition(pc)];
    }

    private static State Merge(State? destination, State source)
    {
        if (destination is null) return source.Copy();
        for (int slot = 0; slot < destination.Values.Length; slot++)
        {
            destination.Values[slot] = destination.Values[slot].Union(source.Values[slot]).Order().ToArray();
            destination.Locations[slot] = destination.Locations[slot].Union(source.Locations[slot]).Order().ToArray();
        }
        for (int iterator = 0; iterator < destination.Iterators.Length; iterator++)
            destination.Iterators[iterator] = destination.Iterators[iterator].Union(source.Iterators[iterator]).Order().ToArray();
        return destination;
    }

    private static bool Same(State left, State right) => left.Values.Zip(right.Values).All(pair => pair.First.SequenceEqual(pair.Second))
        && left.Locations.Zip(right.Locations).All(pair => pair.First.SequenceEqual(pair.Second))
        && left.Iterators.Zip(right.Iterators).All(pair => pair.First.SequenceEqual(pair.Second));
}
