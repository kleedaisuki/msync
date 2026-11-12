namespace Msync.Optimization.Execution;

/// <summary>
/// Process-local representation of the operands named by one bound site.
/// Operand positions are in push order, not stack-depth order.
/// </summary>
[Flags]
public enum WordTransport : byte
{
    None = 0,
    First = 1,
    Second = 2,
    Third = 4,
    Fourth = 8,
    Result = 16,
}

internal sealed class WordTransportPlan
{
    private readonly WordTransport[] lanes;
    private readonly int[]? localAt;
    private WordTransportPlan(WordTransport[] lanes, int[]? localAt) =>
        (this.lanes, this.localAt) = (lanes, localAt);

    internal bool HasLocals => localAt is not null;
    internal WordTransport At(int pc) => lanes[pc];
    internal int LocalAt(int pc) => localAt?[pc] ?? -1;

    internal static WordTransportPlan? Find(Module image, int function, Instruction[] bound, ExecutionRegion? region)
    {
        if (!bound.Any(ProducesWord) || !bound.Any(instruction => AcceptsPrimitive(instruction, 1)
            || AcceptsPrimitive(instruction, 2))) return null;
        if (region is null && !ExecutionRegionImporter.TryImport(image, function, out region, out _)) return null;

        var body = image.Functions[function];
        var definitions = new ExecutionOperation?[region.ValueCount];
        var uses = new Use[region.ValueCount];
        var parameters = new List<ExecutionValueId>?[region.ValueCount];
        var localReads = new List<ExecutionOperation>?[body.LocalCount];
        var localWrites = new List<ExecutionOperation>?[body.LocalCount];
        var aliased = new bool[body.LocalCount];
        foreach (var block in region.Blocks)
        {
            foreach (var operation in block.Operations)
            {
                if (operation.Result is { } value) definitions[value.Index] = operation;
                for (int i = 0; i < operation.Inputs.Length; i++) uses[operation.Inputs[i].Index].Add(operation.Pc, i);
                int slot = operation.Instruction.A;
                switch (operation.Instruction.Op)
                {
                    case OpCode.LoadLocal: (localReads[slot] ??= []).Add(operation); break;
                    case OpCode.StoreLocal or OpCode.InitializeLocal: (localWrites[slot] ??= []).Add(operation); break;
                    case OpCode.LoadCell or OpCode.LoadStorage or OpCode.IteratorNext: aliased[slot] = true; break;
                }
            }
            foreach (var edge in block.Successors)
            {
                if (edge.Arguments.Depth == 0) continue;
                // A stack edge retains the canonical public representation.
                // Its parameter can still prove that every incoming value is
                // an exact word, e.g. a store immediately following a join.
                var incoming = new ExecutionValueId[edge.Arguments.Depth];
                region.CopyStack(edge.Arguments, incoming);
                var target = region.BlockAt(edge.TargetPc);
                for (int i = 0; i < incoming.Length; i++)
                {
                    uses[incoming[i].Index].Escape();
                    if (target is not null)
                        (parameters[target.Parameters[i].Index] ??= []).Add(incoming[i]);
                }
            }
        }

        int firstLocal = body.CaptureCount + body.ParameterCount;
        var localWords = new bool[body.LocalCount];
        SparseDefUse? reaching = null;
        for (int slot = firstLocal; slot < localWords.Length; slot++)
        {
            if (aliased[slot] || localWrites[slot] is not { } writes || localReads[slot] is not { } reads)
                continue;
            // O0 declarations intentionally create a private Null before
            // running the initializer. That write is not a word definition
            // if no load can see it, but its selected producer still runs.
            reaching ??= new(image, body, new(body));
            DefinitionSet definitionsAtReads = default;
            foreach (var read in reads)
                definitionsAtReads = definitionsAtReads.Union(reaching.DefinitionsBefore(read.Pc, slot));
            if (definitionsAtReads.Count == 0 || definitionsAtReads.Contains(SparseDefUse.Entry(slot))) continue;
            writes.RemoveAll(write => !definitionsAtReads.Contains(SparseDefUse.Output(write.Pc)));
            localWords[slot] = writes.Count != 0;
        }

        var words = new bool[region.ValueCount];
        for (int i = 0; i < words.Length; i++)
            words[i] = parameters[i] is not null || definitions[i] is { } op
                && (ProducesWord(bound[op.Pc]) || op.Instruction.Op == OpCode.LoadLocal && localWords[op.Instruction.A]);
        // Compute the greatest consistent subset. Reachable cycles are
        // rooted in a real incoming producer or eliminated by an opaque input.
        Prune();

        int[]? localAt = null;
        for (int slot = firstLocal; slot < localWords.Length; slot++)
        {
            if (!localWords[slot]) continue;
            // Do not allocate a word local whose uses all immediately demand
            // ordinary Number. F64 already transports that case.
            if (!localReads[slot]!.Any(read => read.Result is { } result && UsesWord(result.Index)))
            {
                localWords[slot] = false;
                continue;
            }
            localAt ??= Enumerable.Repeat(-1, bound.Length).ToArray();
            foreach (var load in localReads[slot]!) localAt[load.Pc] = slot;
            foreach (var store in localWrites[slot]!) localAt[store.Pc] = slot;
        }

        WordTransport[]? lanes = null;
        foreach (var block in region.Blocks)
        {
            int[]? barrier = null;
            foreach (var operation in block.Operations)
            {
                if (operation.Result is not { } result || !words[result.Index] || uses[result.Index] is not { Unique: true } use
                    || use.Pc <= operation.Pc || use.Pc >= block.StartPc + block.Operations.Length
                    || !Accepts(use.Pc, use.Operand)) continue;
                if (operation.Instruction.Op == OpCode.LoadLocal && localAt?[operation.Pc] is null or < 0) continue;
                barrier ??= BarrierPrefix(block);
                if (barrier[use.Pc - block.StartPc] != barrier[operation.Pc - block.StartPc + 1]) continue;
                lanes ??= new WordTransport[bound.Length];
                lanes[operation.Pc] |= WordTransport.Result;
                lanes[use.Pc] |= (WordTransport)(1 << use.Operand);
            }
        }
        return lanes is null && localAt is null ? null : new(lanes ?? new WordTransport[bound.Length], localAt);

        void Prune()
        {
            bool changed;
            do
            {
                changed = false;
                for (int slot = firstLocal; slot < localWords.Length; slot++)
                    if (localWords[slot] && localWrites[slot]!.Any(write => !words[write.Inputs[0].Index]))
                    {
                        localWords[slot] = false;
                        changed = true;
                    }
                for (int i = 0; i < words.Length; i++)
                {
                    if (!words[i]) continue;
                    if (parameters[i] is { } inputs && inputs.Any(input => !words[input.Index])
                        || definitions[i] is { Instruction.Op: OpCode.LoadLocal } load && !localWords[load.Instruction.A])
                    {
                        words[i] = false;
                        changed = true;
                    }
                }
            } while (changed);
        }

        bool UsesWord(int result) => uses[result] is { Unique: true } use
            && (AcceptsPrimitive(bound[use.Pc], use.Operand)
                || body.Instructions[use.Pc] is { Op: OpCode.StoreLocal or OpCode.InitializeLocal } store && localWords[store.A]);
        bool Accepts(int pc, int operand) => AcceptsPrimitive(bound[pc], operand)
            || localAt?[pc] >= 0 && body.Instructions[pc].Op is OpCode.StoreLocal or OpCode.InitializeLocal;
        int[] BarrierPrefix(ExecutionBlock block)
        {
            var prefix = new int[block.Operations.Length + 1];
            for (int i = 0; i < block.Operations.Length; i++)
            {
                var op = block.Operations[i];
                var physical = bound[op.Pc].Op;
                bool transparent = physical is ExecutionTargets.NumericPrimitiveCall or ExecutionTargets.ArenaPrimitiveCall
                    || op.Instruction.Op is OpCode.LoadLocal or OpCode.Constant or OpCode.Null or OpCode.True or OpCode.False
                        or OpCode.Nop or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Pop or OpCode.Dup;
                prefix[i + 1] = prefix[i] + (transparent ? 0 : 1);
            }
            return prefix;
        }
    }

