namespace Msync.Optimization;

internal static class AffineScanFusion
{
    private sealed record Scan(int Start, int Header, int Latch, int End, int Range, int Iterator);

    private sealed record Body(HashSet<int> Writes, HashSet<int> Inputs,
        HashSet<int> Outputs, HashSet<int> Private);

    private sealed record Group(List<Scan> Scans, List<Body> Bodies);

    internal static FunctionCode Optimize(FunctionCode function,
        Func<int, int, AffineScanFacts?> factsAt)
    {
        var code = function.Instructions;
        if (code.Count(instruction => instruction.Op == OpCode.IteratorNext) < 2) return function;
        var incoming = Incoming(code);
        var scans = Scans(code, incoming);
        if (scans.Count < 2) return function;
        Dictionary<int, Group> groups = [];
        for (int index = 0; index < scans.Count; index++)
        {
            Scan first = scans[index];
            var facts = factsAt(first.Start, first.Range);
            if (facts is null || facts.CapturedSlots.Contains(first.Range)) continue;
            Body? body = Analyze(function, first, facts);
            if (body is null) continue;
            Group group = new([first], [body]);
            int last = index;
            while (last + 1 < scans.Count)
            {
                Scan next = scans[last + 1];
                if (next.Range != first.Range || !EmptyBetween(code, scans[last].End, next.Start)
                    || ExternalEntry(incoming, first.Start, next.End)
                    || ReadAfter(code, next.End, next.Iterator)) break;
                Body? following = Analyze(function, next, facts);
                if (following is null || group.Bodies.Any(previous => !Independent(previous, following))) break;
                group.Scans.Add(next);
                group.Bodies.Add(following);
                last++;
            }
            if (group.Scans.Count == 1) continue;
            groups.Add(first.Start, group);
            index = last;
        }
        return groups.Count == 0 ? function : Rewrite(function, groups);
    }

    private static List<Scan> Scans(IReadOnlyList<Instruction> code, List<int>?[] incoming)
    {
        List<Scan> result = [];
        for (int start = 0; start + 5 < code.Count; start++)
        {
            if (code[start].Op != OpCode.LoadLocal || code[start + 1].Op != OpCode.GetIterator
                || code[start + 2].Op is not (OpCode.StoreLocal or OpCode.InitializeLocal)) continue;
            int header = start + 3;
            Instruction next = code[header];
            if (next.Op != OpCode.IteratorNext || next.A != code[start + 2].A
                || next.A == code[start].A
                || next.B <= header + 2 || next.B > code.Count) continue;
            int latch = next.B - 1;
            if (code[latch].Op != OpCode.Jump || code[latch].A != header
                || code[header + 1].Op is not (OpCode.StoreLocal or OpCode.InitializeLocal)
                || ExternalEntry(incoming, start, next.B)) continue;
            result.Add(new(start, header, latch, next.B, code[start].A, next.A));
            start = latch;
        }
        return result;
    }

    private static Body? Analyze(FunctionCode function, Scan scan, AffineScanFacts facts)
    {
        var types = facts.LocalTypes.ToDictionary(pair => pair.Key,
            pair => pair.Value == TypeKind.Scope ? TypeKind.Any : pair.Value);
        List<TypeKind> stack = [facts.Source.NumberItems ? TypeKind.Number : TypeKind.Scope];
        HashSet<int> writes = [], inputs = [], defined = [];
        for (int pc = scan.Header + 1; pc < scan.Latch; pc++)
        {
            Instruction instruction = function.Instructions[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Constant:
                    if ((uint)instruction.A >= (uint)facts.ConstantTypes.Count) return null;
                    TypeKind constant = facts.ConstantTypes[instruction.A];
                    stack.Add(constant == TypeKind.Scope ? TypeKind.Any : constant);
                    break;
                case OpCode.Null: stack.Add(TypeKind.Null); break;
                case OpCode.True:
                case OpCode.False: stack.Add(TypeKind.Bool); break;
                case OpCode.LoadLocal:
                    if (instruction.A == scan.Range || instruction.A == scan.Iterator
                        || facts.CapturedSlots.Contains(instruction.A)) return null;
                    if (!defined.Contains(instruction.A)) inputs.Add(instruction.A);
                    stack.Add(types.GetValueOrDefault(instruction.A, TypeKind.Any));
                    break;
                case OpCode.StoreLocal:
                case OpCode.InitializeLocal:
                    if (instruction.A == scan.Range || instruction.A == scan.Iterator
                        || facts.CapturedSlots.Contains(instruction.A) || !Pop(stack, out TypeKind assigned)
                        || assigned is not (TypeKind.Number or TypeKind.Scope)) return null;
                    writes.Add(instruction.A);
                    defined.Add(instruction.A);
                    types[instruction.A] = assigned;
                    break;
                case OpCode.Pop:
                    if (!Pop(stack, out _)) return null;
                    break;
                case OpCode.Dup:
                    if (stack.Count == 0) return null;
                    stack.Add(stack[^1]);
                    break;
                case OpCode.GetMember:
                    if (!Pop(stack, out TypeKind receiver) || receiver != TypeKind.Scope
                        || instruction.Text is null || !facts.Source.NumericFields.Contains(instruction.Text)) return null;
                    stack.Add(TypeKind.Number);
                    break;
                case OpCode.Negate:
                    if (!Pop(stack, out TypeKind unary) || unary != TypeKind.Number) return null;
                    stack.Add(TypeKind.Number);
                    break;
                case OpCode.Add:
                case OpCode.Subtract:
                case OpCode.Multiply:
                case OpCode.Divide:
                case OpCode.Remainder:
                    if (!Pop(stack, out TypeKind right) || !Pop(stack, out TypeKind left)
                        || left != TypeKind.Number || right != TypeKind.Number) return null;
                    stack.Add(TypeKind.Number);
                    break;
                default: return null;
            }
        }
        if (stack.Count != 0) return null;
        HashSet<int> local = [];
        foreach (int slot in writes)
            if (!inputs.Contains(slot) && !ReadAfter(function.Instructions, scan.End, slot)) local.Add(slot);
        HashSet<int> outputs = new(writes);
        outputs.ExceptWith(local);
        return new(writes, inputs, outputs, local);
    }

