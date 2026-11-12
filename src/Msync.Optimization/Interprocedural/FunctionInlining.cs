namespace Msync.Optimization;

internal static class FunctionInlining
{
    internal static Module Inline(Module module, int level) => InlineWithCalls(module, level, new(module));

    internal static Module InlineWithCalls(Module module, int level, CallTargetGraph calls)
    {
        var functions = module.Functions.ToArray();
        var targets = calls.Targets;
        var recursive = calls.Recursive;
        var uses = new int[functions.Length];
        foreach (var target in targets.SelectMany(t => t.Values)) uses[target]++;
        var eligible = new bool?[functions.Length];
        var next = new FunctionCode[functions.Length];
        bool changed = false;
        for (var index = 0; index < functions.Length; index++)
        {
            next[index] = module.PreserveLocals && index == module.EntryPoint ? functions[index]
                : InlineFunction(module, functions[index], targets[index], calls.RawArguments(index), functions, recursive, uses, eligible, level);
            changed |= !ReferenceEquals(next[index], functions[index]);
        }
        // The module driver folds and removes dead values between expansion
        // waves, rather than materializing an exponential transient call tree.
        return changed ? module with { Functions = next } : module;
    }

    private static FunctionCode InlineFunction(Module module, FunctionCode function, Dictionary<int, int> targets,
        IReadOnlySet<int>? rawArguments, FunctionCode[] functions, bool[] recursive, int[] uses, bool?[] eligible, int level)
    {
        int budget = level == 1 ? 32 : level == 2 ? 128 : 512;
        var inline = targets.Where(pair => !recursive[pair.Value]
            && (uses[pair.Value] == 1 || functions[pair.Value].Instructions.Count <= budget)
            && (eligible[pair.Value] ??= CanInline(module, functions[pair.Value])))
            .ToDictionary();
        if (inline.Count == 0) return function;
        var code = new List<Instruction>();
        var positions = new int[function.Instructions.Count + 1];
        var originalBranches = new List<int>();
        var locals = function.LocalCount;
        for (var pc = 0; pc < function.Instructions.Count; pc++)
        {
            positions[pc] = code.Count;
            var instruction = function.Instructions[pc];
            if (!inline.TryGetValue(pc, out var target))
            {
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady or OpCode.IteratorNext)
                    originalBranches.Add(code.Count);
                code.Add(instruction); continue;
            }
            var callee = functions[target];
            var offset = locals; locals += callee.LocalCount;
            bool normalize = rawArguments?.Contains(pc) == true;
            for (var argument = instruction.A - 1; argument >= 0; argument--)
            {
                int slot = offset + callee.CaptureCount + argument;
                code.Add(new(OpCode.InitializeLocal, slot, Span: instruction.Span));
                // CopyArguments dereferences raw Cell operands on entry. A
                // cloned parameter still owns a private location.
                if (!normalize) continue;
                code.Add(new(OpCode.LoadLocal, slot, Span: instruction.Span));
                code.Add(new(OpCode.InitializeLocal, slot, Span: instruction.Span));
            }
            if (callee.CaptureCount == 0) code.Add(new(OpCode.Pop, Span: instruction.Span));
            else for (var capture = 0; capture < callee.CaptureCount; capture++)
            {
                if (capture + 1 < callee.CaptureCount) code.Add(new(OpCode.Dup, Span: instruction.Span));
                code.Add(new(OpCode.GetCapture, capture, Span: instruction.Span));
                code.Add(new(OpCode.InitializeLocal, offset + capture, Span: instruction.Span));
            }
            var start = code.Count;
            foreach (var body in callee.Instructions)
                code.Add(body.Op switch
                {
                    OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.LoadCell or OpCode.LoadStorage => body with { A = offset + body.A },
                    OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady => body with { A = start + body.A },
                    OpCode.IteratorNext => body with { A = offset + body.A, B = start + body.B },
                    OpCode.Return => new(OpCode.Jump, start + callee.Instructions.Count, Span: body.Span),
                    _ => body
                });
        }
        positions[^1] = code.Count;
        foreach (var index in originalBranches)
        {
            var instruction = code[index];
            code[index] = instruction.Op == OpCode.IteratorNext
                ? instruction with { B = positions[instruction.B] }
                : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = code, LocalCount = locals };
    }

    private static bool CanInline(Module module, FunctionCode function)
    {
        // Admission and return own scheduled work. An ordinary stack splice must
        // not erase that lifetime boundary; scalar/cell operations have no jobs.
        if (function.Instructions.Any(instruction => instruction.Op is not (OpCode.LoadField or OpCode.LoadStaticScope or OpCode.LoadStaticField or OpCode.JumpIfStaticReady)
            && (InstructionEffects.Effects(instruction)
            & (InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.Control)) != 0)) return false;
        var heights = new int?[function.Instructions.Count + 1];
        var work = new Queue<int>(); heights[0] = 0; work.Enqueue(0);
        while (work.TryDequeue(out var pc))
        {
            if (pc == function.Instructions.Count) return false;
            var instruction = function.Instructions[pc];
            var height = heights[pc]!.Value;
            if (instruction.Op == OpCode.Return) { if (height != 1) return false; continue; }
            void Next(int target, int count)
            {
                if (heights[target] is not null) return;
                heights[target] = count; work.Enqueue(target);
            }
            if (instruction.Op == OpCode.Jump) { Next(instruction.A, height); continue; }
            if (!ExecutionStackEffect.TryGet(module, instruction, height, out var effect)
                || height < effect.Inputs) return false;
            height += instruction.Op == OpCode.Dup ? 1 : (effect.ProducesValue ? 1 : 0) - effect.Inputs;
            if (instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady)
                Next(instruction.A, height);
            Next(pc + 1, height);
        }
        return true;
    }

    internal static Dictionary<int, int> Targets(Module module, FunctionCode function, IReadOnlyList<Value> constants, bool requireKnownCallee = false,
        IReadOnlyList<FunctionCode>? layouts = null, ISet<int>? knownClosures = null)
        => CallTargets.Analyze(module, function, constants, requireKnownCallee, layouts, knownClosures).Targets;
}
