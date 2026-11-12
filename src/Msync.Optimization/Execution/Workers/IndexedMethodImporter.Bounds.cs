using System.Collections.Immutable;
using static Msync.Optimization.Execution.IndexedMethodPlan;

namespace Msync.Optimization.Execution;

internal sealed partial class IndexedMethodImporter
{
    // Index arithmetic is exact in the guarded Count range. Comparisons
    // establish selected bounds; loop-carried index facts must be inductive
    // at every backedge, not inferred from the first iteration.
    private readonly record struct Ordinal(int Root, int Scale, int Bias)
    {
        public bool Constant => Scale == 0;

        public Ordinal? Add(Ordinal other)
        {
            if (!Constant && !other.Constant && Root != other.Root) return null;
            long scale = (long)Scale + other.Scale;
            long bias = (long)Bias + other.Bias;
            if (scale > int.MaxValue || bias > int.MaxValue) return null;
            return new(Constant ? other.Root : Root, (int)scale, (int)bias);
        }
    }

    private sealed class Bounds(int count)
    {
        internal Ordinal?[] Values { get; } = new Ordinal?[count];
        internal Dictionary<(int Root, int Scale), int> Below { get; } = [];

        internal Bounds Copy()
        {
            Bounds copy = new(Values.Length);
            Values.CopyTo(copy.Values, 0);
            foreach (var pair in Below) copy.Below.Add(pair.Key, pair.Value);
            return copy;
        }

        internal void Merge(Bounds incoming, Func<int> nextRoot)
        {
            List<Ordinal>? boundedJoins = null;
            for (int i = 0; i < Values.Length; i++)
            {
                if (Values[i] == incoming.Values[i]) continue;
                if (Values[i] is { } left && incoming.Values[i] is { } right
                    && Allows(left) && incoming.Allows(right))
                {
                    Ordinal merged = new(nextRoot(), 1, 0);
                    Values[i] = merged;
                    (boundedJoins ??= []).Add(merged);
                }
                else Values[i] = null;
            }
            foreach (var key in Below.Keys.ToArray())
            {
                if (incoming.Below.TryGetValue(key, out int bias)) Below[key] = Math.Min(Below[key], bias);
                else Below.Remove(key);
            }
            if (boundedJoins is not null)
                foreach (var merged in boundedJoins) Limit(merged);
        }

        internal void Limit(Ordinal value)
        {
            var key = (value.Root, value.Scale);
            Below[key] = Math.Max(Below.GetValueOrDefault(key, -1), value.Bias);
        }

        internal bool Allows(Ordinal value) => Below.GetValueOrDefault((value.Root, value.Scale), -1) >= value.Bias;
    }

    private static HashSet<int> Reach(int start, Func<int, IEnumerable<int>> edges)
    {
        HashSet<int> visited = [];
        Stack<int> pending = new();
        pending.Push(start);
        while (pending.TryPop(out int pc))
        {
            if (!visited.Add(pc)) continue;
            foreach (int next in edges(pc)) pending.Push(next);
        }
        return visited;
    }

