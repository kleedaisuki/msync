namespace Msync.Optimization;

internal sealed record AffineScanSource(bool NumberItems, IReadOnlySet<string> NumericFields);

internal sealed record AffineScanFacts(AffineScanSource Source,
    IReadOnlyDictionary<int, TypeKind> LocalTypes, IReadOnlySet<int> CapturedSlots,
    IReadOnlyList<TypeKind> ConstantTypes);

// These are shape/provenance facts, not evaluated user values. In particular,
// inspecting a lazy producer never invokes its captured interpreter.
internal sealed class AffineScanProvenance(Module module, FunctionCode function)
{
    private sealed record Closure(int Function, Fact[] Captures);
    private sealed record Sequence(TypeKind ItemType, Fact[]? Items = null,
        string? Deferred = null, Closure? Producer = null, bool Frozen = false,
        IReadOnlySet<string>? NumericFields = null);
    private sealed record Fact(TypeKind Kind, Sequence? Sequence = null,
        IReadOnlySet<string>? NumericFields = null, Closure? Closure = null, int Cell = -1,
        TypeRef? DenotedType = null);

    private static readonly Fact unknown = new(TypeKind.Any);
    private static readonly Fact number = new(TypeKind.Number);
    private static readonly Fact nil = new(TypeKind.Null);
    private static readonly IReadOnlySet<string> noFields = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<(int Pc, int Slot), AffineScanFacts> facts = [];
    private readonly HashSet<int> captured = function.Instructions.Where(item => item.Op == OpCode.LoadCell)
        .Select(item => item.A).Concat(Enumerable.Range(0, function.CaptureCount)).ToHashSet();
    private readonly TypeKind[] constantTypes = module.Constants.Select(item => item.Type.Kind).ToArray();

    internal AffineScanFacts? At(int pc, int slot) => facts.GetValueOrDefault((pc, slot));

