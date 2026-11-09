namespace Msync.Core;

internal static class AggregateArgumentSpecialization
{
    private sealed record Fact(TypeRef Type, IReadOnlyDictionary<string, Fact>? Members = null, bool CallbackFreeRange = false, int Version = 0,
        bool Iterator = false, int? Captures = null);
    private sealed record Plan(string[][] Fields)
    {
        internal int Count => Fields.Sum(fields => Math.Max(1, fields.Length));
    }
    private sealed record Rewrite(int Target, Plan Plan);
    private static readonly Fact Unknown = new(TypeRef.Any);

    internal static Module Optimize(Module module, int level)
    {
        if (level < 2 || module.PreserveLocals) return module;
        var plans = new Dictionary<int, Plan>();
        for (int index = 0; index < module.Functions.Count; index++)
            if (Describe(module.Functions[index]) is { } plan) plans.Add(index, plan);
        if (plans.Count == 0) return module;
        var targets = module.Functions.Select(function => EffectAnalysisSafe(function)
            ? FunctionInlining.Targets(function, module.Constants, true, module.Functions) : new Dictionary<int, int>()).ToArray();
        var recursive = FunctionInlining.Recursive(targets);
        var called = targets.SelectMany(mapping => mapping.Values).ToHashSet();
        foreach (int target in plans.Keys.ToArray())
            if (!called.Contains(target) || recursive[target]) plans.Remove(target);
        if (plans.Count == 0) return module;
        var effects = MemoryEffects.Analyze(module);
        foreach (var target in plans.Keys.ToArray())
            if (effects[target].UnknownWrites || effects[target].Writes.Count != 0) plans.Remove(target);
        if (plans.Count == 0) return module;
        var functions = module.Functions.ToList();
        var clones = new Dictionary<int, int>();
        for (int caller = 0; caller < module.Functions.Count; caller++)
        {
            if (targets[caller].Count == 0) continue;
            var rewrites = FindCalls(module, module.Functions[caller], targets[caller], plans, effects, recursive);
            if (rewrites.Count == 0) continue;
            foreach (var rewrite in rewrites.Values)
            {
                if (clones.ContainsKey(rewrite.Target)) continue;
                var clone = Specialize(module.Functions[rewrite.Target], rewrite.Plan);
                int existing = functions.FindIndex(candidate => Equivalent(candidate, clone));
                if (existing < 0) { existing = functions.Count; functions.Add(clone); }
                clones.Add(rewrite.Target, existing);
            }
            functions[caller] = RewriteCalls(module.Functions[caller], rewrites, clones);
        }
        return functions.Where((function, index) => index >= module.Functions.Count || !ReferenceEquals(function, module.Functions[index])).Any()
            ? module with { Functions = functions, Blocks = null } : module;
    }

    private static bool EffectAnalysisSafe(FunctionCode function) => !function.Instructions.Any(instruction =>
        instruction.Op == OpCode.MakeScope && EffectAnalysis.ScopeConstructionMayFail(instruction));

