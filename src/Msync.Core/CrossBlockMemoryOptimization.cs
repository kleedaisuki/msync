namespace Msync.Core;

internal static partial class CrossBlockMemoryOptimization
{
    private readonly record struct Reference(DefinitionSet Origins, bool Cell = false, bool Opaque = false,
        TypeKind Kinds = TypeKind.Never, int Closure = 0, int CellSlot = 0, bool Eager = false, int Function = 0)
    {
        internal Reference Union(Reference other)
        {
            if (Kinds == TypeKind.Never && Origins.Count == 0 && !Cell && !Opaque) return other;
            if (other.Kinds == TypeKind.Never && other.Origins.Count == 0 && !other.Cell && !other.Opaque) return this;
            return new(Origins.Union(other.Origins), Cell || other.Cell, Opaque || other.Opaque, Kinds | other.Kinds,
                Closure == other.Closure ? Closure : 0, CellSlot == other.CellSlot ? CellSlot : 0, Eager && other.Eager,
                Function == other.Function ? Function : -1);
        }
        internal bool Same(Reference other)
        {
            if (Cell != other.Cell || Opaque != other.Opaque || Origins.Count != other.Origins.Count || Kinds != other.Kinds
                || Closure != other.Closure || CellSlot != other.CellSlot || Eager != other.Eager || Function != other.Function) return false;
            foreach (var origin in Origins) if (!other.Origins.Contains(origin)) return false;
            return true;
        }
        internal int Single
        {
            get
            {
                if (Cell || Opaque || Origins.Count != 1) return -1;
                var iterator = Origins.GetEnumerator(); iterator.MoveNext(); return iterator.Current;
            }
        }
    }

    private sealed record State(Reference[] Locals, List<Reference> Stack)
    {
        internal State Copy() => new((Reference[])Locals.Clone(), [.. Stack]);
    }

    private sealed class Block(int start, int end)
    {
        internal readonly int Start = start;
        internal readonly int End = end;
        internal readonly List<int> Successors = [];
        internal readonly List<int> Predecessors = [];
        internal State? Input;
        internal bool Cyclic;
    }

