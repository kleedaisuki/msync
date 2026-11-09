namespace Msync.Core;

internal static class TailCallOptimization
{
    internal static FunctionCode Optimize(Module module, FunctionCode function, int functionIndex)
    {
        var code = function.Instructions;
        if (!code.Select((instruction, index) => (instruction, index)).Any(pair => pair.instruction.Op == OpCode.Call
            && (pair.instruction.B == 0 || pair.instruction.B == functionIndex + 1) && Returns(code, pair.index + 1))) return function;
        var targets = FunctionInlining.Targets(function, module.Constants, requireKnownCallee: !ClosedFunctions(module));
        var tailCalls = targets.Where(pair => pair.Value == functionIndex && Returns(code, pair.Key + 1))
            .Select(pair => pair.Key).ToHashSet();
        if (tailCalls.Count == 0 || OwnsWork(function, targets, functionIndex) || ExcessCaptures(module, function.LocalCount)) return function;

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

    private static bool ExcessCaptures(Module module, int localCount)
    {
        // Direct specialization retains the actual closure's captures. A call
        // whose captures overflow the target frame traps before entering it.
        if (module.Functions.Any(function => function.Instructions.Any(instruction =>
            instruction.Op == OpCode.MakeClosure && instruction.B > localCount))) return true;
        bool Excess(Value value) => value switch
        {
            FunctionValue function => (function.Captures?.Count ?? 0) > localCount || (function.Captures?.Any(Excess) ?? false),
            ScopeValue scope => scope.Members.Values.Any(Excess),
            _ => false
        };
        return module.Constants.Any(Excess);
    }

    private static bool ClosedFunctions(Module module)
    {
        var entry = module.Functions[module.EntryPoint];
        if (module.PreserveLocals || entry.ParameterCount != 0 || entry.CaptureCount != 0) return false;
        var reached = new HashSet<int>();
        var work = new Queue<int>();
        void Function(int index) { if (reached.Add(index)) work.Enqueue(index); }
        bool Closed(Value value)
        {
            if (value is FunctionValue function)
            {
                if (function.NativeName is not null) return false;
                Function(function.Function);
                return function.Captures?.All(Closed) ?? true;
            }
            if (value is ScopeValue scope) return scope.Members.Values.All(Closed);
            return value is not (CellValue or RangeValue);
        }
        Function(module.EntryPoint);
        while (work.TryDequeue(out var index))
        {
            foreach (var instruction in module.Functions[index].Instructions)
            {
                if (instruction.Op == OpCode.LoadBuiltin && instruction.Text?.TrimStart('$') is "std") return false;
                if (instruction.Op == OpCode.Constant && !Closed(module.Constants[instruction.A])) return false;
                if (instruction.Op == OpCode.MakeClosure || instruction.Op == OpCode.SpecializeClosure && instruction.Text is null) Function(instruction.A);
                if (instruction.Op == OpCode.Call && instruction.B > 0) Function(instruction.B - 1);
                if (instruction.Op is OpCode.Call or OpCode.SpecializeClosure && instruction.Text is not null)
                    foreach (var pair in FunctionDispatch.Parse(instruction.Text)) { Function(pair.Key); Function(pair.Value); }
            }
        }
        return true;
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
