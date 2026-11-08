namespace Msync.Core;

internal static class TailCallOptimization
{
    internal static FunctionCode Optimize(Module module, FunctionCode function, int functionIndex)
    {
        var code = function.Instructions;
        var targets = FunctionInlining.Targets(function, module.Constants, requireKnownCallee: true);
        var tailCalls = targets.Where(pair => pair.Value == functionIndex && Returns(code, pair.Key + 1))
            .Select(pair => pair.Key).ToHashSet();
        if (tailCalls.Count == 0 || OwnsWork(function, targets, functionIndex)) return function;

        var closure = function.LocalCount;
        var arguments = closure + 1;
        var output = new List<Instruction>();
        var positions = new int[code.Count + 1];
        var branches = new List<int>();
        for (var pc = 0; pc < code.Count; pc++)
        {
            positions[pc] = output.Count;
            var instruction = code[pc];
            if (!tailCalls.Contains(pc))
            {
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext)
                    branches.Add(output.Count);
                output.Add(instruction); continue;
            }

            for (var argument = function.ParameterCount - 1; argument >= 0; argument--)
                output.Add(new(OpCode.InitializeLocal, arguments + argument, Span: instruction.Span));
            output.Add(new(OpCode.InitializeLocal, closure, Span: instruction.Span));

            // A logical invocation owns fresh locations. Replace, rather than
            // mutate, old frame cells which an escaped closure may still own.
            for (var slot = function.CaptureCount; slot < function.LocalCount; slot++)
            {
                output.Add(new(OpCode.Null, Span: instruction.Span));
                output.Add(new(OpCode.InitializeLocal, slot, Span: instruction.Span));
            }
            for (var capture = 0; capture < function.CaptureCount; capture++)
            {
                output.Add(new(OpCode.LoadLocal, closure, Span: instruction.Span));
                output.Add(new(OpCode.GetCapture, capture, Span: instruction.Span));
                output.Add(new(OpCode.InitializeLocal, capture, Span: instruction.Span));
            }
            for (var argument = 0; argument < function.ParameterCount; argument++)
            {
                output.Add(new(OpCode.LoadLocal, arguments + argument, Span: instruction.Span));
                output.Add(new(OpCode.InitializeLocal, function.CaptureCount + argument, Span: instruction.Span));
            }
            output.Add(new(OpCode.Jump, 0, Span: instruction.Span));
        }
        positions[^1] = output.Count;
        foreach (var branch in branches)
        {
            var instruction = output[branch];
            output[branch] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] }
                : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = output, LocalCount = arguments + function.ParameterCount };
    }

    private static bool OwnsWork(FunctionCode function, IReadOnlyDictionary<int, int> targets, int functionIndex)
    {
        for (var pc = 0; pc < function.Instructions.Count; pc++)
        {
            var instruction = function.Instructions[pc];
            if (instruction.Op == OpCode.Call && targets.TryGetValue(pc, out var target) && target == functionIndex) continue;
            if ((EffectAnalysis.Effects(instruction) & (InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.Control)) != 0) return true;
        }
        return false;
    }

    private static bool Returns(IReadOnlyList<Instruction> code, int pc)
    {
        var visited = new HashSet<int>();
        while (pc < code.Count && visited.Add(pc))
        {
            var instruction = code[pc];
            if (instruction.Op == OpCode.Return) return true;
            if (instruction.Op == OpCode.Jump) { pc = instruction.A; continue; }
            if (instruction.Op != OpCode.Nop) return false;
            pc++;
        }
        return false;
    }
}
