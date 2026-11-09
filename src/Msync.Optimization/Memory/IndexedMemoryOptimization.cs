using System.Collections.Immutable;

namespace Msync.Optimization;

internal static class IndexedMemoryOptimization
{
    private readonly record struct Atom(int Number, bool Storage = false);
    private readonly record struct Index(int Number, long Constant = 0, bool Known = false);
    private readonly record struct Access(string Operation, int Buffer, Index Index);
    private readonly record struct Read(int Position, Atom Result);
    private readonly record struct Identity(int Kind, int Left, int Right = 0, int Third = 0, string? Name = null);
    private sealed class State(Atom[] locals, List<Atom> stack)
    {
        internal readonly Atom[] Locals = locals;
        internal readonly List<Atom> Stack = stack;
        internal ImmutableDictionary<Access, Read> Reads = ImmutableDictionary<Access, Read>.Empty;
        internal ImmutableDictionary<string, int> Fields = ImmutableDictionary<string, int>.Empty;
        internal int Cells;
        internal int Memory;
        internal State Copy() => new((Atom[])Locals.Clone(), [.. Stack])
        { Reads = Reads, Fields = Fields, Cells = Cells, Memory = Memory };
    }
    private sealed class Block(int start, int end)
    {
        internal readonly int Start = start;
        internal readonly int End = end;
        internal readonly List<int> Predecessors = [];
        internal State? Output;
        internal State? IteratorFailure;
    }