    private static Plan? Describe(FunctionCode function)
    {
        if (function.ParameterTypes is null || !function.ParameterTypes.Any(type => type == TypeRef.Scope)) return null;
        var code = function.Instructions;
        var targets = code.Where(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse)
            .Select(instruction => instruction.A).Concat(code.Where(instruction => instruction.Op == OpCode.IteratorNext).Select(instruction => instruction.B)).ToHashSet();
        var fields = new string[function.ParameterCount][];
        for (int parameter = 0; parameter < fields.Length; parameter++)
        {
            fields[parameter] = [];
            if (function.ParameterTypes[parameter] != TypeRef.Scope) continue;
            int slot = function.CaptureCount + parameter;
            var names = new List<string>();
            bool eligible = true;
            for (int pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (!HasSlot(instruction.Op) || instruction.A != slot) continue;
                if (instruction.Op != OpCode.LoadLocal || pc + 1 >= code.Count || code[pc + 1] is not { Op: OpCode.GetMember, Text: { } name }
                    || targets.Contains(pc + 1)) { eligible = false; break; }
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
            if (eligible) fields[parameter] = names.ToArray();
        }
        return fields.Any(names => names.Length != 0) ? new(fields) : null;
    }

    private static Dictionary<int, Rewrite> FindCalls(Module module, FunctionCode function, Dictionary<int, int> targets,
        Dictionary<int, Plan> plans, IReadOnlyList<FunctionMemoryEffects> summaries, bool[] recursive)
    {
        var result = new Dictionary<int, Rewrite>();
        var locals = Enumerable.Repeat(Unknown, function.LocalCount).ToArray();
        for (int parameter = 0; parameter < function.ParameterCount; parameter++)
            locals[function.CaptureCount + parameter] = new(function.ParameterTypes?[parameter] ?? TypeRef.Any);
        var captured = function.Instructions.Where(instruction => instruction.Op == OpCode.LoadCell).Select(instruction => instruction.A)
            .Concat(Enumerable.Range(0, function.CaptureCount)).ToHashSet();
        var writes = new int[function.LocalCount];
        foreach (var instruction in function.Instructions)
            if (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal) writes[instruction.A]++;
        var entries = function.Instructions.Where(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse)
            .Select(instruction => instruction.A).Concat(function.Instructions.Where(instruction => instruction.Op == OpCode.IteratorNext).Select(instruction => instruction.B)).ToHashSet();
        var stack = new List<Fact>();
        int version = 0;
        Fact Pop() { if (stack.Count == 0) return Unknown; var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
        Fact[] PopMany(int count)
        {
            var values = new Fact[count];
            for (int index = count - 1; index >= 0; index--) values[index] = Pop();
            return values;
        }
        void Invalidate() { foreach (int slot in captured) locals[slot] = Unknown; }
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            if (entries.Contains(pc))
            {
                stack.Clear();
                for (int slot = 0; slot < locals.Length; slot++)
                    if (!locals[slot].Iterator || writes[slot] != 1 || captured.Contains(slot)) locals[slot] = Unknown;
            }
            var instruction = function.Instructions[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: case OpCode.Barrier: break;
                case OpCode.Constant: stack.Add(Constant(module.Constants[instruction.A], version)); break;
                case OpCode.True: case OpCode.False: stack.Add(new(TypeRef.Bool)); break;
                case OpCode.Null: stack.Add(new(TypeRef.Null)); break;
                case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
                case OpCode.LoadStorage: stack.Add(captured.Contains(instruction.A) ? Unknown : locals[instruction.A]); break;
                case OpCode.LoadCell: stack.Add(Unknown); break;
                case OpCode.StoreLocal:
                case OpCode.InitializeLocal:
                    locals[instruction.A] = Pop();
                    if (instruction.Op == OpCode.StoreLocal && captured.Contains(instruction.A)) version++;
                    break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: var duplicate = Pop(); stack.Add(duplicate); stack.Add(duplicate); break;
                case OpCode.MakeScope:
                    var members = PopMany(instruction.A); var names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    var fields = new Dictionary<string, Fact>(StringComparer.Ordinal);
                    bool unique = names.Length == members.Length;
                    for (int index = 0; unique && index < names.Length; index++) unique = fields.TryAdd(names[index], members[index]);
                    stack.Add(unique ? new(TypeRef.Scope, fields, Version: version) : Unknown); break;
                case OpCode.GetMember:
                    var receiver = Pop();
                    stack.Add(receiver.Version == version ? receiver.Members?.GetValueOrDefault(instruction.Text!) ?? Unknown : Unknown); break;
                case OpCode.SetMember: PopMany(2); stack.Add(new(TypeRef.Null)); version++; break;
                case OpCode.MakeClosure: PopMany(instruction.B); stack.Add(new(TypeRef.Function, Captures: instruction.B)); break;
                case OpCode.SpecializeClosure: var closure = Pop(); stack.Add(new(TypeRef.Function, Captures: closure.Captures)); break;
                case OpCode.Call:
                    var arguments = PopMany(instruction.A); var callee = Pop();
                    if (targets.TryGetValue(pc, out int target) && plans.TryGetValue(target, out var plan) && !recursive[target]
                        && callee.Captures == module.Functions[target].CaptureCount
                        && arguments.Length == plan.Fields.Length && Safe(plan, arguments, summaries[target], version))
                        result.Add(pc, new(target, plan));
                    stack.Add(targets.TryGetValue(pc, out target) ? new(module.Functions[target].ReturnType) : Unknown); Invalidate(); version++; break;
                case OpCode.NativeCall:
                    PopMany(instruction.A);
                    stack.Add(instruction.Text is not null && NativeSignatures.TryGet(instruction.Text, out var signature) ? new(signature.Result) : Unknown);
                    Invalidate(); version++; break;
                case OpCode.ProcessCall: PopMany(instruction.A + 1); stack.Add(new(TypeRef.Process)); Invalidate(); version++; break;
                case OpCode.MakeRange:
                    var items = PopMany(instruction.A);
                    stack.Add(new(new(TypeKind.Range, items.Select(item => item.Type).Aggregate(new TypeRef(TypeKind.Never), TypeRef.Union)), CallbackFreeRange: true)); break;
                case OpCode.MakeLazyRange:
                    PopMany(instruction.A); stack.Add(new(new(TypeKind.Range), CallbackFreeRange: instruction.Text?.All(value => value == '0') == true)); break;
                case OpCode.LoadBuiltin: stack.Add(Unknown); break;
                case OpCode.Convert: Pop(); stack.Add(new(instruction.Text == "Path" ? TypeRef.Path : TypeRef.String)); break;
                case OpCode.Qualify: case OpCode.PathJoin: PopMany(2); stack.Add(new(TypeRef.Path)); break;
                case OpCode.IsNull: case OpCode.Not: Pop(); stack.Add(new(TypeRef.Bool)); break;
                case OpCode.Negate: Pop(); stack.Add(new(TypeRef.Number)); break;
                case OpCode.SelectPath: Pop(); stack.Add(new(new(TypeKind.Range, TypeRef.Path), CallbackFreeRange: true)); break;
                case OpCode.GetIterator: var source = Pop(); stack.Add(source with { Iterator = true }); break;
                case OpCode.GetCapture: case OpCode.DeferRange: Pop(); stack.Add(Unknown); break;
                case OpCode.RegexMatch: PopMany(instruction.Text is null ? 2 : 1); stack.Add(new(TypeRef.Bool)); break;
                case OpCode.Acquire: if (instruction.A != 0) { Pop(); stack.Add(new(TypeRef.Process)); } Invalidate(); version++; break;
                case OpCode.IteratorNext:
                    var iterator = locals[instruction.A];
                    stack.Add(iterator.Iterator && iterator.CallbackFreeRange ? new(iterator.Type.Element ?? TypeRef.Any) : Unknown);
                    Invalidate(); version++; break;
                case OpCode.JumpIfTrue: case OpCode.JumpIfFalse: Pop(); stack.Clear(); Array.Fill(locals, Unknown); break;
                case OpCode.Jump: case OpCode.Return: stack.Clear(); Array.Fill(locals, Unknown); break;
                default: PopMany(2); stack.Add(Unknown); break;
            }
        }
        return result;
    }

    private static Fact Constant(Value value, int version) => value is ScopeValue scope
        ? new(TypeRef.Scope, scope.Members.ToDictionary(pair => pair.Key, pair => Constant(pair.Value, version), StringComparer.Ordinal), Version: version)
        : value is FunctionValue { NativeName: null } function ? new(TypeRef.Function, Captures: function.Captures?.Count ?? 0) : new(value.Type);

    private static bool Safe(Plan plan, IReadOnlyList<Fact> arguments, FunctionMemoryEffects summary, int version)
    {
        for (int parameter = 0; parameter < plan.Fields.Length; parameter++)
        {
            if (plan.Fields[parameter].Length == 0) continue;
            if (arguments[parameter].Members is not { } members || plan.Fields[parameter].Any(field => !members.ContainsKey(field))) return false;
            if (summary.Escapes.Concat(summary.ReturnAliases).Any(access => access.Root == new MemoryRoot(MemoryRootKind.Parameter, parameter)
                && access.Fields.Count == 0)) return false;
        }
        foreach (var access in summary.Enumerates)
        {
            if (access.Root.Kind != MemoryRootKind.Parameter || access.Descendants || access.Root.Index >= arguments.Count) return false;
            var value = arguments[access.Root.Index];
            foreach (string field in access.Fields)
            {
                if (value.Version != version || value.Members is not { } members || !members.TryGetValue(field, out var member)) return false;
                value = member;
            }
            if (value.Type != TypeRef.Path && value.Type != TypeRef.String && !value.CallbackFreeRange) return false;
        }
        return true;
    }

    private static FunctionCode Specialize(FunctionCode function, Plan plan)
    {
        var slots = new int[function.LocalCount];
        var fieldSlots = new Dictionary<(int Parameter, string Name), int>();
        var types = new List<TypeRef>();
        int next = function.CaptureCount;
        for (int capture = 0; capture < function.CaptureCount; capture++) slots[capture] = capture;
        for (int parameter = 0; parameter < function.ParameterCount; parameter++)
        {
            if (plan.Fields[parameter].Length == 0)
            { slots[function.CaptureCount + parameter] = next++; types.Add(function.ParameterTypes![parameter]); continue; }
            foreach (string field in plan.Fields[parameter]) { fieldSlots.Add((parameter, field), next++); types.Add(TypeRef.Any); }
        }
        for (int local = function.CaptureCount + function.ParameterCount; local < slots.Length; local++) slots[local] = next++;
        var code = new List<Instruction>(); var positions = new int[function.Instructions.Count + 1]; var branches = new List<int>();
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            positions[pc] = code.Count;
            var instruction = function.Instructions[pc];
            int parameter = instruction.A - function.CaptureCount;
            if (instruction.Op == OpCode.LoadLocal && parameter >= 0 && parameter < plan.Fields.Length && plan.Fields[parameter].Length != 0)
            {
                positions[pc + 1] = code.Count;
                var member = function.Instructions[++pc];
                code.Add(new(OpCode.LoadLocal, fieldSlots[(parameter, member.Text!)], Span: member.Span)); continue;
            }
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(code.Count);
            code.Add(HasSlot(instruction.Op) ? instruction with { A = slots[instruction.A] } : instruction);
        }
        positions[^1] = code.Count;
        Relocate(code, branches, positions);
        return function with
        {
            Name = function.Name + "$sra",
            ParameterCount = plan.Count,
            ParameterTypes = types,
            LocalCount = next,
            Instructions = code
        };
    }

