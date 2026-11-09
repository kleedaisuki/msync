namespace Msync.Core;

internal static class InterproceduralSpecialization
{
    private sealed record Fact(Value? Constant = null, int? Function = null, int CaptureCount = 0,
        IReadOnlyDictionary<string, Fact>? Members = null, bool Storage = false);
    private sealed record Candidate(int Position, int Target, Value?[] Constants);
    private sealed record Parameters(bool[] Replaceable, bool[] Unused, bool[] Control);
    private sealed record Clone(int Function, bool[] Removed);
    private static readonly Fact Unknown = new();
    private static readonly Fact UnknownStorage = new(Storage: true);
    private sealed class Literals(List<Value> values)
    {
        private Dictionary<Value, int>? indices;
        internal int Intern(Value value)
        {
            if (indices is null)
            {
                indices = new(ValueRepresentation.Comparer);
                for (var index = 0; index < values.Count; index++)
                    if (Immutable(values[index])) indices.TryAdd(values[index], index);
            }
            if (indices.TryGetValue(value, out var existing)) return existing;
            var added = values.Count; values.Add(value); indices.Add(value, added); return added;
        }
    }

    internal static Module Optimize(Module module, int level)
    {
        if (level < 2 || module.PreserveLocals) return module;
        var constants = module.Constants.ToList();
        var literals = new Literals(constants);
        var functions = module.Functions.ToList();
        var cloneCosts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            var marker = function.Name.LastIndexOf("$ipa(", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var origin = function.Name[..marker];
                cloneCosts[origin] = cloneCosts.GetValueOrDefault(origin) + function.Instructions.Count;
            }
        }
        var descriptions = new Dictionary<int, Parameters>();
        var clones = new Dictionary<string, Clone>(StringComparer.Ordinal);
        var spent = new Dictionary<int, int>();
        for (var caller = 0; caller < module.Functions.Count; caller++)
        {
            var body = functions[caller];
            if (!body.Instructions.Any(instruction => instruction.Op == OpCode.Call)) continue;
            var rewrites = new Dictionary<int, Clone>();
            foreach (var candidate in Calls(module, body))
            {
                var target = module.Functions[candidate.Target];
                if (target.ParameterCount == 0 || target.ParameterCount != candidate.Constants.Length) continue;
                if (!descriptions.TryGetValue(candidate.Target, out var parameters))
                    descriptions.Add(candidate.Target, parameters = Describe(target));
                var selected = new Value?[target.ParameterCount];
                var removed = parameters.Unused.ToArray();
                for (var parameter = 0; parameter < selected.Length; parameter++)
                    if (parameters.Replaceable[parameter] && parameters.Control[parameter]
                        && candidate.Constants[parameter] is { } value
                        && target.ParameterTypes is { } types && types[parameter] == value.Type)
                    { selected[parameter] = value; removed[parameter] = true; }
                if (!removed.Any(value => value)) continue;
                var key = Key(candidate.Target, selected, removed, literals);
                if (!clones.TryGetValue(key, out var clone))
                {
                    // Names carry cost provenance only; clone reuse requires the
                    // current call's exact target and representation tuple.
                    if (!spent.TryGetValue(candidate.Target, out var cost))
                        cost = cloneCosts.GetValueOrDefault(target.Name);
                    var budget = target.Instructions.Count * (level >= 3 ? 2 : 1);
                    if (cost + target.Instructions.Count > budget) continue;
                    var id = functions.Count;
                    functions.Add(Specialize(target, selected, removed, literals,
                        target.Name + "$ipa(" + candidate.Target + "," + id + ")"));
                    clone = new(id, removed); clones.Add(key, clone);
                    spent[candidate.Target] = cost + target.Instructions.Count;
                    cloneCosts[target.Name] = cloneCosts.GetValueOrDefault(target.Name) + target.Instructions.Count;
                }
                rewrites[candidate.Position] = clone;
            }
            if (rewrites.Count > 0) functions[caller] = RewriteCalls(body, rewrites);
        }
        return functions.Count == module.Functions.Count ? module : module with { Functions = functions, Constants = constants, Blocks = null };
    }

    private static string Key(int target, Value?[] values, bool[] removed, Literals literals)
    {
        var parts = new string[values.Length + 1]; parts[0] = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not { } value) { parts[index + 1] = removed[index] ? "d" : "k"; continue; }
            var literal = literals.Intern(value);
            parts[index + 1] = "v" + literal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return string.Join(':', parts);
    }

    private static FunctionCode Specialize(FunctionCode function, Value?[] values, bool[] removed,
        Literals literals, string name)
    {
        var slots = new int[function.LocalCount];
        for (var capture = 0; capture < function.CaptureCount; capture++) slots[capture] = capture;
        var next = function.CaptureCount;
        for (var parameter = 0; parameter < function.ParameterCount; parameter++)
            if (!removed[parameter]) slots[function.CaptureCount + parameter] = next++;
        for (var slot = function.CaptureCount + function.ParameterCount; slot < slots.Length; slot++) slots[slot] = next++;
        Instruction Literal(Value value, SourceSpan? span)
        {
            if (value is NullValue) return new(OpCode.Null, Span: span);
            if (value is BoolValue boolean) return new(boolean.Data ? OpCode.True : OpCode.False, Span: span);
            return new(OpCode.Constant, literals.Intern(value), Span: span);
        }
        var code = function.Instructions.Select(instruction =>
        {
            if (instruction.Op == OpCode.LoadLocal && instruction.A >= function.CaptureCount
                && instruction.A < function.CaptureCount + function.ParameterCount
                && values[instruction.A - function.CaptureCount] is { } constant) return Literal(constant, instruction.Span);
            return HasSlot(instruction.Op) ? instruction with { A = slots[instruction.A] } : instruction;
        }).ToArray();
        return function with
        {
            Name = name,
            ParameterCount = removed.Count(value => !value),
            LocalCount = next,
            Instructions = code,
            ParameterTypes = function.ParameterTypes?.Where((_, index) => !removed[index]).ToArray()
        };
    }

    private static FunctionCode RewriteCalls(FunctionCode function, IReadOnlyDictionary<int, Clone> rewrites)
    {
        var code = new List<Instruction>(); var positions = new int[function.Instructions.Count + 1];
        var branches = new List<int>(); var locals = function.LocalCount;
        foreach (var (instruction, position) in function.Instructions.Select((instruction, position) => (instruction, position)))
        {
            positions[position] = code.Count;
            if (!rewrites.TryGetValue(position, out var clone))
            {
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext) branches.Add(code.Count);
                code.Add(instruction); continue;
            }
            var kept = new int[clone.Removed.Length];
            // Argument expressions have already run. Remove their input values,
            // never the evaluations that produced them.
            for (var argument = clone.Removed.Length - 1; argument >= 0; argument--)
            {
                if (clone.Removed[argument]) code.Add(new(OpCode.Pop, Span: instruction.Span));
                else { kept[argument] = locals++; code.Add(new(OpCode.InitializeLocal, kept[argument], Span: instruction.Span)); }
            }
            code.Add(new(OpCode.SpecializeClosure, clone.Function, Span: instruction.Span));
            for (var argument = 0; argument < kept.Length; argument++)
                if (!clone.Removed[argument]) code.Add(new(OpCode.LoadLocal, kept[argument], Span: instruction.Span));
            code.Add(instruction with { A = clone.Removed.Count(value => !value), B = clone.Function + 1, Text = null });
        }
        positions[^1] = code.Count;
        foreach (var position in branches)
        {
            var instruction = code[position];
            code[position] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] } : instruction with { A = positions[instruction.A] };
        }
        return function with { Instructions = code, LocalCount = locals };
    }

    private static Parameters Describe(FunctionCode function)
    {
        var replaceable = Enumerable.Repeat(true, function.ParameterCount).ToArray();
        var unused = Enumerable.Repeat(true, function.ParameterCount).ToArray();
        foreach (var instruction in function.Instructions.Where(instruction => HasSlot(instruction.Op)))
        {
            var parameter = instruction.A - function.CaptureCount;
            if (parameter < 0 || parameter >= function.ParameterCount) continue;
            unused[parameter] = false;
            if (instruction.Op != OpCode.LoadLocal) replaceable[parameter] = false;
        }
        var control = new bool[function.ParameterCount];
        var starts = Starts(function.Instructions);
        var locals = new HashSet<int>[function.LocalCount];
        var stack = new List<HashSet<int>>();
        void Reset()
        {
            stack.Clear();
            for (var index = 0; index < locals.Length; index++) locals[index] = [];
            for (var parameter = 0; parameter < replaceable.Length; parameter++)
                if (replaceable[parameter]) locals[function.CaptureCount + parameter] = [parameter];
        }
        HashSet<int> Pop() { if (stack.Count == 0) return []; var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item; }
        Reset();
        for (var position = 0; position < function.Instructions.Count; position++)
        {
            if (starts.Contains(position)) Reset();
            var instruction = function.Instructions[position];
            switch (instruction.Op)
            {
                case OpCode.LoadLocal: stack.Add(locals[instruction.A]); break;
                case OpCode.StoreLocal: case OpCode.InitializeLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Dup: var item = Pop(); stack.Add(item); stack.Add(item); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Not: case OpCode.IsNull: case OpCode.Negate: break;
                case OpCode.Equal:
                case OpCode.NotEqual:
                case OpCode.Less:
                case OpCode.LessEqual:
                case OpCode.Greater:
                case OpCode.GreaterEqual:
                case OpCode.IsType:
                case OpCode.Add:
                case OpCode.Subtract:
                case OpCode.Multiply:
                case OpCode.Divide: case OpCode.Remainder:
                    var right = Pop(); var left = new HashSet<int>(Pop()); left.UnionWith(right); stack.Add(left); break;
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                    foreach (var parameter in Pop()) control[parameter] = true;
                    Reset(); break;
                case OpCode.Constant:
                case OpCode.Null:
                case OpCode.True:
                case OpCode.False:
                case OpCode.LoadCell:
                case OpCode.LoadStorage:
                case OpCode.SetSingleton: Pop(); stack.Add([]); break;
                case OpCode.GetSingleton: case OpCode.LoadBuiltin: stack.Add([]); break;
                case OpCode.Nop: case OpCode.Barrier: break;
                default: Reset(); break;
            }
        }
        return new(replaceable, unused, control);
    }

    private static IEnumerable<Candidate> Calls(Module module, FunctionCode function)
    {
        var starts = Starts(function.Instructions);
        var mutable = function.Instructions.Where(instruction => instruction.Op is OpCode.LoadCell or OpCode.LoadStorage)
            .Select(instruction => instruction.A).Concat(Enumerable.Range(0, function.CaptureCount)).ToHashSet();
        var locals = new Fact[function.LocalCount]; Array.Fill(locals, Unknown);
        var stack = new List<Fact>();
        Fact Pop() { if (stack.Count == 0) return Unknown; var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item; }
        Fact[] PopMany(int count) { var result = new Fact[count]; for (var index = count - 1; index >= 0; index--) result[index] = Pop(); return result; }
        void Invalidate()
        {
            for (var slot = 0; slot < locals.Length; slot++) if (mutable.Contains(slot) || locals[slot].Members is not null) locals[slot] = Unknown;
            for (var slot = 0; slot < stack.Count; slot++) if (stack[slot].Members is not null) stack[slot] = Unknown;
        }
        for (var position = 0; position < function.Instructions.Count; position++)
        {
            if (starts.Contains(position)) { stack.Clear(); Array.Fill(locals, Unknown); }
            var instruction = function.Instructions[position];
            switch (instruction.Op)
            {
                case OpCode.Nop: case OpCode.Barrier: break;
                case OpCode.Constant:
                    var value = module.Constants[instruction.A];
                    stack.Add(value is FunctionValue { NativeName: null } callable ? new(Function: callable.Function, CaptureCount: callable.Captures?.Count ?? 0)
                        : Immutable(value) ? new(value) : Unknown); break;
                case OpCode.True: stack.Add(new(Value.True)); break;
                case OpCode.False: stack.Add(new(Value.False)); break;
                case OpCode.Null: stack.Add(new(Value.Null)); break;
                case OpCode.LoadLocal: stack.Add(locals[instruction.A].Storage ? locals[instruction.A] with { Storage = false } : locals[instruction.A]); break;
                case OpCode.LoadCell: case OpCode.LoadStorage: stack.Add(UnknownStorage); break;
                case OpCode.StoreLocal:
                case OpCode.InitializeLocal:
                    var stored = Pop();
                    if (stored.Storage) mutable.Add(instruction.A);
                    if (mutable.Contains(instruction.A)) Invalidate();
                    locals[instruction.A] = stored.Storage ? UnknownStorage : stored;
                    break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup: var duplicate = Pop(); stack.Add(duplicate); stack.Add(duplicate); break;
                case OpCode.MakeClosure: PopMany(instruction.B); stack.Add(new(Function: instruction.A, CaptureCount: instruction.B)); break;
                case OpCode.SpecializeClosure:
                    var closure = Pop();
                    var specialized = closure.Function is null ? null : instruction.Text is null ? instruction.A
                        : FunctionDispatch.Parse(instruction.Text).TryGetValue(closure.Function.Value, out var selected) ? selected : (int?)null;
                    stack.Add(closure with { Function = specialized }); break;
                case OpCode.MakeScope:
                    var values = PopMany(instruction.A); var names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    stack.Add(names.Distinct(StringComparer.Ordinal).Count() == names.Length
                        ? new(Members: names.Select((name, index) => KeyValuePair.Create(name, values[index])).ToDictionary()) : Unknown); break;
                case OpCode.GetMember:
                    var scope = Pop(); stack.Add(scope.Members is { } members && members.TryGetValue(instruction.Text!, out var member) ? member : Unknown); break;
                case OpCode.Call:
                    var arguments = PopMany(instruction.A); var callee = Pop();
                    var target = callee.Function;
                    if (target is not null && instruction.Text is { } mapping)
                        target = FunctionDispatch.Parse(mapping).TryGetValue(target.Value, out var dispatched) ? dispatched : null;
                    else if (target is not null && instruction.B > 0) target = instruction.B - 1;
                    if (target is { } known && known >= 0 && known < module.Functions.Count
                        && callee.CaptureCount == module.Functions[known].CaptureCount)
                        yield return new(position, known, arguments.Select(argument => argument.Constant).ToArray());
                    stack.Add(Unknown); Invalidate(); break;
                case OpCode.NativeCall: PopMany(instruction.A); stack.Add(Unknown); Invalidate(); break;
                case OpCode.ProcessCall: PopMany(instruction.A + 1); stack.Add(Unknown); Invalidate(); break;
                case OpCode.SetMember: PopMany(2); stack.Add(Unknown); Invalidate(); break;
                case OpCode.MakeRange: case OpCode.MakeLazyRange: PopMany(instruction.A); stack.Add(Unknown); break;
                case OpCode.GetCapture: Pop(); stack.Add(UnknownStorage); break;
                case OpCode.Not:
                case OpCode.IsNull:
                case OpCode.Negate:
                case OpCode.Convert:
                case OpCode.GetIterator:
                case OpCode.SelectPath:
                case OpCode.DeferRange: Pop(); stack.Add(Unknown); break;
                case OpCode.RegexMatch: PopMany(instruction.Text is null ? 2 : 1); stack.Add(Unknown); break;
                case OpCode.SetSingleton: Pop(); stack.Add(Unknown); break;
                case OpCode.GetSingleton: case OpCode.LoadBuiltin: stack.Add(Unknown); break;
                case OpCode.Acquire: if (instruction.A != 0) { Pop(); stack.Add(Unknown); } Invalidate(); break;
                case OpCode.IteratorNext: stack.Add(Unknown); Invalidate(); break;
                case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(); stack.Clear(); Array.Fill(locals, Unknown); break;
                case OpCode.Jump: case OpCode.Return: stack.Clear(); Array.Fill(locals, Unknown); break;
                default: PopMany(2); stack.Add(Unknown); break;
            }
        }
    }

    private static HashSet<int> Starts(IReadOnlyList<Instruction> instructions) => instructions
        .Where(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse).Select(instruction => instruction.A)
        .Concat(instructions.Where(instruction => instruction.Op == OpCode.IteratorNext).Select(instruction => instruction.B)).ToHashSet();
    private static bool Immutable(Value value) => value is NullValue or BoolValue or NumberValue or StringValue or TypeValue or PathValue or ConnectionValue;
    private static bool HasSlot(OpCode op) => op is OpCode.LoadLocal or OpCode.LoadCell or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.IteratorNext;
}