    internal static FunctionCode Optimize(FunctionCode function, IReadOnlyList<Value> constants)
    {
        int gets = 0, sizes = 0;
        bool unknown = false, repeated = false;
        HashSet<long>? indices = null;
        long first = -1;
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            var instruction = function.Instructions[pc];
            if (instruction.Op != OpCode.NativeCall) continue;
            if (instruction.Text == "storage.buffer_size" && ++sizes == 2) return new Analysis(function, constants).Run();
            if (instruction.Text != "storage.buffer_get") continue;
            gets++;
            if (pc == 0 || function.Instructions[pc - 1] is not { Op: OpCode.Constant } literal
                || constants[literal.A] is not NumberValue { Data: var index }
                || !(index >= 0 && index <= int.MaxValue && index == Math.Truncate(index))) unknown = true;
            else if (first < 0) first = (long)index;
            else if (first == index) repeated = true;
            else repeated |= !(indices ??= [first]).Add((long)index);
            if (gets >= 2 && (unknown || repeated)) return new Analysis(function, constants).Run();
        }
        return function;
    }

    private sealed class Analysis(FunctionCode function, IReadOnlyList<Value> constants)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly Dictionary<Identity, int> identities = [];
        private readonly Dictionary<Value, int> literals = new(ValueRepresentation.Comparer);
        private readonly Dictionary<int, long> indices = [];
        private readonly Dictionary<int, int> replacements = [];
        private readonly Dictionary<int, int> retained = [];
        private int nextIdentity = 1;
        private Block[] blocks = [];
        private int[] blockAt = [];

        internal FunctionCode Run()
        {
            BuildBlocks();
            for (int index = 0; index < blocks.Length; index++)
            {
                State? input = Input(index);
                if (input is not null) Transfer(blocks[index], input);
            }
            return replacements.Count == 0 ? function : Rewrite();
        }

        private int Number(Identity identity)
        {
            if (identities.TryGetValue(identity, out int number)) return number;
            identities.Add(identity, number = nextIdentity++);
            return number;
        }

        private Atom Unknown(int position, int operand = 0, bool storage = false) =>
            new(Number(new(0, position, operand)), storage);

        private Atom Literal(Value value)
        {
            if (value is ScopeValue or FunctionValue or RangeValue or CellValue) return new(nextIdentity++);
            if (!literals.TryGetValue(value, out int number)) literals.Add(value, number = nextIdentity++);
            if (value is NumberValue { Data: var index } && index >= 0 && index <= int.MaxValue && index == Math.Truncate(index))
                indices[number] = (long)index;
            return new(number);
        }

        private Atom Dereference(State state, Atom value) => value.Storage
            ? new(Number(new(1, value.Number, state.Cells))) : value;

        private Index Subscript(Atom value) => indices.TryGetValue(value.Number, out long index)
            ? new(0, index, true) : new(value.Number);

        private void BuildBlocks()
        {
            SortedSet<int> starts = [0, code.Count];
            for (int pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) starts.Add(instruction.A);
                if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext or OpCode.Return)
                    starts.Add(pc + 1);
            }
            int[] positions = starts.ToArray();
            blocks = positions.Zip(positions.Skip(1), (start, end) => new Block(start, end)).ToArray();
            blockAt = new int[code.Count + 1];
            for (int index = 0; index < blocks.Length; index++)
                for (int pc = blocks[index].Start; pc < blocks[index].End; pc++) blockAt[pc] = index;
            blockAt[^1] = blocks.Length;
            for (int index = 0; index < blocks.Length; index++)
            {
                Instruction last = code[blocks[index].End - 1];
                if (last.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) Edge(last.A);
                if (last.Op == OpCode.IteratorNext) Edge(last.B);
                if (last.Op is not (OpCode.Jump or OpCode.Return)) Edge(blocks[index].End);
                void Edge(int target)
                {
                    if (target == code.Count) return;
                    var predecessors = blocks[blockAt[target]].Predecessors;
                    if (!predecessors.Contains(index)) predecessors.Add(index);
                }
            }
        }

        private State? Input(int index)
        {
            Block block = blocks[index];
            if (index == 0) return Initial(block.Start);
            var previous = block.Predecessors.Where(parent => parent < index)
                .Select(parent => Incoming(parent, block.Start)).Where(state => state is not null).Cast<State>().ToArray();
            if (previous.Length == 0) return null;
            State result = previous[0].Copy();
            for (int slot = 0; slot < result.Locals.Length; slot++)
                if (previous.Skip(1).Any(state => state.Locals[slot] != result.Locals[slot]))
                    result.Locals[slot] = Unknown(block.Start, slot + 1, previous.Any(state => state.Locals[slot].Storage));
            if (previous.Any(state => state.Stack.Count != result.Stack.Count)) return null;
            for (int item = 0; item < result.Stack.Count; item++)
                if (previous.Skip(1).Any(state => state.Stack[item] != result.Stack[item]))
                    result.Stack[item] = Unknown(block.Start, -item - 1, previous.Any(state => state.Stack[item].Storage));
            if (previous.Skip(1).Any(state => state.Cells != result.Cells)) result.Cells = Number(new(2, block.Start));
            if (previous.Skip(1).Any(state => state.Memory != result.Memory)) result.Memory = Number(new(3, block.Start));
            foreach (string field in previous.SelectMany(state => state.Fields.Keys).Distinct(StringComparer.Ordinal))
                if (previous.Skip(1).Any(state => state.Fields.GetValueOrDefault(field) != result.Fields.GetValueOrDefault(field)))
                    result.Fields = result.Fields.SetItem(field, Number(new(4, block.Start, Name: field)));
            foreach (var entry in result.Reads)
                if (previous.Skip(1).Any(state => !state.Reads.TryGetValue(entry.Key, out var read) || read != entry.Value))
                    result.Reads = result.Reads.Remove(entry.Key);
            // A backedge starts a new dynamic iteration. Reuse within an
            // iteration needs no loop-invariance or speculative-load proof.
            if (block.Predecessors.Any(parent => parent >= index)) ResetLoop(result, block.Start);
            return result;
        }

        private State? Incoming(int parent, int start)
        {
            var block = blocks[parent];
            Instruction last = code[block.End - 1];
            return last.Op == OpCode.IteratorNext && last.B == start ? block.IteratorFailure : block.Output;
        }

        private State Initial(int start) => new(Enumerable.Range(0, function.LocalCount)
            .Select(slot => Unknown(start, slot + 1, slot < function.CaptureCount)).ToArray(), []);

        private void ResetLoop(State state, int start)
        {
            Invalidate(state, start);
            for (int slot = 0; slot < state.Locals.Length; slot++) state.Locals[slot] = Unknown(start, slot + 1, state.Locals[slot].Storage);
            for (int item = 0; item < state.Stack.Count; item++) state.Stack[item] = Unknown(start, -item - 1, state.Stack[item].Storage);
        }

        private void Invalidate(State state, int pc)
        {
            state.Reads = ImmutableDictionary<Access, Read>.Empty;
            state.Cells = Number(new(5, pc));
            state.Memory = Number(new(6, pc));
            state.Fields = ImmutableDictionary<string, int>.Empty;
        }

        private void Transfer(Block block, State state)
        {
            Atom Pop() { var item = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return item; }
            Atom[] Arguments(int count)
            {
                Atom[] values = new Atom[count];
                for (int index = count - 1; index >= 0; index--) values[index] = Pop();
                return values;
            }
            for (int pc = block.Start; pc < block.End; pc++)
            {
                var instruction = code[pc];
                switch (instruction.Op)
                {
                    case OpCode.Nop: case OpCode.Jump: break;
                    case OpCode.Constant: state.Stack.Add(Literal(constants[instruction.A])); break;
                    case OpCode.True: state.Stack.Add(Literal(Value.True)); break;
                    case OpCode.False: state.Stack.Add(Literal(Value.False)); break;
                    case OpCode.Null: state.Stack.Add(Literal(Value.Null)); break;
                    case OpCode.LoadLocal: state.Stack.Add(Dereference(state, state.Locals[instruction.A])); break;
                    case OpCode.LoadStorage: state.Stack.Add(state.Locals[instruction.A]); break;
                    case OpCode.LoadCell:
                        if (!state.Locals[instruction.A].Storage) state.Locals[instruction.A] = Unknown(pc, instruction.A + 1, true);
                        state.Stack.Add(state.Locals[instruction.A]); break;
                    case OpCode.InitializeLocal: state.Locals[instruction.A] = Pop(); break;
                    case OpCode.StoreLocal:
                        Atom stored = Pop();
                        if (state.Locals[instruction.A].Storage) state.Cells = Number(new(7, pc));
                        else state.Locals[instruction.A] = stored;
                        break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup: Atom duplicate = Pop(); state.Stack.Add(duplicate); state.Stack.Add(duplicate); break;
                    case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(); break;
                    case OpCode.Return: if (state.Stack.Count > 0) Pop(); break;
                    case OpCode.GetMember:
                        Atom receiver = Pop();
                        int stamp = Number(new(8, state.Memory, state.Cells,
                            state.Fields.GetValueOrDefault(instruction.Text!), instruction.Text));
                        state.Stack.Add(new(Number(new(80, receiver.Number, stamp))));
                        break;
                    case OpCode.SetMember:
                        Arguments(2);
                        state.Fields = state.Fields.SetItem(instruction.Text!, Number(new(9, pc)));
                        state.Cells = Number(new(7, pc));
                        state.Stack.Add(Literal(Value.Null)); break;
                    case OpCode.NativeCall: Native(state, instruction, pc, Arguments(instruction.A)); break;
                    case OpCode.Call: case OpCode.ProcessCall:
                        Arguments(instruction.A + 1); Invalidate(state, pc); state.Stack.Add(Unknown(pc, storage: true)); break;
                    case OpCode.Barrier: Invalidate(state, pc); break;
                    case OpCode.Acquire:
                        if (instruction.A != 0) { Atom acquired = Pop(); state.Stack.Add(acquired); }
                        Invalidate(state, pc); break;
                    case OpCode.GetIterator:
                        Pop(); Invalidate(state, pc); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.IteratorNext:
                        Invalidate(state, pc); block.IteratorFailure = state.Copy(); state.Stack.Add(Unknown(pc, storage: true)); break;
                    case OpCode.MakeClosure: Arguments(instruction.B); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.MakeScope: case OpCode.MakeRange: case OpCode.MakeLazyRange:
                        Arguments(instruction.A); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.GetCapture: Pop(); state.Stack.Add(Unknown(pc, storage: true)); break;
                    case OpCode.GetSingleton: case OpCode.LoadBuiltin: state.Stack.Add(Unknown(pc)); break;
                    case OpCode.SetSingleton: Pop(); state.Stack.Add(Unknown(pc)); break;
                    case OpCode.Not: case OpCode.IsNull: case OpCode.Negate: case OpCode.Convert:
                    case OpCode.SpecializeClosure: case OpCode.SelectPath: case OpCode.DeferRange:
                        Unary(state, instruction, Pop()); break;
                    case OpCode.RegexMatch when instruction.Text is not null: Pop(); state.Stack.Add(Unknown(pc)); break;
                    default:
                        Atom right = Pop(), left = Pop();
                        if (instruction.Op is OpCode.Transfer or OpCode.LocalTransfer) Invalidate(state, pc);
                        state.Stack.Add(new(Number(new(10 + (int)instruction.Op, left.Number, right.Number, Name: instruction.Text))));
                        break;
                }
            }
            block.Output = state;
        }

        private void Unary(State state, Instruction instruction, Atom input) => state.Stack.Add(new(Number(
            new(10 + (int)instruction.Op, input.Number, Name: instruction.Text))));

        private void Native(State state, Instruction instruction, int pc, Atom[] arguments)
        {
            string name = instruction.Text!;
            if (name is "storage.buffer_get" or "storage.buffer_size")
            {
                var access = new Access(name, Dereference(state, arguments[0]).Number,
                    name == "storage.buffer_get" ? Subscript(Dereference(state, arguments[1])) : default);
                if (state.Reads.TryGetValue(access, out var read))
                {
                    // Keep the first selected read, including its failure span.
                    // An available result proves success on every incoming edge.
                    replacements[pc] = read.Position;
                    retained.TryAdd(read.Position, -1);
                    state.Stack.Add(read.Result);
                }
                else
                {
                    Atom value = Unknown(pc, storage: name == "storage.buffer_get");
                    state.Reads = state.Reads.Add(access, new(pc, value));
                    state.Stack.Add(value);
                }
                return;
            }
            if (name is "storage.buffer_set" or "storage.buffer_swap")
            {
                Index first = Subscript(Dereference(state, arguments[1]));
                Index second = name == "storage.buffer_swap" ? Subscript(Dereference(state, arguments[2])) : first;
                foreach (var entry in state.Reads)
                    if (entry.Key.Operation == "storage.buffer_get" && MayOverlap(entry.Key.Index, first, second))
                        state.Reads = state.Reads.Remove(entry.Key);
            }
            else if (name is "storage.buffer_push" or "storage.buffer_pop" or "storage.buffer_insert" or "storage.buffer_remove" or "storage.buffer_clear")
                state.Reads = ImmutableDictionary<Access, Read>.Empty;
            else if ((InstructionEffects.NativeEffects(name) & (InstructionEffect.Write | InstructionEffect.Invoke)) != 0)
                Invalidate(state, pc);
            bool item = NativeSignatures.TryGet(name, out var signature) && signature.ResultRule == NativeResultRule.RangeItem;
            state.Stack.Add(Unknown(pc, storage: item));
        }

        private static bool MayOverlap(Index read, Index first, Index second) =>
            !read.Known || !first.Known || !second.Known || read.Constant == first.Constant || read.Constant == second.Constant;

        private FunctionCode Rewrite()
        {
            int localCount = function.LocalCount;
            foreach (int position in retained.Keys) retained[position] = localCount++;
            int[] positions = new int[code.Count + 1];
            List<Instruction> output = [];
            List<int> branches = [];
            for (int pc = 0; pc < code.Count; pc++)
            {
                positions[pc] = output.Count;
                Instruction instruction = code[pc];
                if (replacements.TryGetValue(pc, out int producer))
                {
                    for (int argument = 0; argument < instruction.A; argument++) output.Add(new(OpCode.Pop, Span: instruction.Span));
                    output.Add(new(OpCode.LoadStorage, retained[producer], Span: instruction.Span));
                    continue;
                }
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext) branches.Add(output.Count);
                output.Add(instruction);
                if (!retained.TryGetValue(pc, out int slot)) continue;
                output.Add(new(OpCode.Dup, Span: instruction.Span));
                output.Add(new(OpCode.InitializeLocal, slot, Span: instruction.Span));
            }
            positions[^1] = output.Count;
            foreach (int position in branches)
            {
                Instruction instruction = output[position];
                output[position] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] }
                    : instruction with { A = positions[instruction.A] };
            }
            return function with { LocalCount = localCount, Instructions = output };
        }
    }
}