    private sealed class Allocation(int position, string[] names)
    {
        internal readonly int Position = position;
        internal readonly string[] Names = names;
        internal readonly Dictionary<string, int> Fields = names.Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        internal readonly Dictionary<string, Reference> Contents = new(StringComparer.Ordinal);
        internal bool Escaped;
        internal bool SharedFields;
        internal int FirstSlot;
        internal bool Promoted;
    }

    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants)
        => Optimize(module, function, constants, null);

    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants,
        IReadOnlyList<FunctionMemoryEffects>? summaries)
    {
        _ = constants;
        if (module.PreserveLocals
            || !function.Instructions.Any(instruction => instruction.Op == OpCode.MakeScope)) return function;
        return new Analysis(module, function, summaries).Optimize();
    }

    private sealed partial class Analysis(Module module, FunctionCode function, IReadOnlyList<FunctionMemoryEffects>? summaries)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly Dictionary<int, Allocation> allocations = [];
        private readonly Dictionary<int, Reference> receivers = [];
        private readonly Dictionary<(int Origin, string Field), HashSet<int>> readers = [];
        private readonly Dictionary<int, Reference[]> closures = [];
        private readonly Dictionary<int, Reference[]> calls = [];
        private readonly Dictionary<int, Reference> stored = [];
        private readonly Dictionary<int, HashSet<int>> cellAliases = [];
        private readonly HashSet<int> cellSlots = [];
        private readonly Dictionary<int, int> targets = FunctionInlining.Targets(function, module.Constants, true, module.Functions);
        private readonly HashSet<int> captured = function.Instructions.Where(instruction => instruction.Op == OpCode.LoadCell)
            .Select(instruction => instruction.A).Concat(Enumerable.Range(0, function.CaptureCount)).ToHashSet();
        private Block[] blocks = [];
        private int[] blockAt = [];
        private bool[] queued = [];
        private readonly Queue<int> work = new();

        internal FunctionCode Optimize()
        {
            if (code.Count == 0) return function;
            BuildBlocks();
            MarkCycles();
            DiscoverAllocations();
            if (allocations.Count == 0) return function;
            cellSlots.UnionWith(captured);
            var initial = new Reference[function.LocalCount];
            for (var slot = 0; slot < function.CaptureCount + function.ParameterCount; slot++) initial[slot] = new(default,
                Cell: slot < function.CaptureCount, Opaque: true,
                Kinds: slot < function.CaptureCount ? TypeKind.Any : function.ParameterTypes?[slot - function.CaptureCount].Kind ?? TypeKind.Any);
            blocks[0].Input = new(initial, []);
            Enqueue(0);
            while (work.TryDequeue(out var index))
            {
                queued[index] = false;
                Transfer(index);
            }
            foreach (var (position, receiver) in receivers)
            {
                if (receiver.Single is var origin && allocations.TryGetValue(origin, out var allocation)
                    && allocation.Fields.ContainsKey(code[position].Text!)) continue;
                Escape(receiver);
            }
            foreach (var allocation in allocations.Values)
                allocation.Escaped |= allocation.SharedFields;
            PropagateEscapes();
            var promoted = allocations.Where(pair => !pair.Value.Escaped).ToDictionary();
            foreach (var allocation in promoted.Values) allocation.Promoted = true;
            var forwarded = DiscoverForwarding();
            var used = forwarded.Values.ToHashSet();
            var tracked = allocations.Where(pair => !pair.Value.SharedFields && (pair.Value.Promoted || used.Contains(pair.Key))).ToDictionary();
            if (tracked.Count == 0) return function;
            return Rewrite(tracked, forwarded);
        }

        private void BuildBlocks()
        {
            var starts = new SortedSet<int> { 0, code.Count };
            for (var pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) starts.Add(instruction.A);
                if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext or OpCode.Return)
                    starts.Add(pc + 1);
            }
            var positions = starts.ToArray();
            blocks = positions.Zip(positions.Skip(1), (start, end) => new Block(start, end)).ToArray();
            blockAt = new int[code.Count + 1];
            queued = new bool[blocks.Length];
            for (var index = 0; index < blocks.Length; index++)
                for (var pc = blocks[index].Start; pc < blocks[index].End; pc++) blockAt[pc] = index;
            blockAt[^1] = blocks.Length;
            for (var index = 0; index < blocks.Length; index++)
            {
                var block = blocks[index];
                var instruction = code[block.End - 1];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) Edge(instruction.A);
                if (instruction.Op == OpCode.IteratorNext) Edge(instruction.B);
                if (instruction.Op is not (OpCode.Jump or OpCode.Return)) Edge(block.End);

                void Edge(int target)
                {
                    if (target == code.Count) return;
                    var next = blockAt[target];
                    if (block.Successors.Contains(next)) return;
                    block.Successors.Add(next); blocks[next].Predecessors.Add(index);
                }
            }
        }

        private void MarkCycles()
        {
            var seen = new bool[blocks.Length];
            var order = new List<int>();
            for (var root = 0; root < blocks.Length; root++)
            {
                if (seen[root]) continue;
                var pending = new Stack<(int Block, bool Exit)>(); pending.Push((root, false));
                while (pending.TryPop(out var item))
                {
                    if (item.Exit) { order.Add(item.Block); continue; }
                    if (seen[item.Block]) continue;
                    seen[item.Block] = true; pending.Push((item.Block, true));
                    foreach (var successor in blocks[item.Block].Successors)
                        if (!seen[successor]) pending.Push((successor, false));
                }
            }
            Array.Clear(seen);
            for (var position = order.Count - 1; position >= 0; position--)
            {
                var root = order[position];
                if (seen[root]) continue;
                var component = new List<int>(); var pending = new Stack<int>(); pending.Push(root); seen[root] = true;
                while (pending.TryPop(out var index))
                {
                    component.Add(index);
                    foreach (var previous in blocks[index].Predecessors)
                        if (!seen[previous]) { seen[previous] = true; pending.Push(previous); }
                }
                if (component.Count > 1 || blocks[root].Successors.Contains(root))
                    foreach (var index in component) blocks[index].Cyclic = true;
            }
        }

        private void DiscoverAllocations()
        {
            for (var pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op != OpCode.MakeScope || blocks[blockAt[pc]].Cyclic) continue;
                var names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (names.Length != instruction.A || names.Distinct(StringComparer.Ordinal).Count() != names.Length) continue;
                allocations.Add(pc, new(pc, names));
            }
        }

        private void Escape(Reference value)
        {
            foreach (var origin in value.Origins)
                if (allocations.TryGetValue(origin, out var allocation)) allocation.Escaped = true;
        }

        private void Receiver(int pc, Reference value) => receivers[pc] = receivers.TryGetValue(pc, out var previous) ? previous.Union(value) : value;

        private void Content(Allocation owner, string field, Reference value)
        {
            var joined = owner.Contents.TryGetValue(field, out var previous) ? previous.Union(value) : value;
            if (owner.Contents.ContainsKey(field) && joined.Same(previous)) return;
            owner.Contents[field] = joined;
            if (readers.TryGetValue((owner.Position, field), out var waiting))
                foreach (var block in waiting) Enqueue(block);
        }

        private Reference ReadField(Reference receiver, string field, int block)
        {
            if (!allocations.TryGetValue(receiver.Single, out var owner) || !owner.Fields.ContainsKey(field))
                return new(default, Opaque: true, Kinds: TypeKind.Any);
            if (!readers.TryGetValue((owner.Position, field), out var waiting)) readers.Add((owner.Position, field), waiting = []);
            waiting.Add(block);
            return owner.Contents.TryGetValue(field, out var value)
                ? value with { Cell = false, Opaque = value.Opaque || value.Cell }
                : default;
        }

        private void PropagateEscapes()
        {
            var work = new Queue<Allocation>(allocations.Values.Where(allocation => allocation.Escaped));
            while (work.TryDequeue(out var owner))
                foreach (var value in owner.Contents.Values)
                    foreach (var origin in value.Origins)
                        if (allocations.TryGetValue(origin, out var child) && !child.Escaped)
                        { child.Escaped = true; work.Enqueue(child); }
        }

        private void Enqueue(int index)
        {
            if (queued[index]) return;
            queued[index] = true; work.Enqueue(index);
        }

        private void Merge(int target, State incoming)
        {
            if (target == code.Count) return;
            var index = blockAt[target];
            if (blocks[index].Input is not { } previous) { blocks[index].Input = incoming.Copy(); Enqueue(index); return; }
            var changed = false;
            for (var slot = 0; slot < incoming.Locals.Length; slot++)
            {
                var joined = previous.Locals[slot].Union(incoming.Locals[slot]);
                if (joined.Same(previous.Locals[slot])) continue;
                previous.Locals[slot] = joined; changed = true;
            }
            for (var slot = 0; slot < incoming.Stack.Count; slot++)
            {
                var joined = previous.Stack[slot].Union(incoming.Stack[slot]);
                if (joined.Same(previous.Stack[slot])) continue;
                previous.Stack[slot] = joined; changed = true;
            }
            if (changed) Enqueue(index);
        }

        private void Transfer(int index)
        {
            var state = blocks[index].Input!.Copy();
            Reference Pop() { var value = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return value; }
            Reference[] PopMany(int count)
            {
                var result = new Reference[count];
                for (var item = count - 1; item >= 0; item--) result[item] = Pop();
                return result;
            }
            void Consume(int count) { for (var item = 0; item < count; item++) Escape(Pop()); }
            void Unknown(int consumed, bool cell = false, TypeKind kinds = TypeKind.Any) { Consume(consumed); state.Stack.Add(new(default, cell, Opaque: true, Kinds: kinds)); }
            for (var pc = blocks[index].Start; pc < blocks[index].End; pc++)
            {
                var instruction = code[pc];
                switch (instruction.Op)
                {
                    case OpCode.Nop: case OpCode.Barrier: break;
                    case OpCode.GetSingleton: Unknown(0, kinds: TypeKind.Scope | TypeKind.Null); break;
                    case OpCode.SetSingleton: Unknown(1, kinds: TypeKind.Scope); break;
                    case OpCode.Constant:
                        var literal = module.Constants[instruction.A];
                        state.Stack.Add(new(default, Opaque: true, Kinds: literal.Type.Kind,
                            Closure: literal is FunctionValue ? -instruction.A - 1 : 0,
                            Function: literal is FunctionValue literalFunction ? literalFunction.Function : -1)); break;
                    case OpCode.Null: state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Null)); break;
                    case OpCode.True:
                    case OpCode.False: state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Bool)); break;
                    case OpCode.LoadBuiltin: state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Connection)); break;
                    case OpCode.LoadLocal:
                        var loaded = state.Locals[instruction.A]; state.Stack.Add(loaded with { Cell = false }); break;
                    case OpCode.LoadCell:
                        var capturedValue = state.Locals[instruction.A]; Escape(capturedValue);
                        state.Locals[instruction.A] = capturedValue with { Cell = true, CellSlot = instruction.A + 1 };
                        state.Stack.Add(state.Locals[instruction.A]); cellSlots.Add(instruction.A); break;
                    case OpCode.LoadStorage:
                        var storage = state.Locals[instruction.A];
                        bool raw = storage.Cell;
                        if (raw) Escape(storage);
                        state.Stack.Add(storage with { Cell = raw }); break;
                    case OpCode.StoreLocal:
                        var stored = Pop();
                        if (captured.Contains(instruction.A) || state.Locals[instruction.A].Cell) Escape(stored);
                        RememberStorage(instruction.A, stored);
                        if (state.Locals[instruction.A].Cell) InvalidateBindings(state, instruction.A);
                        state.Locals[instruction.A] = stored with { Cell = state.Locals[instruction.A].Cell || stored.Cell };
                        if (state.Locals[instruction.A].Cell) cellSlots.Add(instruction.A);
                        break;
                    case OpCode.InitializeLocal:
                        var initialized = Pop();
                        if (captured.Contains(instruction.A)) Escape(initialized);
                        RememberStorage(instruction.A, initialized);
                        state.Locals[instruction.A] = initialized;
                        if (initialized.Cell) cellSlots.Add(instruction.A);
                        break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup: var duplicate = Pop(); state.Stack.Add(duplicate); state.Stack.Add(duplicate); break;
                    case OpCode.MakeScope:
                        var fields = PopMany(instruction.A);
                        if (allocations.TryGetValue(pc, out var allocation))
                        {
                            allocation.SharedFields |= fields.Any(field => field.Cell);
                            for (var field = 0; field < fields.Length; field++) Content(allocation, allocation.Names[field], fields[field]);
                            state.Stack.Add(new(new(pc), Kinds: TypeKind.Scope));
                        }
                        else
                        {
                            foreach (var field in fields) Escape(field);
                            state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Scope));
                        }
                        break;
                    case OpCode.GetMember:
                        var receiver = Pop(); Receiver(pc, receiver); state.Stack.Add(ReadField(receiver, instruction.Text!, index)); break;
                    case OpCode.SetMember:
                        var assigned = Pop(); var destination = Pop(); Receiver(pc, destination);
                        if (allocations.TryGetValue(destination.Single, out var owner) && owner.Fields.ContainsKey(instruction.Text!))
                        { Content(owner, instruction.Text!, assigned); owner.SharedFields |= assigned.Cell; }
                        else Escape(assigned);
                        state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Null)); break;
                    case OpCode.MakeClosure:
                        var captures = PopMany(instruction.B);
                        foreach (var capture in captures) Escape(capture);
                        Remember(closures, pc + 1, captures);
                        state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Function, Closure: pc + 1, Function: instruction.A)); break;
                    case OpCode.Call:
                    case OpCode.ProcessCall:
                        var arguments = PopMany(instruction.A + 1); Remember(calls, pc, arguments);
                        foreach (var argument in arguments) Escape(argument);
                        var callResult = CallResult(pc, arguments);
                        if (MayRebind(pc, arguments)) InvalidateBindings(state);
                        state.Stack.Add(callResult); break;
                    case OpCode.NativeCall:
                        var nativeArguments = PopMany(instruction.A); Remember(calls, pc, nativeArguments);
                        foreach (var argument in nativeArguments) Escape(argument);
                        bool invoke = NativeInvokes(instruction.Text, nativeArguments);
                        if (invoke) InvalidateBindings(state);
                        var signature = NativeSignatures.TryGet(instruction.Text!, out var nativeSignature) ? nativeSignature : null;
                        state.Stack.Add(new(default, Opaque: true, Kinds: signature?.Result.Kind ?? TypeKind.Any,
                            Eager: EagerNative(instruction.Text, signature, nativeArguments))); break;
                    case OpCode.MakeRange:
                    case OpCode.MakeLazyRange:
                        Consume(instruction.A); state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Range, Eager: instruction.Op == OpCode.MakeRange)); break;
                    case OpCode.GetCapture: Unknown(1, cell: true); break;
                    case OpCode.SpecializeClosure:
                        var callable = Pop();
                        state.Stack.Add(callable.Closure == 0 || callable.Function < 0 ? callable
                            : callable with
                            {
                                Function = instruction.Text is null ? instruction.A
                                : FunctionDispatch.Parse(instruction.Text).GetValueOrDefault(callable.Function, callable.Function)
                            }); break;
                    case OpCode.GetIterator:
                        var range = Pop(); Enumeration(pc, range);
                        if (MayEnumerate(range)) InvalidateBindings(state);
                        state.Stack.Add(range); break;
                    case OpCode.Not: case OpCode.IsNull: Unknown(1, kinds: TypeKind.Bool); break;
                    case OpCode.Negate: Unknown(1, kinds: TypeKind.Number); break;
                    case OpCode.Convert: Unknown(1, kinds: instruction.Text == "Path" ? TypeKind.Path : TypeKind.String); break;
                    case OpCode.SelectPath: Unknown(1, kinds: TypeKind.Range); break;
                    case OpCode.DeferRange: Unknown(1, kinds: TypeKind.Range); break;
                    case OpCode.Transfer:
                    case OpCode.LocalTransfer:
                        var destinationPath = Pop(); var source = Pop(); Escape(source); Escape(destinationPath);
                        Enumeration(pc, source);
                        if (MayEnumerate(source)) InvalidateBindings(state);
                        state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Bool)); break;
                    case OpCode.RegexMatch: Unknown(instruction.Text is null ? 2 : 1); break;
                    case OpCode.Acquire: if (instruction.A != 0) Unknown(1); break;
                    case OpCode.IteratorNext:
                        Enumeration(pc, state.Locals[instruction.A]);
                        if (!state.Locals[instruction.A].Eager) InvalidateBindings(state);
                        Merge(instruction.B, state);
                        state.Stack.Add(new(default, Opaque: true, Kinds: TypeKind.Any)); break;
                    case OpCode.JumpIfTrue:
                    case OpCode.JumpIfFalse:
                        Escape(Pop()); Merge(instruction.A, state); break;
                    case OpCode.Jump: Merge(instruction.A, state); return;
                    case OpCode.Return: if (state.Stack.Count > 0) Escape(Pop()); return;
                    default: Unknown(2); break;
                }
            }
            Merge(blocks[index].End, state);
        }

        private FunctionCode Rewrite(Dictionary<int, Allocation> promoted, Dictionary<int, int> forwarded)
        {
            var slots = function.LocalCount;
            foreach (var allocation in promoted.Values) { allocation.FirstSlot = slots; slots += allocation.Names.Length; }
            var output = new List<Instruction>(); var positions = new int[code.Count + 1]; var branches = new List<int>();
            for (var pc = 0; pc < code.Count; pc++)
            {
                positions[pc] = output.Count;
                var instruction = code[pc];
                if (promoted.TryGetValue(pc, out var allocation))
                {
                    for (var field = allocation.Names.Length - 1; field >= 0; field--)
                        output.Add(new(OpCode.InitializeLocal, allocation.FirstSlot + field, Span: instruction.Span));
                    if (allocation.Promoted) output.Add(new(OpCode.Null, Span: instruction.Span));
                    else
                    {
                        for (var field = 0; field < allocation.Names.Length; field++)
                            output.Add(new(OpCode.LoadStorage, allocation.FirstSlot + field, Span: instruction.Span));
                        output.Add(instruction);
                    }
                    continue;
                }
                if (receivers.TryGetValue(pc, out var receiver) && promoted.TryGetValue(receiver.Single, out var owner))
                {
                    var slot = owner.FirstSlot + owner.Fields[instruction.Text!];
                    if (instruction.Op == OpCode.GetMember)
                    {
                        if (!owner.Promoted && !forwarded.ContainsKey(pc)) { output.Add(instruction); continue; }
                        output.Add(new(OpCode.Pop, Span: instruction.Span));
                        output.Add(new(OpCode.LoadLocal, slot, Span: instruction.Span));
                    }
                    else
                    {
                        if (!owner.Promoted) output.Add(new(OpCode.Dup, Span: instruction.Span));
                        output.Add(new(OpCode.StoreLocal, slot, Span: instruction.Span));
                        if (owner.Promoted)
                        {
                            output.Add(new(OpCode.Pop, Span: instruction.Span));
                            output.Add(new(OpCode.Null, Span: instruction.Span));
                        }
                        else output.Add(instruction);
                    }
                    continue;
                }
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(output.Count);
                output.Add(instruction);
            }
            positions[^1] = output.Count;
            foreach (var branch in branches)
            {
                var instruction = output[branch];
                output[branch] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] }
                    : instruction with { A = positions[instruction.A] };
            }
            return function with { Instructions = output, LocalCount = slots };
        }
    }
}