    // D = [0, N): change schedule (statement, i) to (i, statement), while
    // retaining each statement's reduction order and expression grouping.
    private static bool Independent(Body first, Body second) =>
        !first.Writes.Overlaps(second.Inputs) && !second.Writes.Overlaps(first.Inputs)
        && !first.Outputs.Overlaps(second.Writes) && !second.Outputs.Overlaps(first.Writes);

    private static bool Pop(List<TypeKind> stack, out TypeKind value)
    {
        value = default;
        if (stack.Count == 0) return false;
        value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return true;
    }

    private static bool ReadAfter(IReadOnlyList<Instruction> code, int start, int slot)
    {
        HashSet<int> seen = [];
        Stack<int> pending = new([start]);
        while (pending.TryPop(out int pc))
        {
            if (pc == code.Count || !seen.Add(pc)) continue;
            Instruction instruction = code[pc];
            if (instruction.A == slot)
            {
                if (instruction.Op is OpCode.LoadLocal or OpCode.LoadStorage or OpCode.LoadCell or OpCode.IteratorNext)
                    return true;
                if (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal) continue;
            }
            foreach (int target in Successors(code, pc)) pending.Push(target);
        }
        return false;
    }

    private static bool EmptyBetween(IReadOnlyList<Instruction> code, int start, int end)
    {
        int depth = 0;
        for (int pc = start; pc < end; pc++)
        {
            if (code[pc].Op == OpCode.Nop) continue;
            if (code[pc].Op == OpCode.Null) { depth++; continue; }
            if (code[pc].Op != OpCode.Pop || depth == 0) return false;
            depth--;
        }
        return depth == 0;
    }

    private static bool ExternalEntry(List<int>?[] incoming, int start, int end)
    {
        for (int pc = start + 1; pc < end; pc++)
            if (incoming[pc] is { } sources && sources.Any(source => source < start || source >= end)) return true;
        return false;
    }

    private static List<int>?[] Incoming(IReadOnlyList<Instruction> code)
    {
        var incoming = new List<int>?[code.Count + 1];
        for (int pc = 0; pc < code.Count; pc++)
        {
            Instruction instruction = code[pc];
            int target = instruction.Op switch
            {
                OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse => instruction.A,
                OpCode.IteratorNext => instruction.B,
                _ => -1
            };
            if (target >= 0) (incoming[target] ??= []).Add(pc);
        }
        return incoming;
    }

    private static IEnumerable<int> Successors(IReadOnlyList<Instruction> code, int pc)
    {
        Instruction instruction = code[pc];
        if (instruction.Op == OpCode.Return) yield break;
        if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse)
        {
            yield return instruction.A;
            if (instruction.Op == OpCode.Jump) yield break;
        }
        if (instruction.Op == OpCode.IteratorNext) yield return instruction.B;
        if (pc + 1 < code.Count) yield return pc + 1;
    }

    private static FunctionCode Rewrite(FunctionCode function, Dictionary<int, Group> groups)
    {
        var source = function.Instructions;
        List<Instruction> output = [];
        int[] positions = new int[source.Count + 1];
        List<int> originalBranches = [];
        int locals = function.LocalCount;
        for (int pc = 0; pc < source.Count;)
        {
            if (!groups.TryGetValue(pc, out Group? group))
            {
                positions[pc] = output.Count;
                if (source[pc].Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext)
                    originalBranches.Add(output.Count);
                output.Add(source[pc++]);
                continue;
            }
            Scan first = group.Scans[0];
            int end = group.Scans[^1].End;
            for (int removed = pc; removed < end; removed++) positions[removed] = output.Count;
            for (; pc < first.Header; pc++) { positions[pc] = output.Count; output.Add(source[pc]); }
            int header = output.Count;
            output.Add(source[first.Header]);
            int item = locals++;
            output.Add(new(OpCode.InitializeLocal, item, Span: source[first.Header].Span));
            for (int index = 0; index < group.Scans.Count; index++)
            {
                Scan scan = group.Scans[index];
                Dictionary<int, int> remap = [];
                if (index != 0)
                    foreach (int slot in group.Bodies[index].Private) remap.Add(slot, locals++);
                output.Add(new(OpCode.LoadLocal, item, Span: source[scan.Header + 1].Span));
                for (int body = scan.Header + 1; body < scan.Latch; body++)
                {
                    positions[body] = output.Count;
                    Instruction instruction = source[body];
                    if (instruction.Op is OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal
                        && remap.TryGetValue(instruction.A, out int slot)) instruction = instruction with { A = slot };
                    output.Add(instruction);
                }
            }
            output.Add(source[first.Latch] with { A = header });
            output[header] = output[header] with { B = output.Count };
            pc = end;
            positions[pc] = output.Count;
        }
        positions[^1] = output.Count;
        foreach (int branch in originalBranches)
        {
            Instruction instruction = output[branch];
            output[branch] = instruction.Op == OpCode.IteratorNext
                ? instruction with { B = positions[instruction.B] }
                : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = output, LocalCount = locals };
    }
}