    private static bool ProducesWord(Instruction instruction) =>
        instruction.Op == ExecutionTargets.NumericPrimitiveCall
            && new ExecutionNumericOperation((NumericIntrinsic)instruction.B, instruction.A).Representation == NumericRepresentation.Word32
        || instruction.Op == ExecutionTargets.ArenaPrimitiveCall && (ArenaOperation)instruction.B is
            ArenaOperation.Root or ArenaOperation.Child or ArenaOperation.Parent or ArenaOperation.Allocate;

    private static bool AcceptsPrimitive(Instruction instruction, int operand)
    {
        if (instruction.Op == ExecutionTargets.NumericPrimitiveCall)
            return new ExecutionNumericOperation((NumericIntrinsic)instruction.B, instruction.A).Representation == NumericRepresentation.Word32;
        if (instruction.Op != ExecutionTargets.ArenaPrimitiveCall) return false;
        return (ArenaOperation)instruction.B switch
        {
            ArenaOperation.Allocate => operand == 2,
            ArenaOperation.SetChild => operand is 1 or 3,
            ArenaOperation.SetParent => operand is 1 or 2,
            ArenaOperation.Key or ArenaOperation.Child or ArenaOperation.Parent or ArenaOperation.Red or ArenaOperation.Release
                or ArenaOperation.SetRoot or ArenaOperation.SetRed => operand == 1,
            _ => false
        };
    }

    private struct Use
    {
        private int count;
        internal int Pc;
        internal int Operand;
        internal readonly bool Unique => count == 1;
        internal void Add(int pc, int operand)
        {
            count++;
            Pc = pc;
            Operand = operand;
        }
        internal void Escape() => count += 2;
    }
}
