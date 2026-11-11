namespace Msync.Optimization;

internal static class ExecutableLayout
{
    internal static Module Arrange(Module module)
    {
        if (module.OptimizationLevel < 2 || module.ExecutionPlans is not null) return module;
        FunctionCode[]? rewritten = null;
        for (int index = 0; index < module.Functions.Count; index++)
        {
            if (module.PreserveLocals && index == module.EntryPoint) continue;
            var function = module.Functions[index];
            var code = Arrange(function.Instructions);
            if (ReferenceEquals(code, function.Instructions)) continue;
            (rewritten ??= module.Functions.ToArray())[index] = function with { Instructions = code };
        }
        return rewritten is null ? module : module with { Functions = rewritten, Blocks = null };
    }

    // A trace segment ends at a terminator with no implicit fallthrough. Keeping
    // its interior intact preserves every conditional/iterator fallthrough.
    private static IReadOnlyList<Instruction> Arrange(IReadOnlyList<Instruction> code)
    {
        int terminators = 0;
        bool hasJump = false;
        for (int pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            if (Target(instruction) is { } target && target <= pc) return code;
            hasJump |= instruction.Op == OpCode.Jump;
            if (instruction.Op is OpCode.Jump or OpCode.Return) terminators++;
        }
        if (!hasJump || terminators < 2) return code;
        List<int> ends = new(terminators + 1);
        for (int pc = 0; pc < code.Count; pc++)
            if (code[pc].Op is OpCode.Jump or OpCode.Return) ends.Add(pc + 1);
        bool openTail = ends[^1] != code.Count;
        if (openTail) ends.Add(code.Count);
        int traceLimit = ends.Count - (openTail ? 1 : 0);
        Dictionary<int, int> starts = new(traceLimit);
        int[] incoming = new int[traceLimit];
        for (int index = 0; index < traceLimit; index++)
            starts.Add(index == 0 ? 0 : ends[index - 1], index);
        foreach (var instruction in code)
            if (Target(instruction) is { } target && starts.TryGetValue(target, out int segment))
                incoming[segment]++;

        List<int> order = new(ends.Count);
        bool[] placed = new bool[traceLimit];
        bool[] omitJump = new bool[ends.Count];
        bool changed = false;
        for (int index = 0; index < traceLimit; index++)
        {
            int current = index;
            while (!placed[current])
            {
                placed[current] = true;
                order.Add(current);
                int last = ends[current] - 1;
                if (code[last] is not { Op: OpCode.Jump } jump
                    || !starts.TryGetValue(jump.A, out int next)
                    || placed[next] || incoming[next] != 1 || code[jump.A].Op == OpCode.Jump) break;
                // Transfer followed by Pop is the discarded-result scheduling
                // contract; do not create that instruction pair by layout.
                if (last > 0 && code[last - 1].Op is (OpCode.Transfer or OpCode.LocalTransfer)
                    && code[jump.A].Op == OpCode.Pop) break;
                omitJump[current] = changed = true;
                current = next;
            }
        }
        if (!changed) return code;
        if (openTail) order.Add(ends.Count - 1);

        int[] positions = new int[code.Count + 1];
        List<Instruction> output = new(code.Count);
        foreach (int segment in order)
            for (int pc = segment == 0 ? 0 : ends[segment - 1]; pc < ends[segment]; pc++)
            {
                positions[pc] = output.Count;
                if (!omitJump[segment] || pc + 1 != ends[segment]) output.Add(code[pc]);
            }
        positions[^1] = output.Count;
        for (int pc = 0; pc < output.Count; pc++)
            if (Target(output[pc]) is { } target)
                output[pc] = output[pc].Op == OpCode.IteratorNext
                    ? output[pc] with { B = positions[target] }
                    : output[pc] with { A = positions[target] };
        return output.ToArray();
    }

    private static int? Target(Instruction instruction) => instruction.Op switch
    {
        OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse => instruction.A,
        OpCode.IteratorNext => instruction.B,
        _ => null
    };
}