    internal void Analyze()
    {
        var locals = Enumerable.Repeat(unknown, function.LocalCount).ToArray();
        for (int index = 0; index < function.ParameterCount; index++)
            locals[function.CaptureCount + index] = Typed(function.ParameterTypes?[index]);
        List<Fact> stack = [];
        Dictionary<int, Fact[]> loopEntries = [];
        HashSet<int> joins = function.Instructions.SelectMany((item, pc) =>
            item.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse && item.A > pc ? [item.A] : Array.Empty<int>()).ToHashSet();
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            if (joins.Contains(pc)) { Array.Fill(locals, unknown); stack.Clear(); }
            Instruction instruction = function.Instructions[pc];
            if (IsScanSource(pc, instruction)) Observe(pc, instruction.A, locals);
            if (instruction.Op == OpCode.IteratorNext) loopEntries[pc] = (Fact[])locals.Clone();
            if (instruction.Op == OpCode.Jump && instruction.A <= pc)
            {
                if (loopEntries.TryGetValue(instruction.A, out var before))
                    for (int slot = 0; slot < locals.Length; slot++) locals[slot] = Join(before[slot], locals[slot]);
                else Array.Fill(locals, unknown);
                stack.Clear();
                continue;
            }
            if (!Step(instruction, locals, stack, [], summary: false))
            {
                stack.Clear();
                if (instruction.Op is OpCode.Call or OpCode.NativeCall or OpCode.ProcessCall) Invalidate(locals, capturedOnly: true);
            }
        }
    }

    private bool IsScanSource(int pc, Instruction instruction) => instruction.Op == OpCode.LoadLocal
        && pc + 1 < function.Instructions.Count && function.Instructions[pc + 1].Op == OpCode.GetIterator;

    private void Observe(int pc, int slot, Fact[] locals)
    {
        if (captured.Contains(slot) || locals[slot].Sequence is not { Frozen: true } sequence) return;
        bool numeric = sequence.ItemType == TypeKind.Number;
        var fields = sequence.NumericFields ?? noFields;
        if (!numeric && fields.Count == 0) return;
        facts[(pc, slot)] = new(new(numeric, fields),
            locals.Select((value, index) => (value, index)).ToDictionary(pair => pair.index, pair => pair.value.Kind),
            captured, constantTypes);
    }

    private bool Step(Instruction instruction, Fact[] locals, List<Fact> stack,
        HashSet<int> active, bool summary)
    {
        Fact Pop()
        {
            if (stack.Count == 0) return unknown;
            Fact item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item;
        }
        Fact[] Many(int count)
        {
            if (count > stack.Count) { stack.Clear(); return Enumerable.Repeat(unknown, count).ToArray(); }
            Fact[] values = stack.GetRange(stack.Count - count, count).ToArray();
            stack.RemoveRange(stack.Count - count, count); return values;
        }
        Fact Resolve(Fact value) => value.Cell >= 0 ? locals[value.Cell] : value;
        void Push(Fact value) => stack.Add(value);
        switch (instruction.Op)
        {
            case OpCode.Nop: return true;
            case OpCode.Null: Push(nil); return true;
            case OpCode.True:
            case OpCode.False: Push(new(TypeKind.Bool)); return true;
            case OpCode.Constant: Push(Constant(module.Constants[instruction.A])); return true;
            case OpCode.LoadLocal: Push(locals[instruction.A] with { Cell = -1 }); return true;
            case OpCode.LoadStorage:
                Push(summary ? locals[instruction.A] with { Cell = -1 }
                    : captured.Contains(instruction.A) ? locals[instruction.A] with { Cell = instruction.A }
                    : locals[instruction.A]); return true;
            case OpCode.LoadCell:
                if (summary) return false;
                Push(locals[instruction.A] with { Cell = instruction.A }); return true;
            case OpCode.StoreLocal:
            case OpCode.InitializeLocal:
                var assigned = Pop();
                if (!summary && assigned.Cell != -1)
                {
                    captured.Add(instruction.A);
                    if (assigned.Cell >= 0) captured.Add(assigned.Cell);
                }
                locals[instruction.A] = assigned; return true;
            case OpCode.Pop: Pop(); return true;
            case OpCode.Dup:
                var value = Pop(); Push(value); Push(value); return true;
            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:
            case OpCode.Remainder:
                var right = Resolve(Pop()); var left = Resolve(Pop());
                Push(left.Kind == TypeKind.Number && right.Kind == TypeKind.Number ? number : unknown); return true;
            case OpCode.Negate:
                Push(Resolve(Pop()).Kind == TypeKind.Number ? number : unknown); return true;
            case OpCode.GetMember:
                var receiver = Resolve(Pop());
                Push(receiver.NumericFields?.Contains(instruction.Text!) == true ? number : unknown); return true;
            case OpCode.SetMember:
                if (summary) return false;
                Pop(); Pop(); Invalidate(locals, capturedOnly: false); Push(nil); return true;
            case OpCode.MakeScope:
                var fields = Many(instruction.A).Select(Resolve).ToArray();
                string[] names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (names.Length != fields.Length) return false;
                Push(new(TypeKind.Scope, NumericFields: names.Where((_, index) => fields[index].Kind == TypeKind.Number)
                    .ToHashSet(StringComparer.Ordinal))); return true;
            case OpCode.MakeRange:
                var items = Many(instruction.A).Select(Resolve).ToArray();
                Push(Snapshot(items)); return true;
            case OpCode.MakeLazyRange:
                var lazyItems = Many(instruction.A);
                TypeKind itemType = instruction.B > 0 && module.Constants[instruction.B - 1] is TypeValue type
                    ? type.Data.Kind : TypeKind.Any;
                Push(new(TypeKind.Range, new(itemType, lazyItems, instruction.Text))); return true;
            case OpCode.MakeClosure:
                Push(new(TypeKind.Function, Closure: new(instruction.A, Many(instruction.B)))); return true;
            case OpCode.SpecializeClosure:
                var closure = Pop();
                Push(instruction.A >= 0 && closure.Closure is { } known
                    ? closure with { Closure = known with { Function = instruction.A } } : unknown); return true;
            case OpCode.DeferRange:
                var producer = Pop();
                TypeKind produced = producer.Closure is { } rangeBody
                    ? module.Functions[rangeBody.Function].ReturnType.Element?.Kind ?? TypeKind.Any : TypeKind.Any;
                Push(new(TypeKind.Range, new(produced, Producer: producer.Closure))); return true;
            case OpCode.NativeCall:
                var arguments = Many(instruction.A).Select(Resolve).ToArray();
                Fact output = Native(instruction.Text!, arguments, locals, active, summary);
                if (!summary && NativeSignatures.TryGet(instruction.Text!, out var signature)
                    && (signature.Effect == NativeEffect.Write
                        || (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates)) != 0))
                    Invalidate(locals, capturedOnly: true);
                Push(output); return output != unknown || !summary;
            case OpCode.GetCapture:
                var environment = Pop().Closure;
                Push(environment is not null && instruction.A < environment.Captures.Length
                    ? environment.Captures[instruction.A] : unknown with { Cell = -2 }); return true;
            case OpCode.Call:
                var supplied = Many(instruction.A).Select(Resolve).ToArray();
                var callable = Resolve(Pop()).Closure;
                if (!summary) return false;
                Push(callable is null ? unknown : Summarize(callable, supplied, locals, active));
                return stack[^1] != unknown;
            case OpCode.GetIterator:
                Push(Pop()); return true;
            case OpCode.IteratorNext:
                var source = locals[instruction.A].Sequence;
                Push(source is null ? unknown : new(source.ItemType,
                    NumericFields: source.NumericFields)); return true;
            case OpCode.Return: return true;
            default: return false;
        }
    }

    private Fact Native(string name, Fact[] arguments, Fact[] locals, HashSet<int> active, bool summary)
    {
        if (!NativeSignatures.TryGet(name, out var signature)) return unknown;
        if (signature.ResultRule == NativeResultRule.CastTarget && arguments.Length > 1)
            return Typed(arguments[1].DenotedType);
        if ((signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0 && arguments.Length != 0)
        {
            var result = Materialize(arguments[0], locals, active);
            return name == "storage.buffer_new" && result.Sequence is { } sequence
                ? result with { Sequence = sequence with { Frozen = false } } : result;
        }
        if (signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length != 0
            && signature.Result.Kind == TypeKind.Range)
            return (signature.Behavior.GetValueOrDefault() & NativeBehavior.PreservesIdentity) != 0
                ? arguments[0] : Typed(new(TypeKind.Range,
                    arguments[0].Sequence is { } input ? new TypeRef(input.ItemType) : TypeRef.Any));
        if (name == "ranges.repeat" && arguments.Length == 2)
            return new(TypeKind.Range, new(arguments[0].Kind, NumericFields: arguments[0].NumericFields));
        if (summary && (signature.Effect != NativeEffect.Pure
            || (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.LocalMemory)) != 0))
            return unknown;
        return Typed(signature.Result);
    }

    private Fact Materialize(Fact source, Fact[] locals, HashSet<int> active)
    {
        if (source.Sequence is not { } sequence) return unknown;
        if (sequence.Producer is { } producer)
        {
            Fact result = Materialize(Summarize(producer, [], locals, active), locals, active);
            return result != unknown ? result : new(TypeKind.Range, new(sequence.ItemType, Frozen: true));
        }
        if (sequence.Items is not { } items)
            return new(TypeKind.Range, sequence with { Frozen = true });
        Fact[] values = new Fact[items.Length];
        for (int index = 0; index < items.Length; index++)
        {
            Fact value = items[index];
            if (value.Cell >= 0) value = locals[value.Cell];
            values[index] = sequence.Deferred is not null && sequence.Deferred[index] == '1'
                ? value.Closure is { } closure ? Summarize(closure, [], locals, active) : unknown : value;
        }
        return Snapshot(values);
    }

    private Fact Summarize(Closure closure, Fact[] arguments, Fact[] caller, HashSet<int> active)
    {
        if (!active.Add(closure.Function)) return unknown;
        try
        {
            FunctionCode body = module.Functions[closure.Function];
            if (body.ParameterCount != arguments.Length) return unknown;
            Fact[] locals = Enumerable.Repeat(unknown, body.LocalCount).ToArray();
            for (int index = 0; index < closure.Captures.Length; index++)
                locals[index] = closure.Captures[index].Cell >= 0 ? caller[closure.Captures[index].Cell] : closure.Captures[index];
            Array.Copy(arguments, 0, locals, body.CaptureCount, arguments.Length);
            List<Fact> stack = [];
            foreach (Instruction instruction in body.Instructions)
            {
                if (instruction.Op == OpCode.Return) return stack.Count == 0 ? nil : stack[^1];
                if (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal && instruction.A < body.CaptureCount) return unknown;
                if (!Step(instruction, locals, stack, active, summary: true)) return unknown;
            }
            return unknown;
        }
        finally { active.Remove(closure.Function); }
    }

    private static Fact Constant(Value value) => value switch
    {
        TypeValue type => new(TypeKind.Type, DenotedType: type.Data),
        RangeValue { SnapshotValues: { } values, Resolve: null, Selection: null } => Snapshot(values.Select(Constant).ToArray()),
        ScopeValue scope => new(TypeKind.Scope, NumericFields: scope.Members.Where(pair => pair.Value.Type == TypeRef.Number)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal)),
        FunctionValue { NativeName: null } function => new(TypeKind.Function,
            Closure: new(function.Function, (function.Captures ?? []).Select(Constant).ToArray())),
        CellValue => new(value.Type.Kind, Cell: -2),
        _ => Typed(value.Type)
    };

    private static Fact Typed(TypeRef? type) => type is null ? unknown : type.Kind == TypeKind.Range
        ? new(TypeKind.Range, new(type.Element?.Kind ?? TypeKind.Any)) : new(type.Kind);

    private static Fact Snapshot(Fact[] values)
    {
        TypeKind kind = values.Length == 0 ? TypeKind.Never : values[0].Kind;
        foreach (Fact value in values) kind |= value.Kind;
        HashSet<string>? fields = values.FirstOrDefault()?.NumericFields?.ToHashSet(StringComparer.Ordinal);
        foreach (Fact value in values)
            if (value.NumericFields is null) fields = null;
            else fields?.IntersectWith(value.NumericFields);
        return new(TypeKind.Range, new(kind, Frozen: true, NumericFields: fields));
    }

    private static Fact Join(Fact left, Fact right)
    {
        if (ReferenceEquals(left, right)) return left;
        if (left.Kind == TypeKind.Number && right.Kind == TypeKind.Number) return number;
        if (left.Sequence is not null && ReferenceEquals(left.Sequence, right.Sequence)) return left;
        return unknown;
    }

    private void Invalidate(Fact[] locals, bool capturedOnly)
    {
        // Field/callback writes can change a row's payload, but cannot change
        // the membership of an independently owned eager snapshot.
        for (int index = 0; index < locals.Length; index++)
        {
            if (capturedOnly && captured.Contains(index)) { locals[index] = unknown; continue; }
            locals[index] = ForgetSchema(locals[index]);
        }
    }

    private static Fact ForgetSchema(Fact value)
    {
        if (value.NumericFields is null && value.Sequence is null && value.Closure is null) return value;
        return value with
        {
            NumericFields = null,
            Sequence = value.Sequence is { } sequence ? sequence with
            {
                NumericFields = null,
                Items = sequence.Items?.Select(ForgetSchema).ToArray()
            } : null,
            Closure = value.Closure is { } closure ? closure with
            {
                Captures = closure.Captures.Select(ForgetSchema).ToArray()
            } : null
        };
    }
}
