namespace Msync.Core;

internal static class FunctionInlining
{
    private sealed record Shape(int? Function = null, IReadOnlyDictionary<string, Shape?>? Members = null, int? Captures = null);

    internal static Module Inline(Module module, int level)
    {
        var functions = module.Functions.ToArray();
        var targets = functions.Select(f => Targets(f, module.Constants, requireKnownCallee: true, layouts: functions)).ToArray();
        var recursive = Recursive(targets);
        var uses = new int[functions.Length];
        foreach (var target in targets.SelectMany(t => t.Values)) uses[target]++;
        var next = new FunctionCode[functions.Length];
        for (var index = 0; index < functions.Length; index++)
            next[index] = module.PreserveLocals && index == module.EntryPoint ? functions[index]
                : InlineFunction(functions[index], targets[index], functions, recursive, uses, level);
        // The module driver folds and removes dead values between expansion
        // waves, rather than materializing an exponential transient call tree.
        return module with { Functions = next };
    }

    private static FunctionCode InlineFunction(FunctionCode function, Dictionary<int, int> targets,
        FunctionCode[] functions, bool[] recursive, int[] uses, int level)
    {
        var inline = targets.Where(pair => !recursive[pair.Value]
            && CanInline(functions[pair.Value])
            && (uses[pair.Value] == 1 || functions[pair.Value].Instructions.Count <= (level == 1 ? 32 : level == 2 ? 128 : 512)))
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
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext)
                    originalBranches.Add(code.Count);
                code.Add(instruction); continue;
            }
            var callee = functions[target];
            var offset = locals; locals += callee.LocalCount;
            for (var argument = instruction.A - 1; argument >= 0; argument--)
                code.Add(new(OpCode.InitializeLocal, offset + callee.CaptureCount + argument, Span: instruction.Span));
            if (callee.CaptureCount == 0) code.Add(new(OpCode.Pop, Span: instruction.Span));
            else
            {
                var closure = locals++;
                code.Add(new(OpCode.InitializeLocal, closure, Span: instruction.Span));
                for (var capture = 0; capture < callee.CaptureCount; capture++)
                {
                    code.Add(new(OpCode.LoadLocal, closure, Span: instruction.Span));
                    code.Add(new(OpCode.GetCapture, capture, Span: instruction.Span));
                    code.Add(new(OpCode.InitializeLocal, offset + capture, Span: instruction.Span));
                }
            }
            var start = code.Count;
            foreach (var body in callee.Instructions)
                code.Add(body.Op switch
                {
                    OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.LoadCell or OpCode.LoadStorage => body with { A = offset + body.A },
                    OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue => body with { A = start + body.A },
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

    private static bool CanInline(FunctionCode function)
    {
        // Admission and return own scheduled work. An ordinary stack splice must
        // not erase that lifetime boundary; scalar/cell operations have no jobs.
        if (function.Instructions.Any(instruction => (EffectAnalysis.Effects(instruction)
            & (InstructionEffect.Read | InstructionEffect.Invoke | InstructionEffect.Control)) != 0)) return false;
        var heights = new int?[function.Instructions.Count + 1];
        var work = new Queue<int>(); heights[0] = 0; work.Enqueue(0);
        while (work.TryDequeue(out var pc))
        {
            if (pc == function.Instructions.Count) return false;
            var instruction = function.Instructions[pc];
            var height = heights[pc]!.Value;
            if (instruction.Op == OpCode.Return) { if (height == 0) return false; continue; }
            void Next(int target, int count)
            {
                if (heights[target] is not null) return;
                heights[target] = count; work.Enqueue(target);
            }
            if (instruction.Op == OpCode.Jump) { Next(instruction.A, height); continue; }
            var delta = instruction.Op switch
            {
                OpCode.Constant or OpCode.Null or OpCode.True or OpCode.False or OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.LoadBuiltin or OpCode.GetSingleton or OpCode.Dup => 1,
                OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Pop or OpCode.JumpIfTrue or OpCode.JumpIfFalse => -1,
                OpCode.MakeClosure => 1 - instruction.B,
                OpCode.MakeScope or OpCode.MakeRange or OpCode.MakeLazyRange or OpCode.NativeCall => 1 - instruction.A,
                OpCode.Nop or OpCode.SetSingleton or OpCode.SpecializeClosure or OpCode.GetMember or OpCode.GetCapture or OpCode.Not or OpCode.IsNull or OpCode.Negate
                    or OpCode.Convert or OpCode.SelectPath or OpCode.DeferRange => 0,
                OpCode.RegexMatch => instruction.Text is null ? -1 : 0,
                _ => -1
            };
            height += delta;
            if (instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse) Next(instruction.A, height);
            Next(pc + 1, height);
        }
        return true;
    }

    internal static bool[] Recursive(Dictionary<int, int>[] targets)
    {
        var color = new byte[targets.Length];
        var recursive = new bool[targets.Length];
        var path = new List<int>();
        void Visit(int index)
        {
            if (color[index] == 2) return;
            if (color[index] == 1)
            {
                for (var position = path.LastIndexOf(index); position < path.Count; position++) recursive[path[position]] = true;
                return;
            }
            color[index] = 1; path.Add(index);
            foreach (var target in targets[index].Values.Distinct()) Visit(target);
            path.RemoveAt(path.Count - 1); color[index] = 2;
        }
        for (var index = 0; index < color.Length; index++) Visit(index);
        return recursive;
    }

    internal static Dictionary<int, int> Targets(FunctionCode function, IReadOnlyList<Value> constants, bool requireKnownCallee = false,
        IReadOnlyList<FunctionCode>? layouts = null)
    {
        var targets = new Dictionary<int, int>();
        if (!function.Instructions.Any(instruction => instruction.Op == OpCode.Call)) return targets;
        var incoming = new HashSet<int>();
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) incoming.Add(instruction.A);
            if (instruction.Op == OpCode.IteratorNext) incoming.Add(instruction.B);
        }
        var locals = new Shape?[function.LocalCount];
        var aliases = function.Instructions.Any(instruction => instruction.Op == OpCode.StoreLocal)
            && (function.CaptureCount > 0 || function.Instructions.Any(instruction => instruction.Op is OpCode.LoadCell or OpCode.LoadStorage or OpCode.GetCapture))
            ? PredicateOptimization.CaptureAliases(function) : [];
        var stack = new List<Shape?>();
        Shape? Pop() { if (stack.Count == 0) return null; var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item; }
        Shape?[] PopMany(int count) { var result = new Shape?[count]; for (var i = count - 1; i >= 0; i--) result[i] = Pop(); return result; }
        for (var pc = 0; pc < function.Instructions.Count; pc++)
        {
            if (incoming.Contains(pc)) { stack.Clear(); Array.Clear(locals); }
            var instruction = function.Instructions[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.GetSingleton: stack.Add(null); break;
                case OpCode.SetSingleton: Pop(); stack.Add(null); break;
                case OpCode.Constant: stack.Add(constants[instruction.A] is FunctionValue { NativeName: null } f ? new(f.Function, Captures: f.Captures?.Count ?? 0) : null); break;
                case OpCode.Null: case OpCode.True: case OpCode.False: case OpCode.LoadBuiltin: stack.Add(null); break;
                case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
                case OpCode.LoadCell: case OpCode.LoadStorage: stack.Add(null); break;
                case OpCode.StoreLocal:
                    var assigned = Pop();
                    // A raw capture can put the same location in several slots.
                    // Invalidate future loads, not already evaluated callees on
                    // the operand stack: argument effects cannot replace them.
                    if (Array.BinarySearch(aliases, instruction.A) >= 0)
                        foreach (var alias in aliases) locals[alias] = null;
                    locals[instruction.A] = assigned; break;
                case OpCode.InitializeLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: var item = Pop(); stack.Add(item); stack.Add(item); break;
                case OpCode.MakeClosure: PopMany(instruction.B); stack.Add(new(instruction.A, Captures: instruction.B)); break;
                case OpCode.SpecializeClosure:
                    var specialized = Pop();
                    stack.Add(instruction.Text is null && (specialized?.Function is not null || !requireKnownCallee)
                        ? new(instruction.A, Captures: specialized?.Captures) : instruction.Text is not null && specialized?.Function is { } original
                            && FunctionDispatch.Parse(instruction.Text).TryGetValue(original, out var mapped) ? new(mapped, Captures: specialized.Captures) : null); break;
                case OpCode.MakeScope:
                    var values = PopMany(instruction.A); var names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    stack.Add(new(Members: names.Select((name, i) => KeyValuePair.Create(name, values[i])).ToDictionary())); break;
                case OpCode.GetMember:
                    var scope = Pop(); stack.Add(scope?.Members is { } members && members.TryGetValue(instruction.Text!, out var member) ? member : null); break;
                case OpCode.Call:
                    PopMany(instruction.A); var closure = Pop(); var target = instruction.B > 0 && (!requireKnownCallee || closure?.Function is not null)
                        ? instruction.B - 1 : instruction.Text is null ? closure?.Function : null;
                    if (instruction.Text is not null && closure?.Function is { } identity
                        && FunctionDispatch.Parse(instruction.Text).TryGetValue(identity, out var dispatched)) target = dispatched;
                    if (target is { } known && (layouts is null || closure?.Captures is not { } count || count <= layouts[known].LocalCount)) targets[pc] = known;
                    stack.Add(null); Array.Clear(locals); break;
                case OpCode.NativeCall: PopMany(instruction.A); stack.Add(null); Array.Clear(locals); break;
                case OpCode.ProcessCall: PopMany(instruction.A + 1); stack.Add(null); Array.Clear(locals); break;
                case OpCode.MakeRange: case OpCode.MakeLazyRange: PopMany(instruction.A); stack.Add(null); break;
                case OpCode.Not:
                case OpCode.IsNull:
                case OpCode.Negate:
                case OpCode.Convert:
                case OpCode.SelectPath:
                case OpCode.GetIterator:
                case OpCode.GetCapture:
                case OpCode.DeferRange: Pop(); stack.Add(null); break;
                case OpCode.RegexMatch: PopMany(instruction.Text is null ? 2 : 1); stack.Add(null); break;
                case OpCode.Barrier: break;
                case OpCode.Acquire: if (instruction.A != 0) Pop(); break;
                case OpCode.IteratorNext: stack.Add(null); break;
                case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(); stack.Clear(); Array.Clear(locals); break;
                case OpCode.Jump: case OpCode.Return: stack.Clear(); Array.Clear(locals); break;
                default: PopMany(2); stack.Add(null); if (instruction.Op == OpCode.SetMember) Array.Clear(locals); break;
            }
        }
        return targets;
    }
}