    private bool ProveScalarRows(NativeWorkerPlan plan, ImmutableArray<Input> planInputs, out int maxRows)
    {
        maxRows = int.MaxValue;
        Bounds entry = new(plan.Slots.Length);
        const int CountRoot = -2;
        for (int i = 0; i < planInputs.Length; i++)
        {
            if (planInputs[i].Type != NativeWorkerType.Index) continue;
            int slot = plan.Parameters[i];
            bool size = accesses[planInputs[i].Access].Kind == AccessKind.Count;
            var value = new Ordinal(size ? CountRoot : slot, 1, 0);
            entry.Values[slot] = value;
            if (!size) entry.Limit(value);
        }

        var code = plan.Instructions;
        var states = new Bounds?[code.Length];
        states[0] = entry;
        var targets = new HashSet<int>();
        var headers = new HashSet<int>();
        var incoming = Enumerable.Range(0, code.Length).Select(_ => new List<int>(2)).ToArray();
        var outgoing = new int[code.Length][];
        var loopAssumptions = new Dictionary<int, int[]>();
        int syntheticRoot = -3;
        int NewRoot() => syntheticRoot--;
        for (int pc = 0; pc < code.Length; pc++)
        {
            var edge = code[pc];
            outgoing[pc] = edge.Op switch
            {
                NativeWorkerOp.Return => [],
                NativeWorkerOp.Jump => [edge.Data],
                NativeWorkerOp.BranchIfFalse or NativeWorkerOp.BranchIfTrue => [edge.Data, pc + 1],
                _ => [pc + 1]
            };
            foreach (int successor in outgoing[pc])
            {
                if ((uint)successor >= (uint)code.Length) return false;
                incoming[successor].Add(pc);
            }
            if (edge.Op is not (NativeWorkerOp.Jump or NativeWorkerOp.BranchIfFalse or NativeWorkerOp.BranchIfTrue)) continue;
            targets.Add(edge.Data);
            if (edge.Data <= pc) headers.Add(edge.Data);
        }
        var loopWrites = new Dictionary<int, HashSet<int>>();
        foreach (int header in headers)
        {
            HashSet<int> component = Reach(header, pc => outgoing[pc]);
            component.IntersectWith(Reach(header, pc => incoming[pc]));
            var writes = new HashSet<int>();
            foreach (int pc in component)
                if (code[pc] is { Op: NativeWorkerOp.Move or NativeWorkerOp.IndexConstant or NativeWorkerOp.IndexAdd,
                        Destination: var written } && plan.Slots[written] == NativeWorkerType.Index)
                    writes.Add(written);
            loopWrites.Add(header, writes);
        }
        for (int pc = 0; pc < code.Length; pc++)
        {
            if (states[pc] is not { } state) continue;
            if (loopWrites.TryGetValue(pc, out var writes))
            {
                List<int> assumed = [];
                foreach (int slot in writes)
                {
                    if (state.Values[slot] is not { } old || !state.Allows(old))
                    {
                        state.Values[slot] = null;
                        continue;
                    }
                    Ordinal induction = new(NewRoot(), 1, 0);
                    state.Values[slot] = induction;
                    state.Limit(induction);
                    assumed.Add(slot);
                }
                loopAssumptions.Add(pc, [.. assumed]);
            }
            var op = code[pc];
            switch (op.Op)
            {
                case NativeWorkerOp.Return:
                    continue;
                case NativeWorkerOp.Jump:
                    if (!Publish(pc, op.Data, state)) return false;
                    continue;
                case NativeWorkerOp.BranchIfTrue or NativeWorkerOp.BranchIfFalse:
                    var alternate = state.Copy();
                    if (pc > 0 && !targets.Contains(pc))
                    {
                        var comparison = code[pc - 1];
                        if (comparison.Destination == op.Left
                            && comparison.Op is NativeWorkerOp.IndexLess or NativeWorkerOp.IndexGreaterEqual
                            && state.Values[comparison.Right] is { Root: CountRoot, Scale: 1, Bias: 0 }
                            && state.Values[comparison.Left] is { } candidate)
                        {
                            bool onTaken = (op.Op == NativeWorkerOp.BranchIfTrue) == (comparison.Op == NativeWorkerOp.IndexLess);
                            (onTaken ? alternate : state).Limit(candidate);
                        }
                    }
                    if (!Publish(pc, op.Data, alternate)) return false;
                    break;
                case NativeWorkerOp.LoadNumber or NativeWorkerOp.StoreNumber:
                    if (state.Values[op.Left] is not { } ordinal || !state.Allows(ordinal)) return false;
                    break;
                case NativeWorkerOp.Move when plan.Slots[op.Destination] == NativeWorkerType.Index:
                    state.Values[op.Destination] = state.Values[op.Left];
                    break;
                case NativeWorkerOp.IndexConstant:
                    state.Values[op.Destination] = new(-1, 0, op.Data);
                    break;
                case NativeWorkerOp.IndexAdd:
                    if (state.Values[op.Left] is not { } left || state.Values[op.Right] is not { } right
                        || left.Add(right) is not { } added) return false;
                    state.Values[op.Destination] = added;
                    if (!added.Constant)
                    {
                        long limit = ((long)int.MaxValue - added.Bias) / added.Scale + (added.Root == CountRoot ? 0 : 1);
                        maxRows = (int)Math.Min(maxRows, limit);
                    }
                    break;
            }
            if (!Publish(pc, pc + 1, state)) return false;
        }
        return true;

        bool Publish(int pc, int next, Bounds state)
        {
            if ((uint)next >= (uint)states.Length) return false;
            if (next <= pc)
                return loopAssumptions.TryGetValue(next, out var assumptions)
                    && assumptions.All(slot => state.Values[slot] is { } value && state.Allows(value));
            if (states[next] is { } prior) prior.Merge(state, NewRoot);
            else states[next] = state.Copy();
            return true;
        }
    }
}
