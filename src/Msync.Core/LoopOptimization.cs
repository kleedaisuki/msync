namespace Msync.Core;

internal static class LoopOptimization
{
    private sealed record Loop(int Header, int Latch, int Exit, int Iterator);
    private sealed record Expression(int Start, TypeKind Type, bool Invariant, int Operations);

    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants, int level)
    {
        var growthLimit = (long)function.Instructions.Count * (level == 1 ? 2 : level == 2 ? 4 : 8);
        var current = function;
        while (true)
        {
            var next = current;
            foreach (var loop in Loops(current.Instructions))
            {
                next = Expand(current, loop, growthLimit);
                if (!ReferenceEquals(next, current)) break;
                next = Hoist(current, loop, constants);
                if (!ReferenceEquals(next, current)) break;
            }
            if (ReferenceEquals(next, current)) return current;
            current = next;
        }
    }

    private static IEnumerable<Loop> Loops(IReadOnlyList<Instruction> code)
    {
        List<int>[]? incoming = null;
        // The iterator header dominates the body: no external edge may enter
        // between header and latch. This also identifies a real preheader.
        for (var latch = 0; latch < code.Count; latch++)
        {
            if (code[latch] is not { Op: OpCode.Jump } back || back.A >= latch) continue;
            var header = back.A;
            if (code[header] is not { Op: OpCode.IteratorNext } next || next.B != latch + 1 || header < 2) continue;
            if (code[header - 1] is not { Op: OpCode.StoreLocal } store || store.A != next.A || code[header - 2].Op != OpCode.GetIterator) continue;
            // The lexical preheader is the sole ordinary fallthrough entry.
            incoming ??= Incoming(code);
            if (Enumerable.Range(header, latch - header + 1)
                .Any(target => incoming[target].Any(pc => pc < header - 1 || pc > latch))) continue;
            yield return new(header, latch, next.B, next.A);
        }
    }

    private static List<int>[] Incoming(IReadOnlyList<Instruction> code)
    {
        var incoming = Enumerable.Range(0, code.Count).Select(_ => new List<int>()).ToArray();
        for (var pc = 0; pc < code.Count; pc++)
            foreach (var target in Successors(code, pc))
                if (target < code.Count) incoming[target].Add(pc);
        return incoming;
    }

    private static IEnumerable<int> Successors(IReadOnlyList<Instruction> code, int pc)
    {
        var instruction = code[pc];
        if (instruction.Op == OpCode.Return) yield break;
        if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse)
        {
            yield return instruction.A;
            if (instruction.Op == OpCode.Jump) yield break;
        }
        if (instruction.Op == OpCode.IteratorNext) yield return instruction.B;
        if (pc + 1 < code.Count) yield return pc + 1;
    }

    private static FunctionCode Expand(FunctionCode function, Loop loop, long growthLimit)
    {
        var code = function.Instructions;
        var range = loop.Header - 3;
        if (range < 0 || code[range].Op != OpCode.MakeRange) return function;
        for (var pc = 0; pc < code.Count; pc++)
            if (pc != loop.Header - 1 && pc != loop.Header
                && code[pc].Op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.IteratorNext
                && code[pc].A == loop.Iterator) return function;
        var count = code[range].A;
        var begin = range;
        var bodyLength = loop.Latch - loop.Header - 1;
        var expandedLength = (long)count * (bodyLength + 1) + count;
        var originalLength = loop.Exit - begin;
        // Expansion spends code space only in proportion to eliminated dynamic
        // iterator/control dispatches. Large literal tables keep their loop.
        if (function.Instructions.Count - originalLength + expandedLength > growthLimit) return function;
        for (var pc = 0; pc < code.Count; pc++)
        {
            foreach (var target in Successors(code, pc))
            {
                if ((pc < begin || pc >= loop.Exit) && target > begin && target < loop.Exit) return function;
                if (pc >= loop.Header + 1 && pc < loop.Latch && (target < loop.Header || target > loop.Exit)) return function;
            }
        }
        var output = new List<Instruction>();
        var oldPositions = new int[code.Count + 1];
        var outerBranches = new List<int>();
        for (var pc = 0; pc < begin; pc++) { oldPositions[pc] = output.Count; AddOriginal(pc); }
        oldPositions[begin] = output.Count;
        var firstSlot = function.LocalCount;
        for (var item = count - 1; item >= 0; item--) output.Add(new(OpCode.InitializeLocal, firstSlot + item, Span: code[range].Span));
        var copies = new List<(int Start, int[] Positions, List<int> Branches)>();
        for (var item = 0; item < count; item++)
        {
            var positions = new int[bodyLength + 1];
            var branches = new List<int>();
            var start = output.Count;
            output.Add(new(OpCode.LoadStorage, firstSlot + item, Span: code[loop.Header].Span));
            for (var pc = loop.Header + 1; pc < loop.Latch; pc++)
            {
                positions[pc - loop.Header - 1] = output.Count;
                if (IsBranch(code[pc])) branches.Add(output.Count);
                output.Add(code[pc]);
            }
            positions[^1] = output.Count;
            copies.Add((start, positions, branches));
        }
        var exit = output.Count;
        for (var item = 0; item < copies.Count; item++)
        {
            var copy = copies[item];
            var next = item + 1 < copies.Count ? copies[item + 1].Start : exit;
            int Target(int target) => target == loop.Header || target == loop.Latch ? next
                : target == loop.Exit ? exit : copy.Positions[target - loop.Header - 1];
            foreach (var pc in copy.Branches)
                output[pc] = RewriteTarget(output[pc], Target);
        }
        for (var pc = begin + 1; pc <= loop.Exit; pc++) oldPositions[pc] = exit;
        for (var pc = loop.Exit; pc < code.Count; pc++) { oldPositions[pc] = output.Count; AddOriginal(pc); }
        oldPositions[^1] = output.Count;
        foreach (var pc in outerBranches) output[pc] = RewriteTarget(output[pc], target => oldPositions[target]);
        return function with { Instructions = output, LocalCount = firstSlot + count };

        void AddOriginal(int pc)
        {
            if (IsBranch(code[pc])) outerBranches.Add(output.Count);
            output.Add(code[pc]);
        }
    }

    private static FunctionCode Hoist(FunctionCode function, Loop loop, IReadOnlyList<Value> constants)
    {
        var code = function.Instructions;
        var writes = new HashSet<int>();
        for (var pc = loop.Header; pc <= loop.Latch; pc++)
            if (code[pc].Op is OpCode.StoreLocal or OpCode.InitializeLocal) writes.Add(code[pc].A);
        var shared = code.Where(instruction => instruction.Op is OpCode.LoadCell or OpCode.LoadStorage)
            .Select(instruction => instruction.A).Concat(Enumerable.Range(0, function.CaptureCount)).ToHashSet();
        for (var pc = 1; pc < code.Count; pc++)
            if (code[pc].Op is OpCode.StoreLocal or OpCode.InitializeLocal
                && code[pc - 1].Op is OpCode.GetCapture or OpCode.LoadCell or OpCode.LoadStorage)
                shared.Add(code[pc].A);
        var types = LocalTypes(function, constants);
        var entries = code.SelectMany(instruction => instruction.Op switch
        {
            OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse => new[] { instruction.A },
            OpCode.IteratorNext => new[] { instruction.B },
            _ => []
        }).ToHashSet();
        var entryCounts = new int[code.Count + 1];
        for (var pc = 0; pc < code.Count; pc++) entryCounts[pc + 1] = entryCounts[pc] + (entries.Contains(pc) ? 1 : 0);
        var stack = new List<Expression>();
        var candidates = new List<(int Start, int End)>();
        Expression? Pop()
        {
            if (stack.Count == 0) return null;
            var result = stack[^1]; stack.RemoveAt(stack.Count - 1); return result;
        }
        for (var pc = loop.Header + 1; pc < loop.Latch; pc++)
        {
            if (entries.Contains(pc)) stack.Clear();
            var instruction = code[pc];
            TypeKind literal = instruction.Op switch
            {
                OpCode.Constant => constants[instruction.A].Type.Kind,
                OpCode.True or OpCode.False => TypeKind.Bool,
                OpCode.Null => TypeKind.Null,
                _ => TypeKind.Never
            };
            if (literal != TypeKind.Never) { stack.Add(new(pc, literal, true, 0)); continue; }
            if (instruction.Op == OpCode.LoadLocal)
            {
                stack.Add(new(pc, types[instruction.A], !writes.Contains(instruction.A) && !shared.Contains(instruction.A), 0)); continue;
            }
            var arity = Arity(instruction.Op);
            if (arity == 0) { stack.Clear(); continue; }
            var right = Pop(); var left = arity == 2 ? Pop() : null;
            if (right is null || arity == 2 && left is null) { stack.Clear(); continue; }
            var resultType = Total(instruction, left?.Type, right.Type);
            var start = left?.Start ?? right.Start;
            var invariant = right.Invariant && (left?.Invariant ?? true) && resultType != TypeKind.Never
                && entryCounts[pc + 1] == entryCounts[start + 1];
            var expression = new Expression(start, resultType, invariant, right.Operations + (left?.Operations ?? 0) + 1);
            stack.Add(expression);
            if (!invariant) continue;
            // Replace an already selected child by its larger expression tree.
            while (candidates.Count > 0 && candidates[^1].Start >= start) candidates.RemoveAt(candidates.Count - 1);
            candidates.Add((start, pc + 1));
        }
        if (candidates.Count == 0) return function;
        candidates = candidates.OrderBy(candidate => candidate.Start).ToList();
        var slots = candidates.Select((candidate, index) => (candidate, Slot: function.LocalCount + index))
            .ToDictionary(pair => pair.candidate.Start, pair => (pair.candidate.End, pair.Slot));
        var output = new List<Instruction>();
        var positions = new int[code.Count + 1];
        var originalBranches = new List<int>();
        for (var pc = 0; pc < code.Count; pc++)
        {
            if (pc == loop.Header)
                foreach (var candidate in candidates)
                {
                    for (var index = candidate.Start; index < candidate.End; index++) output.Add(code[index]);
                    output.Add(new(OpCode.InitializeLocal, slots[candidate.Start].Slot, Span: code[candidate.End - 1].Span));
                }
            positions[pc] = output.Count;
            if (slots.TryGetValue(pc, out var replacement))
            {
                output.Add(new(OpCode.LoadLocal, replacement.Slot, Span: code[pc].Span));
                while (++pc < replacement.End) positions[pc] = output.Count - 1;
                pc--; continue;
            }
            if (IsBranch(code[pc])) originalBranches.Add(output.Count);
            output.Add(code[pc]);
        }
        positions[^1] = output.Count;
        foreach (var pc in originalBranches) output[pc] = RewriteTarget(output[pc], target => positions[target]);
        return function with { Instructions = output, LocalCount = function.LocalCount + candidates.Count };
    }

    private static TypeKind[] LocalTypes(FunctionCode function, IReadOnlyList<Value> constants)
    {
        var result = new TypeKind[function.LocalCount];
        Array.Fill(result, TypeKind.Any);
        for (var index = 0; index < function.ParameterCount; index++)
            result[function.CaptureCount + index] = function.ParameterTypes is { } types && index < types.Count ? types[index].Kind : TypeKind.Any;
        var stores = function.Instructions.Select((instruction, pc) => (instruction, pc))
            .Where(pair => pair.instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal).GroupBy(pair => pair.instruction.A);
        foreach (var group in stores)
        {
            TypeKind type = TypeKind.Never;
            foreach (var pair in group)
            {
                if (pair.pc == 0) { type = TypeKind.Any; break; }
                var previous = function.Instructions[pair.pc - 1];
                type |= previous.Op switch
                {
                    OpCode.Constant => constants[previous.A].Type.Kind,
                    OpCode.True or OpCode.False => TypeKind.Bool,
                    OpCode.Null => TypeKind.Null,
                    _ => TypeKind.Any
                };
            }
            result[group.Key] = type;
        }
        return result;
    }

    private static int Arity(OpCode op) => op switch
    {
        OpCode.IsNull or OpCode.Not or OpCode.Negate or OpCode.Convert => 1,
        OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder or OpCode.Equal or OpCode.NotEqual
            or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.PathJoin or OpCode.IsType => 2,
        _ => 0
    };

    private static TypeKind Total(Instruction instruction, TypeKind? left, TypeKind right)
    {
        var op = instruction.Op;
        if (op == OpCode.IsNull) return TypeKind.Bool;
        if (op == OpCode.Not && right == TypeKind.Bool) return TypeKind.Bool;
        if (op == OpCode.Negate && right == TypeKind.Number) return TypeKind.Number;
        if (op == OpCode.Convert && instruction.Text is "Path" or "String" && (right & ~(TypeKind.Path | TypeKind.String)) == 0)
            return instruction.Text == "Path" ? TypeKind.Path : TypeKind.String;
        if (left == TypeKind.Number && right == TypeKind.Number)
        {
            if (op is OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder) return TypeKind.Number;
            if (op is OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual) return TypeKind.Bool;
        }
        if (left == TypeKind.String && right == TypeKind.String && op == OpCode.Add) return TypeKind.String;
        if (op == OpCode.PathJoin && left is { } a && (a & ~(TypeKind.Path | TypeKind.String)) == 0 && (right & ~(TypeKind.Path | TypeKind.String)) == 0) return TypeKind.Path;
        if (op == OpCode.IsType && right == TypeKind.Type) return TypeKind.Bool;
        if (op is OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual
            && (left == TypeKind.Bool && right == TypeKind.Bool || left == TypeKind.String && right == TypeKind.String)) return TypeKind.Bool;
        return TypeKind.Never;
    }

    private static bool IsBranch(Instruction instruction) => instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext;
    private static Instruction RewriteTarget(Instruction instruction, Func<int, int> target) => instruction.Op == OpCode.IteratorNext
        ? instruction with { B = target(instruction.B) } : instruction with { A = target(instruction.A) };
}