    private static FunctionCode RewriteCalls(FunctionCode function, Dictionary<int, Rewrite> rewrites, Dictionary<int, int> clones)
    {
        var code = new List<Instruction>(); var positions = new int[function.Instructions.Count + 1]; var branches = new List<int>();
        int locals = function.LocalCount;
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            positions[pc] = code.Count;
            var instruction = function.Instructions[pc];
            if (!rewrites.TryGetValue(pc, out var rewrite))
            {
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(code.Count);
                code.Add(instruction); continue;
            }
            var staged = new int[instruction.A];
            // Every actual argument has already been evaluated, including writes
            // that can change an earlier aggregate argument's current fields.
            for (int parameter = staged.Length - 1; parameter >= 0; parameter--)
            { staged[parameter] = locals++; code.Add(new(OpCode.InitializeLocal, staged[parameter], Span: instruction.Span)); }
            int clone = clones[rewrite.Target];
            code.Add(new(OpCode.SpecializeClosure, clone, Span: instruction.Span));
            for (int parameter = 0; parameter < staged.Length; parameter++)
            {
                var fields = rewrite.Plan.Fields[parameter];
                if (fields.Length == 0) { code.Add(new(OpCode.LoadLocal, staged[parameter], Span: instruction.Span)); continue; }
                foreach (string field in fields)
                {
                    code.Add(new(OpCode.LoadLocal, staged[parameter], Span: instruction.Span));
                    code.Add(new(OpCode.GetMember, Text: field, Span: instruction.Span));
                }
            }
            code.Add(instruction with { A = rewrite.Plan.Count, B = clone + 1, Text = null });
        }
        positions[^1] = code.Count;
        Relocate(code, branches, positions);
        return function with { LocalCount = locals, Instructions = code };
    }

    private static void Relocate(List<Instruction> code, IEnumerable<int> branches, IReadOnlyList<int> positions)
    {
        foreach (int pc in branches)
        {
            var instruction = code[pc];
            code[pc] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] }
                : instruction with { A = positions[instruction.A] };
        }
    }

    private static bool Equivalent(FunctionCode left, FunctionCode right) => left.ParameterCount == right.ParameterCount
        && left.CaptureCount == right.CaptureCount && left.LocalCount == right.LocalCount && left.ReturnType == right.ReturnType
        && left.ParameterTypes is not null && right.ParameterTypes is not null && left.ParameterTypes.SequenceEqual(right.ParameterTypes)
        && left.Instructions.SequenceEqual(right.Instructions);

    private static bool HasSlot(OpCode op) => op is OpCode.LoadLocal or OpCode.LoadStorage or OpCode.LoadCell or OpCode.StoreLocal
        or OpCode.InitializeLocal or OpCode.IteratorNext;
}
