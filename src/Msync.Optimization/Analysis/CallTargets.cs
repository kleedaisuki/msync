namespace Msync.Optimization;

internal static class CallTargets
{
    internal readonly record struct Result(Dictionary<int, int> Targets, IReadOnlySet<int>? RawArguments);

    private sealed record Shape(int? Function = null, int? Captures = null, IReadOnlyDictionary<string, Fact>? Members = null);

    private readonly record struct Fact(bool NonCell, Shape? Shape = null, bool? Bool = null)
    {
        internal static readonly Fact Plain = new(true);

        internal Fact Meet(Fact other) => new(NonCell && other.NonCell, Shape == other.Shape ? Shape : null,
            Bool == other.Bool ? Bool : null);
    }

    private sealed class State(Fact[] locals, List<Fact> stack, bool hasMembers = false)
    {
        internal State Copy() => new((Fact[])Locals.Clone(), [.. Stack], HasMembers);
        internal Fact[] Locals { get; } = locals;
        internal List<Fact> Stack { get; } = stack;
        internal bool HasMembers { get; set; } = hasMembers;

        internal bool Meet(State incoming)
        {
            bool changed = false;
            for (int slot = 0; slot < Locals.Length; slot++)
                changed |= Merge(ref Locals[slot], incoming.Locals[slot]);
            var values = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Stack);
            for (int slot = 0; slot < values.Length; slot++)
                changed |= Merge(ref values[slot], incoming.Stack[slot]);
            return changed;

            static bool Merge(ref Fact existing, Fact incoming)
            {
                Fact next = existing.Meet(incoming);
                if (existing == next) return false;
                existing = next;
                return true;
            }
        }

        internal void InvalidateMembers()
        {
            if (!HasMembers) return;
            HasMembers = false;
            for (int slot = 0; slot < Locals.Length; slot++)
                if (Locals[slot].Shape?.Members is not null) Locals[slot] = Fact.Plain;
            for (int slot = 0; slot < Stack.Count; slot++)
                if (Stack[slot].Shape?.Members is not null) Stack[slot] = Fact.Plain;
        }

        internal Fact Pop()
        {
            var value = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return value;
        }

