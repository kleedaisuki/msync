namespace Msync.Core;

internal static class ValueNumbering
{
    private readonly record struct Key(OpCode Op, string? Text, int Count, int Left, int Right, int Third);

    internal static FunctionCode Eliminate(FunctionCode function, IReadOnlyList<Value> constants)
    {
        var code = function.Instructions;
        var starts = new HashSet<int> { 0 };
        foreach (var instruction in code)
        {
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue) starts.Add(instruction.A);
            if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
        }
        var numbers = new Dictionary<Value, int>(ValueRepresentation.Comparer);
        var expressions = new Dictionary<Key, (int Number, int Position)>();
        var retained = new Dictionary<int, int>();
        var replacements = new Dictionary<int, (int Slot, int Count)>();
        var stack = new List<int>();
        var locals = new int[function.LocalCount];
        var hasCells = function.CaptureCount > 0 || code.Any(instruction => instruction.Op == OpCode.LoadCell);
        var next = 0; var localCount = function.LocalCount;
        void Reset()
        {
            expressions.Clear(); stack.Clear();
            for (var index = 0; index < locals.Length; index++) locals[index] = next++;
        }
        int Pop() { if (stack.Count == 0) return next++; var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item; }
        int[] PopMany(int count) { var values = new int[count]; for (var index = count - 1; index >= 0; index--) values[index] = Pop(); return values; }
        int Literal(Value value)
        {
            if (value is ScopeValue or FunctionValue or RangeValue or CellValue) return next++;
            if (numbers.TryGetValue(value, out var number)) return number;
            number = next++; numbers.Add(value, number); return number;
        }
        Reset();
        for (var pc = 0; pc < code.Count; pc++)
        {
            if (starts.Contains(pc) && pc != 0) Reset();
            var instruction = code[pc];
            if (Candidate(instruction, out var count))
            {
                var operands = PopMany(count);
                var key = new Key(instruction.Op, instruction.Text, count, count > 0 ? operands[0] : -1,
                    count > 1 ? operands[1] : -1, count > 2 ? operands[2] : -1);
                if (expressions.TryGetValue(key, out var previous))
                {
                    if (!retained.TryGetValue(previous.Position, out var slot)) { slot = localCount++; retained.Add(previous.Position, slot); }
                    replacements.Add(pc, (slot, count)); stack.Add(previous.Number);
                }
                else { var number = next++; expressions.Add(key, (number, pc)); stack.Add(number); }
                continue;
            }
            switch (instruction.Op)
            {
                case OpCode.Nop: case OpCode.Barrier: break;
                case OpCode.Constant: stack.Add(Literal(constants[instruction.A])); break;
                case OpCode.True: stack.Add(Literal(Value.True)); break;
                case OpCode.False: stack.Add(Literal(Value.False)); break;
                case OpCode.Null: stack.Add(Literal(Value.Null)); break;
                case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
                case OpCode.StoreLocal: case OpCode.InitializeLocal:
                    locals[instruction.A] = Pop();
                    // A captured cell may alias any other local. Its write cannot
                    // share a value number with a prior dereference of that cell.
                    if (hasCells)
                        for (var index = 0; index < locals.Length; index++) if (index != instruction.A) locals[index] = next++;
                    break;
                case OpCode.Dup: var value = Pop(); stack.Add(value); stack.Add(value); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.LoadBuiltin: case OpCode.LoadCell: case OpCode.LoadStorage: stack.Add(next++); break;
                case OpCode.MakeClosure: PopMany(instruction.B); stack.Add(next++); break;
                case OpCode.MakeScope: case OpCode.MakeRange: case OpCode.MakeLazyRange: PopMany(instruction.A); stack.Add(next++); break;
                case OpCode.Call: case OpCode.ProcessCall:
                    PopMany(instruction.A + 1); stack.Add(next++); Invalidate(); break;
                case OpCode.NativeCall: PopMany(instruction.A); stack.Add(next++); Invalidate(); break;
                case OpCode.GetMember: case OpCode.GetCapture: case OpCode.SpecializeClosure: case OpCode.GetIterator:
                case OpCode.SelectPath: case OpCode.DeferRange: Pop(); stack.Add(next++); break;
                case OpCode.Acquire: if (instruction.A != 0) { Pop(); stack.Add(next++); } Invalidate(); break;
                case OpCode.IteratorNext: stack.Add(next++); Invalidate(); break;
                case OpCode.JumpIfTrue: case OpCode.JumpIfFalse: Pop(); Reset(); break;
                case OpCode.Jump: case OpCode.Return: Reset(); break;
                default: PopMany(2); stack.Add(next++); if (instruction.Op == OpCode.SetMember) Invalidate(); break;
            }
        }
        if (replacements.Count == 0) return function;
        var positions = new int[code.Count + 1];
        var output = new List<Instruction>();
        var branches = new List<int>();
        for (var pc = 0; pc < code.Count; pc++)
        {
            positions[pc] = output.Count;
            var instruction = code[pc];
            if (replacements.TryGetValue(pc, out var replacement))
            {
                for (var argument = 0; argument < replacement.Count; argument++) output.Add(new(OpCode.Pop, Span: instruction.Span));
                output.Add(new(OpCode.LoadLocal, replacement.Slot, Span: instruction.Span));
                continue;
            }
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(output.Count);
            output.Add(instruction);
            if (retained.TryGetValue(pc, out var slot))
            {
                output.Add(new(OpCode.Dup, Span: instruction.Span));
                output.Add(new(OpCode.InitializeLocal, slot, Span: instruction.Span));
            }
        }
        positions[^1] = output.Count;
        foreach (var branch in branches)
        {
            var instruction = output[branch];
            output[branch] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] } : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = output, LocalCount = localCount };

        void Invalidate()
        {
            expressions.Clear();
            for (var index = 0; index < locals.Length; index++) locals[index] = next++;
        }
    }

    private static bool Candidate(Instruction instruction, out int count)
    {
        count = instruction.Op switch
        {
            OpCode.Not or OpCode.IsNull or OpCode.Negate or OpCode.Convert => 1,
            OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Equal or OpCode.NotEqual
                or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.IsType or OpCode.PathJoin => 2,
            OpCode.RegexMatch => instruction.Text is null ? 2 : 1,
            OpCode.NativeCall when instruction.A <= 3 && instruction.Text is { } name && !name.StartsWith("ranges.", StringComparison.Ordinal)
                && NativeSignatures.TryGet(name, out var native) && native.Effect == NativeEffect.Pure
                && (EffectAnalysis.NativeEffects(name) & (InstructionEffect.Read | InstructionEffect.Write | InstructionEffect.Invoke)) == 0 => instruction.A,
            _ => -1
        };
        return count >= 0;
    }
}
