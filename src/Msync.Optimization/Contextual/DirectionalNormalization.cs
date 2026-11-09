namespace Msync.Optimization;

internal static class DirectionalNormalization
{
    private readonly record struct Transfer(int Position, int Lower, int Upper);

    internal static FunctionCode Optimize(Module module, FunctionCode function)
    {
        var code = function.Instructions;
        var targets = code.Where(i => i.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse).Select(i => i.A)
            .Concat(code.Where(i => i.Op == OpCode.IteratorNext).Select(i => i.B)).ToHashSet();
        var captured = code.Where(i => i.Op == OpCode.LoadCell).Select(i => i.A).ToHashSet();
        captured.UnionWith(Enumerable.Range(0, function.CaptureCount));
        var stack = new List<TypeKind>();
        var locals = new TypeKind[function.LocalCount];
        var moves = new Dictionary<int, List<Instruction>>();
        var preceding = new Dictionary<int, List<Instruction>>();
        var removed = new HashSet<int>();
        var start = 0;
        TypeKind Pop() { if (stack.Count == 0) return TypeKind.Any; var type = stack[^1]; stack.RemoveAt(stack.Count - 1); return type; }
        bool Is(TypeKind actual, TypeKind allowed) => actual != TypeKind.Never && (actual & ~allowed) == 0;
        for (var pc = 0; pc < code.Count; pc++)
        {
            if (targets.Contains(pc)) { Region(start, pc); start = pc; stack.Clear(); Array.Clear(locals); }
            var instruction = code[pc];
            var safe = true;
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Barrier: safe = instruction.A is 1 or 2 && instruction.B == 0 && instruction.Text is null; break;
                case OpCode.Constant:
                    var value = module.Constants[instruction.A]; stack.Add(value.Type.Kind);
                    safe = value is not (ScopeValue or FunctionValue or RangeValue or ProcessValue or CellValue); break;
                case OpCode.Null: stack.Add(TypeKind.Null); break;
                case OpCode.True: case OpCode.False: stack.Add(TypeKind.Bool); break;
                case OpCode.LoadLocal: stack.Add(locals[instruction.A]); safe = !captured.Contains(instruction.A); break;
                case OpCode.InitializeLocal: case OpCode.StoreLocal:
                    locals[instruction.A] = Pop(); safe = !captured.Contains(instruction.A); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: var item = Pop(); stack.Add(item); stack.Add(item); break;
                case OpCode.Not: safe = Is(Pop(), TypeKind.Bool); stack.Add(TypeKind.Bool); break;
                case OpCode.IsNull: Pop(); stack.Add(TypeKind.Bool); break;
                case OpCode.Negate: safe = Is(Pop(), TypeKind.Number); stack.Add(TypeKind.Number); break;
                case OpCode.Add: case OpCode.Subtract: case OpCode.Multiply: case OpCode.Divide: case OpCode.Remainder:
                    safe = Is(Pop(), TypeKind.Number) & Is(Pop(), TypeKind.Number); stack.Add(TypeKind.Number); break;
                case OpCode.PathJoin:
                    safe = Is(Pop(), TypeKind.Path | TypeKind.String) & Is(Pop(), TypeKind.Path | TypeKind.String); stack.Add(TypeKind.Path); break;
                case OpCode.Convert:
                    safe = Is(Pop(), TypeKind.Path | TypeKind.String); stack.Add(instruction.Text == "Path" ? TypeKind.Path : TypeKind.String); break;
                case OpCode.Transfer: case OpCode.LocalTransfer:
                    safe = Is(Pop(), TypeKind.Path | TypeKind.String) & Is(Pop(), TypeKind.Path | TypeKind.String)
                        && pc + 1 < code.Count && code[pc + 1].Op == OpCode.Pop;
                    stack.Add(TypeKind.Bool); break;
                default: safe = false; break;
            }
            if (safe) continue;
            Region(start, pc); start = pc + 1; stack.Clear(); Array.Clear(locals);
        }
        Region(start, code.Count);
        if (removed.Count == 0) return function;
        var output = new List<Instruction>();
        var positions = new int[code.Count + 1];
        var branches = new List<int>();
        for (var pc = 0; pc < code.Count; pc++)
        {
            if (preceding.TryGetValue(pc, out var suffix)) output.AddRange(suffix);
            positions[pc] = output.Count;
            if (moves.TryGetValue(pc, out var markers)) output.AddRange(markers);
            if (removed.Contains(pc)) continue;
            if (code[pc].Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(output.Count);
            output.Add(code[pc]);
        }
        if (preceding.TryGetValue(code.Count, out var trailing)) output.AddRange(trailing);
        positions[^1] = output.Count;
        if (moves.TryGetValue(code.Count, out var end)) output.AddRange(end);
        foreach (var pc in branches)
        {
            var instruction = output[pc];
            output[pc] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] } : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = output };

        void Region(int first, int last)
        {
            var markers = new List<(int Position, Instruction Instruction)>();
            var transfers = new List<(int Position, int Slot)>();
            for (var pc = first; pc < last; pc++)
            {
                if (code[pc].Op == OpCode.Barrier) markers.Add((pc, code[pc]));
                if (code[pc].Op is OpCode.Transfer or OpCode.LocalTransfer) transfers.Add((pc, markers.Count));
            }
            if (markers.Count == 0 || transfers.Count == 0) return;
            var before = new int[markers.Count + 1];
            var after = new int[markers.Count + 1];
            var upper = markers.Count;
            for (var slot = markers.Count; slot >= 0; slot--)
            {
                before[slot] = upper;
                if (slot > 0 && markers[slot - 1].Instruction.A == 1) upper = slot - 1;
            }
            var lower = 0;
            for (var slot = 0; slot <= markers.Count; slot++)
            {
                after[slot] = lower;
                if (slot < markers.Count && markers[slot].Instruction.A == 2) lower = slot + 1;
            }
            var intervals = transfers.Select(transfer => new Transfer(transfer.Position, after[transfer.Slot], before[transfer.Slot])).ToArray();
            var marker = 0; var index = 0;
            while (index < intervals.Length)
            {
                var slot = intervals[index].Upper;
                var position = intervals[index].Position;
                while (marker < slot) Move(markers[marker++], position);
                index++;
                while (index < intervals.Length && intervals[index].Lower <= slot) index++;
            }
            while (marker < markers.Count) Move(markers[marker++], last, predecessor: true);
        }

        void Move((int Position, Instruction Instruction) marker, int position, bool predecessor = false)
        {
            removed.Add(marker.Position);
            var destination = predecessor ? preceding : moves;
            if (!destination.TryGetValue(position, out var list)) destination[position] = list = [];
            list.Add(marker.Instruction);
        }
    }
}