        internal void Consume(int count) => Stack.RemoveRange(Stack.Count - count, count);
    }

    internal static Result Analyze(Module module, FunctionCode function, IReadOnlyList<Value> constants,
        bool requireKnownCallee, IReadOnlyList<FunctionCode>? layouts, ISet<int>? knownClosures = null, bool trackArguments = false,
        int captureCeiling = int.MaxValue, IReadOnlyList<bool>? plainReturns = null)
    {
        Dictionary<int, int> targets = [];
        HashSet<int>? rawArguments = null;
        var code = function.Instructions;
        if (!code.Any(instruction => instruction.Op == OpCode.Call)) return new(targets, null);

        var locals = new Fact[function.LocalCount];
        // A direct-call target's declared capture prefix alone is insufficient:
        // an imported specialization can supply extra cells.
        int firstPrivate = Math.Max(captureCeiling, function.CaptureCount + function.ParameterCount);
        if (firstPrivate < locals.Length)
            Array.Fill(locals, Fact.Plain, firstPrivate, locals.Length - firstPrivate);
        State entry = new(locals, []);

        if (!code.Any(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady or OpCode.IteratorNext))
        {
            for (int pc = 0; pc < code.Count && code[pc].Op != OpCode.Return; pc++)
                if (!Step(pc, entry)) return new([], null);
            return new(targets, rawArguments);
        }

        var cfg = new ControlFlowGraph(function);
        var inputs = new State?[cfg.Blocks.Count];
        var queued = new bool[cfg.Blocks.Count];
        Queue<int> work = new();
        bool inconsistent = false;
        Merge(0, entry);
        while (!inconsistent && work.TryDequeue(out int start))
        {
            queued[start] = false;
            Visit(start, inputs[start]!.Copy());
        }
        if (!inconsistent) return new(targets, rawArguments);
        if (knownClosures is not null)
            foreach (int pc in targets.Keys) knownClosures.Remove(pc);
        return new([], null);

        void Merge(int target, State state)
        {
            if (target < 0) return;
            if (inputs[target] is { } old)
            {
                // A source-unreachable edge can have no meaningful stack state.
                if (old.Stack.Count != state.Stack.Count) { inconsistent = true; return; }
                if (!old.Meet(state)) return;
            }
            else inputs[target] = state.Copy();
            if (queued[target]) return;
            queued[target] = true;
            work.Enqueue(target);
        }

        void Visit(int block, State state)
        {
            var range = cfg.Blocks[block];
            bool? condition = null;
            for (int pc = range.Start; pc < range.End; pc++)
            {
                var instruction = code[pc];
                switch (instruction.Op)
                {
                    case OpCode.Return: return;
                    case OpCode.Jump: case OpCode.JumpIfStaticReady: break;
                    case OpCode.JumpIfTrue:
                    case OpCode.JumpIfFalse:
                        if (state.Stack.Count == 0) { inconsistent = true; return; }
                        condition = state.Pop().Bool;
                        break;
                    case OpCode.IteratorNext:
                        state.InvalidateMembers();
                        break;
                    default:
                        if (!Step(pc, state)) { inconsistent = true; return; }
                        break;
                }
            }

            bool takesBranch = condition == (code[range.End - 1].Op == OpCode.JumpIfTrue);
            foreach (var edge in cfg.OutgoingEdges(block))
            {
                if (condition.HasValue && takesBranch != (edge.Kind == ControlFlowEdgeKind.Taken)) continue;
                if (edge.Kind == ControlFlowEdgeKind.IteratorYield)
                {
                    state.Stack.Add(default);
                    Merge(edge.Target, state);
                    state.Pop();
                }
                else Merge(edge.Target, state);
            }
        }

        bool Step(int pc, State state)
        {
            var instruction = code[pc];
            if (!ExecutionStackEffect.TryGet(module, instruction, state.Stack.Count, out var effect)) return false;
            int consumed = effect.Inputs;
            if (consumed > state.Stack.Count) return false;
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Constant: state.Stack.Add(Constant(constants[instruction.A])); break;
                case OpCode.True: state.Stack.Add(new(true, Bool: true)); break;
                case OpCode.False: state.Stack.Add(new(true, Bool: false)); break;
                case OpCode.Null:
                case OpCode.LoadBuiltin: state.Stack.Add(Fact.Plain); break;
                case OpCode.LoadStaticScope: state.Stack.Add(Fact.Plain); break;
                case OpCode.LoadStaticField: state.Stack.Add(default); break;
                case OpCode.LoadLocal:
                case OpCode.LoadStorage: state.Stack.Add(state.Locals[instruction.A]); break;
                case OpCode.LoadCell:
                    state.Locals[instruction.A] = default;
                    state.Stack.Add(default);
                    break;
                case OpCode.StoreLocal:
                    var assigned = state.Pop();
                    if (state.Locals[instruction.A].NonCell) state.Locals[instruction.A] = assigned;
                    else state.InvalidateMembers();
                    break;
                case OpCode.InitializeLocal: state.Locals[instruction.A] = state.Pop(); break;
                case OpCode.Dup: state.Stack.Add(state.Stack[^1]); break;
                case OpCode.MakeClosure:
                    state.Consume(consumed);
                    state.Stack.Add(new(true, new(instruction.A, instruction.B)));
                    break;
                case OpCode.SpecializeClosure:
                    state.Stack.Add(Specialize(state.Pop(), instruction, requireKnownCallee));
                    break;
                case OpCode.NewScope:
                    var scope = Scope(ScopeOperands.Construction(module, instruction), state.Stack);
                    state.HasMembers |= scope.Shape is not null;
                    state.Consume(consumed);
                    state.Stack.Add(scope);
                    break;
                case OpCode.LoadField:
                    var receiver = state.Pop().Shape;
                    state.Stack.Add(receiver?.Members is { } members && members.TryGetValue(ScopeOperands.Projection(module, instruction).Name, out var member)
                        ? member : default);
                    break;
                case OpCode.AdmitScope: break;
                case OpCode.Call:
                    var closure = state.Stack[state.Stack.Count - consumed].Shape;
                    var target = Select(instruction, closure, requireKnownCallee, layouts);
                    if (target is int known)
                    {
                        targets[pc] = known;
                        if (closure?.Function is not null && closure.Captures is not null) knownClosures?.Add(pc);
                        else knownClosures?.Remove(pc);
                        if (trackArguments)
                        {
                            bool raw = false;
                            for (int argument = state.Stack.Count - instruction.A; argument < state.Stack.Count; argument++)
                                raw |= !state.Stack[argument].NonCell;
                            if (raw) (rawArguments ??= []).Add(pc);
                            else rawArguments?.Remove(pc);
                        }
                    }
                    else
                    {
                        targets.Remove(pc);
                        knownClosures?.Remove(pc);
                        rawArguments?.Remove(pc);
                    }
                    state.Consume(consumed);
                    state.InvalidateMembers();
                    state.Stack.Add(target is int callee && plainReturns?[callee] == true ? Fact.Plain : default);
                    break;
                case OpCode.Not:
                    state.Stack.Add(new(true, Bool: state.Pop().Bool is bool value ? !value : null));
                    break;
                default:
                    state.Consume(consumed);
                    if (InvalidatesMembers(instruction)) state.InvalidateMembers();
                    if (effect.ProducesValue && PushedValue(instruction) is Fact result) state.Stack.Add(result);
                    break;
            }
            return true;
        }
    }

    private static Fact? PushedValue(Instruction instruction) => instruction.Op switch
    {
        OpCode.Acquire when instruction.A == 0 => null,
        OpCode.Pop or OpCode.Barrier or OpCode.ExecutePlan => null,
        OpCode.NativeCall or OpCode.ProcessCall or OpCode.GetCapture or OpCode.Qualify => default(Fact),
        _ => Fact.Plain
    };

    internal static bool ReturnsPlain(FunctionCode function, IReadOnlyList<Value> constants)
    {
        var code = function.Instructions;
        if (code.Count < 2 || code[^1].Op != OpCode.Return) return false;
        var producer = code[^2];
        bool plain = producer.Op switch
        {
            OpCode.Constant => constants[producer.A] is not CellValue,
            OpCode.Null or OpCode.True or OpCode.False or OpCode.Add or OpCode.Subtract or OpCode.Multiply
                or OpCode.Divide or OpCode.Remainder or OpCode.Negate or OpCode.Not or OpCode.IsNull
                or OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater
                or OpCode.GreaterEqual or OpCode.IsType or OpCode.Convert or OpCode.RegexMatch or OpCode.PathJoin
                or OpCode.NewScope or OpCode.MakeRange or OpCode.MakeLazyRange or OpCode.MakeClosure
                or OpCode.SpecializeClosure or OpCode.DeferRange or OpCode.LoadBuiltin or OpCode.LoadStaticScope => true,
            _ => false
        };
        if (!plain) return false;
        // This is a producer proof, not a TypeRef proof: a raw Cell can have
        // the same language type as its payload.
        for (int pc = 0; pc + 1 < code.Count; pc++)
            if (code[pc].Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady or OpCode.IteratorNext or OpCode.Return)
                return false;
        return true;
    }

    private static bool InvalidatesMembers(Instruction instruction) => instruction.Op is OpCode.NativeCall or OpCode.ProcessCall
        or OpCode.StoreField or OpCode.StoreStaticField or OpCode.PublishStatic or OpCode.GetIterator or OpCode.Acquire or OpCode.Barrier or OpCode.Transfer
        or OpCode.LocalTransfer or OpCode.Qualify;

    private static Fact Constant(Value value) => value switch
    {
        FunctionValue { NativeName: null } closure => new(true, new(closure.Function, closure.Captures?.Count ?? 0)),
        BoolValue boolean => new(true, Bool: boolean.Data),
        CellValue => default,
        _ => Fact.Plain
    };

    private static Fact Scope(CodeLayout layout, List<Fact> stack)
    {
        bool hasCallable = false;
        var fields = layout.Fields;
        for (int slot = stack.Count - fields.Count; slot < stack.Count; slot++)
            hasCallable |= stack[slot].Shape is not null;
        if (!hasCallable) return Fact.Plain;
        Dictionary<string, Fact> members = new(fields.Count, StringComparer.Ordinal);
        for (int slot = 0; slot < fields.Count; slot++)
            members.Add(fields[slot].Name, stack[stack.Count - fields.Count + slot]);
        return new(true, new(Members: members));
    }

    private static Fact Specialize(Fact callee, Instruction instruction, bool requireKnownCallee)
    {
        var closure = callee.Shape;
        if (instruction.A < 0) return Fact.Plain;
        if (instruction.Text is not { } mapping)
            return closure?.Function is not null || !requireKnownCallee
                ? new(true, new(instruction.A, closure?.Captures)) : Fact.Plain;
        return closure?.Function is int original && FunctionDispatch.Targets(mapping).TryGetValue(original, out int target)
            ? new(true, new(target, closure.Captures)) : Fact.Plain;
    }

    private static int? Select(Instruction instruction, Shape? closure, bool requireKnownCallee,
        IReadOnlyList<FunctionCode>? layouts)
    {
        if (instruction.B < 0 || requireKnownCallee && closure?.Function is null) return null;
        int? target;
        if (instruction.Text is { } mapping)
            target = closure?.Function is int identity && FunctionDispatch.Targets(mapping).TryGetValue(identity, out int selected)
                ? selected : null;
        else target = instruction.B > 0 ? instruction.B - 1 : closure?.Function;
        if (target is not int known || layouts is null) return target;
        if ((uint)known >= (uint)layouts.Count) return null;
        var layout = layouts[known];
        return instruction.A == layout.ParameterCount && (closure?.Captures is not int count || count == layout.CaptureCount)
            ? target : null;
    }
}
