namespace Msync.Optimization;

internal static class ScopeScalarization
{
    private sealed class Field(int slot, Reference value, int epoch)
    {
        internal readonly int Slot = slot;
        internal Reference Value = value;
        internal int WrittenAt = epoch;
    }

    private sealed class Storage(Reference value, int epoch, bool captured = false)
    {
        internal Reference Value = value;
        internal int WrittenAt = epoch;
        internal bool Captured = captured;
        internal bool UnknownAlias;
    }

    private sealed class Allocation(int position, IReadOnlyDictionary<string, Field> fields)
    {
        internal readonly int Position = position;
        internal readonly IReadOnlyDictionary<string, Field> Fields = fields;
        internal bool Escaped;
        internal int EscapedAt;
    }

    private sealed record Reference(Allocation? Scope = null, Storage? Cell = null, IReadOnlyList<Reference>? Captures = null);
    private sealed record Read(Allocation Allocation, Field Field);
    private sealed record Write(Allocation Allocation, Field Field);
    private sealed record Block(int Start, int End, int[] Successors);

    internal static FunctionCode Optimize(FunctionCode function)
    {
        if (!function.Instructions.Any(instruction => instruction.Op == OpCode.MakeScope)) return function;
        // A scalar slot represents one instance only while its allocation stays
        // within this CFG region. Live-out aliases keep the physical instance.
        var blocks = Blocks(function.Instructions);
        var liveOut = Liveness(function.Instructions, blocks);
        var capturedAtEntry = CapturedEntries(function, blocks);
        var allocations = new Dictionary<int, Allocation>();
        var reads = new Dictionary<int, Read>();
        var writes = new Dictionary<int, Write>();
        var localsCount = function.LocalCount;
        foreach (var block in blocks)
        {
            var locals = new Dictionary<int, Storage>();
            var stack = new List<Reference>();
            var epoch = 0;
            Storage Local(int slot)
            {
                if (locals.TryGetValue(slot, out var storage)) return storage;
                storage = new(new(), epoch, capturedAtEntry is not null && capturedAtEntry[block.Start].Contains(slot)); locals.Add(slot, storage); return storage;
            }
            Reference ReadStorage(Storage storage) => storage.Captured && storage.WrittenAt < epoch ? new() : storage.Value;
            Reference Payload(Reference value) => value.Cell is { } cell ? ReadStorage(cell) : value;
            Reference Pop()
            {
                // Incoming stack values are opaque across scalarization regions.
                if (stack.Count == 0) return new();
                var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value;
            }
            Reference[] PopMany(int count)
            {
                var values = new Reference[count];
                for (var index = count - 1; index >= 0; index--) values[index] = Pop();
                return values;
            }
            void Escape(Reference value)
            {
                if (Payload(value).Scope is not { Escaped: false } scope) return;
                scope.Escaped = true; scope.EscapedAt = epoch;
            }
            // Unknown callbacks can change escaped instances and captured cells.
            // Epochs invalidate those facts without scanning every prior object.
            void Invalidate() => epoch++;
            bool Known(Allocation allocation, Field field) => !allocation.Escaped || epoch <= Math.Max(allocation.EscapedAt, field.WrittenAt);
            for (var pc = block.Start; pc < block.End; pc++)
            {
                var instruction = function.Instructions[pc];
                switch (instruction.Op)
                {
                    case OpCode.Nop: case OpCode.Barrier: break;
                    case OpCode.GetSingleton: stack.Add(new()); break;
                    case OpCode.SetSingleton: Escape(Pop()); stack.Add(new()); break;
                    case OpCode.Constant:
                    case OpCode.Null:
                    case OpCode.True:
                    case OpCode.False:
                    case OpCode.LoadBuiltin:
                        stack.Add(new()); break;
                    case OpCode.LoadLocal: stack.Add(ReadStorage(Local(instruction.A))); break;
                    case OpCode.LoadStorage:
                        var raw = Local(instruction.A);
                        stack.Add(raw.Captured ? new(Cell: raw) : raw.Value); break;
                    case OpCode.LoadCell:
                        var captured = Local(instruction.A);
                        // Earlier calls cannot rebind a location before its first capture.
                        var capturedValue = ReadStorage(captured);
                        captured.Captured = true;
                        Escape(capturedValue); stack.Add(new(Cell: captured)); break;
                    case OpCode.StoreLocal:
                        var stored = Pop(); var targetStorage = Local(instruction.A);
                        if (targetStorage.UnknownAlias) Invalidate();
                        targetStorage.Value = stored; targetStorage.WrittenAt = epoch;
                        if (targetStorage.Captured) Escape(stored);
                        break;
                    case OpCode.InitializeLocal:
                        var initialized = Pop(); locals[instruction.A] = initialized.Cell ?? new(initialized, epoch);
                        break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup:
                        var duplicated = Pop(); stack.Add(duplicated); stack.Add(duplicated); break;
                    case OpCode.MakeScope:
                        var values = PopMany(instruction.A);
                        foreach (var value in values) Escape(value);
                        var names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                        var fields = new Dictionary<string, Field>(names.Length, StringComparer.Ordinal);
                        bool unique = true;
                        for (var index = 0; index < names.Length; index++)
                            if (!fields.TryAdd(names[index], new(localsCount++, values[index], epoch))) { unique = false; break; }
                        if (!unique) { stack.Add(new()); break; }
                        var allocation = new Allocation(pc, fields);
                        allocations.Add(pc, allocation); stack.Add(new(allocation)); break;
                    case OpCode.GetMember:
                        var receiver = Pop();
                        if (receiver.Scope is { } readScope && readScope.Fields.TryGetValue(instruction.Text!, out var field))
                        {
                            var known = Known(readScope, field);
                            if (known) reads.Add(pc, new(readScope, field));
                            else Escape(receiver);
                            stack.Add(known ? Payload(field.Value) : new());
                        }
                        else { Escape(receiver); stack.Add(new()); }
                        break;
                    case OpCode.SetMember:
                        var assigned = Pop(); var destination = Pop();
                        if (destination.Scope is { } writeScope && writeScope.Fields.TryGetValue(instruction.Text!, out var target))
                        {
                            Escape(assigned);
                            if (target.Value.Cell is { UnknownAlias: true }) Invalidate();
                            target.WrittenAt = epoch;
                            if (target.Value.Cell is { } cell) { cell.Value = assigned; cell.WrittenAt = epoch; }
                            else target.Value = assigned;
                            writes.Add(pc, new(writeScope, target));
                        }
                        else { Escape(destination); Escape(assigned); Invalidate(); }
                        stack.Add(new()); break;
                    case OpCode.MakeClosure:
                        var captures = PopMany(instruction.B);
                        foreach (var value in captures) Escape(value);
                        stack.Add(new(Captures: captures)); break;
                    case OpCode.GetCapture:
                        var closure = Pop();
                        stack.Add(closure.Captures is { } knownCaptures && instruction.A < knownCaptures.Count
                            ? knownCaptures[instruction.A] : new(Cell: new(new(), epoch, captured: true) { UnknownAlias = true }));
                        break;
                    case OpCode.SpecializeClosure:
                        var specialized = Pop();
                        if (instruction.A < 0) { Escape(specialized); stack.Add(new()); }
                        else
                            stack.Add(specialized);
                        break;
                    case OpCode.Call:
                    case OpCode.ProcessCall:
                        foreach (var value in PopMany(instruction.A + 1)) Escape(value);
                        Invalidate(); stack.Add(new()); break;
                    case OpCode.NativeCall:
                        foreach (var value in PopMany(instruction.A)) Escape(value);
                        if ((InstructionEffects.NativeEffects(instruction.Text) & InstructionEffect.Invoke) != 0) Invalidate();
                        stack.Add(new()); break;
                    case OpCode.MakeRange:
                    case OpCode.MakeLazyRange:
                        foreach (var value in PopMany(instruction.A)) Escape(value);
                        stack.Add(new()); break;
                    case OpCode.Acquire:
                        if (instruction.A != 0) { Escape(Pop()); stack.Add(new()); }
                        Invalidate(); break;
                    case OpCode.IteratorNext: stack.Add(new()); Invalidate(); break;
                    case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Escape(Pop()); break;
                    case OpCode.Jump: break;
                    case OpCode.Return: if (stack.Count > 0) Escape(Pop()); break;
                    case OpCode.GetIterator:
                    case OpCode.Not:
                    case OpCode.IsNull:
                    case OpCode.Negate:
                    case OpCode.Convert:
                    case OpCode.SelectPath:
                    case OpCode.DeferRange:
                        Escape(Pop()); stack.Add(new()); break;
                    case OpCode.RegexMatch:
                        foreach (var value in PopMany(instruction.Text is null ? 2 : 1)) Escape(value);
                        stack.Add(new()); break;
                    default:
                        foreach (var value in PopMany(2)) Escape(value);
                        stack.Add(new()); break;
                }
            }
            foreach (var value in stack) Escape(value);
            foreach (var slot in liveOut[block.Start]) if (locals.TryGetValue(slot, out var value)) Escape(ReadStorage(value));
        }
        var forwarded = reads.Values.Select(read => read.Allocation.Position).ToHashSet();
        allocations = allocations.Where(pair => !pair.Value.Escaped || forwarded.Contains(pair.Key)).ToDictionary();
        if (allocations.Count == 0) return function;
        reads = reads.Where(pair => allocations.ContainsKey(pair.Value.Allocation.Position)).ToDictionary();
        writes = writes.Where(pair => allocations.ContainsKey(pair.Value.Allocation.Position)).ToDictionary();
        return Rewrite(function, allocations, reads, writes);
    }

    private static FunctionCode Rewrite(FunctionCode function, Dictionary<int, Allocation> allocations,
        Dictionary<int, Read> reads, Dictionary<int, Write> writes)
    {
        var code = function.Instructions;
        var positions = new int[code.Count + 1];
        var output = new List<Instruction>();
        var branches = new List<int>();
        var slots = allocations.Values.SelectMany(allocation => allocation.Fields.Values.Select(field => field.Slot))
            .Select((slot, index) => (slot, index))
            .ToDictionary(pair => pair.slot, pair => function.LocalCount + pair.index);
        for (var pc = 0; pc < code.Count; pc++)
        {
            positions[pc] = output.Count;
            var instruction = code[pc];
            if (allocations.TryGetValue(pc, out var allocation))
            {
                foreach (var field in allocation.Fields.Values.Reverse()) output.Add(new(OpCode.InitializeLocal, slots[field.Slot], Span: instruction.Span));
                if (allocation.Escaped)
                {
                    foreach (var field in allocation.Fields.Values) output.Add(new(OpCode.LoadStorage, slots[field.Slot], Span: instruction.Span));
                    output.Add(instruction);
                }
                else output.Add(new(OpCode.Null, Span: instruction.Span));
                continue;
            }
            if (reads.TryGetValue(pc, out var read))
            {
                output.Add(new(OpCode.Pop, Span: instruction.Span));
                output.Add(new(OpCode.LoadLocal, slots[read.Field.Slot], Span: instruction.Span));
                continue;
            }
            if (writes.TryGetValue(pc, out var write))
            {
                if (write.Allocation.Escaped) output.Add(new(OpCode.Dup, Span: instruction.Span));
                output.Add(new(OpCode.StoreLocal, slots[write.Field.Slot], Span: instruction.Span));
                output.Add(write.Allocation.Escaped ? instruction : new(OpCode.Pop, Span: instruction.Span));
                if (!write.Allocation.Escaped) output.Add(new(OpCode.Null, Span: instruction.Span));
                continue;
            }
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(output.Count);
            output.Add(instruction);
        }
        positions[^1] = output.Count;
        foreach (var branch in branches)
        {
            var instruction = output[branch];
            output[branch] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] } : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = output, LocalCount = function.LocalCount + slots.Count };
    }

    private static Block[] Blocks(IReadOnlyList<Instruction> code)
    {
        var starts = new SortedSet<int> { 0, code.Count };
        for (var pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) starts.Add(instruction.A);
            if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext or OpCode.Return) starts.Add(pc + 1);
        }
        var boundaries = starts.ToArray();
        return boundaries.Zip(boundaries.Skip(1), (start, end) =>
        {
            var last = code[end - 1];
            int[] next = last.Op switch
            {
                OpCode.Return => [],
                OpCode.Jump => [last.A],
                OpCode.JumpIfFalse or OpCode.JumpIfTrue => [last.A, end],
                OpCode.IteratorNext => [last.B, end],
                _ => end < code.Count ? [end] : []
            };
            return new Block(start, end, next.Where(target => target < code.Count).Distinct().ToArray());
        }).ToArray();
    }

    private static Dictionary<int, HashSet<int>> Liveness(IReadOnlyList<Instruction> code, IReadOnlyList<Block> blocks)
    {
        var uses = new Dictionary<int, HashSet<int>>();
        var definitions = new Dictionary<int, HashSet<int>>();
        var inputs = blocks.ToDictionary(block => block.Start, _ => new HashSet<int>());
        var outputs = blocks.ToDictionary(block => block.Start, _ => new HashSet<int>());
        var predecessors = blocks.ToDictionary(block => block.Start, _ => new List<int>());
        foreach (var block in blocks)
        {
            var use = new HashSet<int>(); var define = new HashSet<int>();
            for (var pc = block.Start; pc < block.End; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.IteratorNext && !define.Contains(instruction.A)) use.Add(instruction.A);
                if (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal) define.Add(instruction.A);
            }
            uses.Add(block.Start, use); definitions.Add(block.Start, define);
            foreach (var successor in block.Successors) predecessors[successor].Add(block.Start);
        }
        var work = new Queue<int>(blocks.Select(block => block.Start).Reverse());
        var queued = blocks.Select(block => block.Start).ToHashSet();
        var byStart = blocks.ToDictionary(block => block.Start);
        while (work.TryDequeue(out var start))
        {
            queued.Remove(start);
            var output = new HashSet<int>();
            foreach (var successor in byStart[start].Successors) output.UnionWith(inputs[successor]);
            outputs[start] = output;
            var input = new HashSet<int>(output); input.ExceptWith(definitions[start]); input.UnionWith(uses[start]);
            if (inputs[start].SetEquals(input)) continue;
            inputs[start] = input;
            foreach (var predecessor in predecessors[start]) if (queued.Add(predecessor)) work.Enqueue(predecessor);
        }
        return outputs;
    }

    private static Dictionary<int, HashSet<int>>? CapturedEntries(FunctionCode function, IReadOnlyList<Block> blocks)
    {
        if (function.CaptureCount == 0 && !function.Instructions.Any(instruction => instruction.Op == OpCode.LoadCell)) return null;
        var inputs = blocks.ToDictionary(block => block.Start, _ => new HashSet<int>());
        inputs[0].UnionWith(Enumerable.Range(0, function.CaptureCount));
        var byStart = blocks.ToDictionary(block => block.Start);
        var work = new Queue<int>(); work.Enqueue(0);
        var queued = new HashSet<int> { 0 };
        var reached = new HashSet<int> { 0 };
        while (work.TryDequeue(out var start))
        {
            queued.Remove(start);
            var block = byStart[start];
            var captured = new HashSet<int>(inputs[start]);
            for (var pc = block.Start; pc < block.End; pc++)
            {
                var instruction = function.Instructions[pc];
                if (instruction.Op == OpCode.LoadCell) captured.Add(instruction.A);
                // Initializing a lexical identity replaces the slot, not a prior cell's value.
                if (instruction.Op == OpCode.InitializeLocal) captured.Remove(instruction.A);
            }
            foreach (var successor in block.Successors)
            {
                var input = inputs[successor];
                int previous = input.Count;
                input.UnionWith(captured);
                if ((reached.Add(successor) || input.Count != previous) && queued.Add(successor)) work.Enqueue(successor);
            }
        }
        return inputs;
    }
}
