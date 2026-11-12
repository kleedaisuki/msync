namespace Msync.Optimization;

internal static class PredicateOptimization
{
    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants) =>
        OptimizeWithPool(module, function, constants, new(constants));

    internal static FunctionCode OptimizeWithPool(Module module, FunctionCode function, List<Value> constants, ConstantInterner interner) =>
        new Analysis(module, function, constants, interner).Rewrite();

    private enum Test : byte { Boolean, Null, Type, Equal }
    private sealed record Predicate(Test Test, int Subject, Value? Value = null, TypeRef? Type = null, bool Negated = false);
    private readonly record struct Fact(TypeRef Type, int Identity, Value? Constant = null, Predicate? Predicate = null)
    {
        internal static readonly Fact Bottom = new(new(TypeKind.Never), 0);
    }
    private sealed record State(List<Fact> Stack, Fact[] Locals, int[] Generations)
    {
        internal int Generation;
        internal int InvalidatedAt;
        internal State Copy() => new(Stack.ToList(), Locals.ToArray(), Generations.ToArray())
        { Generation = Generation, InvalidatedAt = InvalidatedAt };
    }

    private sealed class Analysis(Module module, FunctionCode function, List<Value> constants, ConstantInterner interner)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly State?[] states = new State?[function.Instructions.Count + 1];
        private readonly Fact?[] results = new Fact?[function.Instructions.Count];
        private readonly Fact?[] conditions = new Fact?[function.Instructions.Count];
        private readonly bool[] starts = new bool[function.Instructions.Count + 1];
        private readonly bool[] queued = new bool[function.Instructions.Count + 1];
        private readonly Queue<int> work = new();
        private readonly (int[] Indices, int Count) aliases = AliasIndices(module, function);
        private readonly Dictionary<(int Block, int Slot, bool Stack, bool Invalidation), int> identities = [];
        private int nextIdentity = -function.LocalCount - 1;

        internal FunctionCode Rewrite()
        {
            Discover();
            var output = new List<Instruction>();
            var positions = new int[code.Count + 1];
            var branches = new List<int>();
            for (var pc = 0; pc < code.Count; pc++)
            {
                positions[pc] = output.Count;
                var instruction = code[pc];
                if (conditions[pc]?.Constant is BoolValue condition)
                {
                    output.Add(new(OpCode.Pop, Span: instruction.Span));
                    if (condition.Data == (instruction.Op == OpCode.JumpIfTrue))
                    { branches.Add(output.Count); output.Add(new(OpCode.Jump, instruction.A, Span: instruction.Span)); }
                    continue;
                }
                if (results[pc]?.Constant is { } value && Replaceable(instruction, out var consumed))
                {
                    for (var index = 0; index < consumed; index++) output.Add(new(OpCode.Pop, Span: instruction.Span));
                    output.Add(Literal(value, instruction.Span));
                    continue;
                }
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady or OpCode.IteratorNext) branches.Add(output.Count);
                output.Add(instruction);
            }
            positions[^1] = output.Count;
            foreach (var pc in branches)
            {
                var instruction = output[pc];
                output[pc] = instruction.Op == OpCode.IteratorNext ? instruction with { B = positions[instruction.B] }
                    : instruction with { A = positions[instruction.A] };
            }
            return function with { Instructions = output };
        }

        private void Discover()
        {
            starts[0] = starts[^1] = true;
            for (var pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady)
                    starts[instruction.A] = starts[pc + 1] = true;
                else if (instruction.Op == OpCode.IteratorNext) starts[instruction.B] = starts[pc + 1] = true;
                else if (instruction.Op == OpCode.Return) starts[pc + 1] = true;
            }
            var locals = Enumerable.Repeat(Fact.Bottom, function.LocalCount).ToArray();
            for (var slot = 0; slot < function.CaptureCount; slot++) locals[slot] = new(TypeRef.Any, -slot - 1);
            for (var parameter = 0; parameter < function.ParameterCount; parameter++)
                locals[function.CaptureCount + parameter] = new(function.ParameterTypes?[parameter] ?? TypeRef.Any, -function.CaptureCount - parameter - 1);
            states[0] = new([], locals, new int[aliases.Count]); Enqueue(0);
            while (work.TryDequeue(out var start))
            {
                queued[start] = false;
                if (start == code.Count) continue;
                var state = states[start]!.Copy();
                for (var pc = start; pc < code.Count; pc++)
                {
                    var instruction = code[pc];
                    var identity = pc + 1;
                    Fact Pop() { var value = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return value; }
                    void Discard(int count) => state.Stack.RemoveRange(state.Stack.Count - count, count);
                    void Push(Fact value) { state.Stack.Add(value); results[pc] = value; }
                    Fact Unknown(TypeRef type) => new(type, identity);
                    void Invalidate()
                    {
                        // Generations defer work; canonical PC identities, not
                        // these counters, participate in the CFG fixed point.
                        state.Generation++;
                        state.InvalidatedAt = pc;
                    }
                    switch (instruction.Op)
                    {
                        case OpCode.Nop: break;
                        case OpCode.Constant:
                            var value = constants[instruction.A];
                            Push(new(value.Type, identity, Immutable(value) ? value : null)); break;
                        case OpCode.Null: Push(new(TypeRef.Null, identity, Value.Null)); break;
                        case OpCode.True: Push(new(TypeRef.Bool, identity, Value.True)); break;
                        case OpCode.False: Push(new(TypeRef.Bool, identity, Value.False)); break;
                        case OpCode.LoadLocal: Push(Local(state, instruction.A)); break;
                        case OpCode.LoadCell: case OpCode.LoadStorage: Push(Unknown(Local(state, instruction.A).Type)); break;
                        case OpCode.StoreLocal:
                        case OpCode.InitializeLocal:
                            state.Locals[instruction.A] = Pop();
                            int alias = aliases.Count == 0 ? -1 : aliases.Indices[instruction.A];
                            if (alias >= 0)
                            {
                                state.Generations[alias] = state.Generation;
                                if (instruction.Op == OpCode.StoreLocal) Invalidate();
                            }
                            break;
                        case OpCode.Pop: Pop(); break;
                        case OpCode.Dup: var duplicate = Pop(); state.Stack.Add(duplicate); Push(duplicate); break;
                        case OpCode.Jump: Merge(instruction.A, state); goto EndBlock;
                        case OpCode.JumpIfStaticReady: Merge(instruction.A, state); Merge(pc + 1, state); goto EndBlock;
                        case OpCode.JumpIfFalse:
                        case OpCode.JumpIfTrue:
                            var condition = Pop(); conditions[pc] = condition;
                            if (condition.Constant is BoolValue known)
                            {
                                Merge(known.Data == (instruction.Op == OpCode.JumpIfTrue) ? instruction.A : pc + 1, state);
                                goto EndBlock;
                            }
                            var taken = state.Copy(); Assume(taken, condition, instruction.Op == OpCode.JumpIfTrue);
                            Merge(instruction.A, taken);
                            Assume(state, condition, instruction.Op == OpCode.JumpIfFalse);
                            Merge(pc + 1, state); goto EndBlock;
                        case OpCode.Return: goto EndBlock;
                        case OpCode.MakeClosure: Discard(instruction.B); Push(Unknown(TypeRef.Function)); break;
                        case OpCode.SpecializeClosure: Pop(); Push(Unknown(TypeRef.Function)); break;
                        case OpCode.GetCapture: Pop(); Push(Unknown(TypeRef.Any)); break;
                        case OpCode.Call: Discard(instruction.A + 1); Invalidate(); Push(Unknown(instruction.B > 0 ? module.Functions[instruction.B - 1].ReturnType : TypeRef.Any)); break;
                        case OpCode.ProcessCall: Discard(instruction.A + 1); Push(Unknown(TypeRef.Process)); break;
                        case OpCode.NativeCall:
                            var arguments = ConstantArguments(state.Stack, instruction.A);
                            Discard(instruction.A);
                            if ((InstructionEffects.NativeEffects(instruction.Text) & InstructionEffect.Invoke) != 0) Invalidate();
                            var result = arguments is null ? null : PureEvaluation.Native(instruction.Text, arguments, module);
                            Push(new(NativeSignatures.TryGet(instruction.Text!, out var native) ? native.Result : TypeRef.Any, identity, result)); break;
                        case OpCode.LoadStaticScope: Push(Unknown(TypeRef.Scope)); break;
                        case OpCode.LoadStaticField: Push(Unknown(ScopeOperands.FieldType(module, instruction))); break;
                        case OpCode.PublishStatic: Discard(ScopeOperands.Construction(module, instruction).Fields.Count); Invalidate(); break;
                        case OpCode.LoadBuiltin:
                            Push(Unknown(instruction.Text?.TrimStart('$') is "std" ? TypeRef.Scope : TypeRef.Connection)); break;
                        case OpCode.MakeRange:
                            var element = new TypeRef(TypeKind.Never);
                            for (int index = state.Stack.Count - instruction.A; index < state.Stack.Count; index++)
                                element = TypeRef.Union(element, state.Stack[index].Type);
                            Discard(instruction.A);
                            Push(Unknown(new(TypeKind.Range, element))); break;
                        case OpCode.MakeLazyRange: Discard(instruction.A); Push(Unknown(new(TypeKind.Range))); break;
                        case OpCode.DeferRange: Pop(); Push(Unknown(new(TypeKind.Range))); break;
                        case OpCode.NewScope: Discard(ScopeOperands.Construction(module, instruction).Fields.Count); Push(Unknown(TypeRef.Scope)); break;
                        case OpCode.LoadField: Pop(); Push(Unknown(ScopeOperands.FieldType(module, instruction))); break;
                        case OpCode.AdmitScope: Push(Pop() with { Type = TypeRef.Scope }); break;
                        case OpCode.GetIterator: Pop(); Push(Unknown(new(TypeKind.Range))); break;
                        case OpCode.IteratorNext:
                            Invalidate(); Merge(instruction.B, state, borrowed: true); Push(Unknown(TypeRef.Any)); Merge(pc + 1, state); goto EndBlock;
                        case OpCode.Transfer:
                        case OpCode.LocalTransfer:
                            Pop();
                            if ((Pop().Type.Kind & TypeKind.Range) != 0) Invalidate();
                            Push(Unknown(TypeRef.Bool)); break;
                        case OpCode.StoreStaticField:
                        case OpCode.StoreField: Discard(instruction.Op == OpCode.StoreField ? 2 : 1); Invalidate(); Push(new(TypeRef.Null, identity, Value.Null)); break;
                        case OpCode.Barrier: break;
                        case OpCode.Acquire:
                            var acquired = instruction.A != 0 ? Pop() : (Fact?)null;
                            if (acquired is { } target) Push(target); break;
                        case OpCode.Not:
                            var boolean = Pop(); Push(boolean.Constant is BoolValue b ? new(TypeRef.Bool, identity, Bool(!b.Data))
                                : new(TypeRef.Bool, identity, Predicate: Negate(boolean.Predicate ?? new(Test.Boolean, boolean.Identity)))); break;
                        case OpCode.IsNull:
                            var nullable = Pop();
                            Push(new(TypeRef.Bool, identity, nullable.Type == TypeRef.Null ? Value.True : !nullable.Type.Nullable ? Value.False : null,
                                new(Test.Null, nullable.Identity))); break;
                        case OpCode.IsType:
                            var expected = Pop(); var actual = Pop(); var expectedType = (expected.Constant as TypeValue)?.Data;
                            var actualType = actual.Constant is TypeValue type ? type.Data : actual.Type;
                            var knownType = expectedType is null ? null : actual.Constant is TypeValue
                                ? Bool(expectedType.Accepts(actualType)) : KnownTypeTest(actualType, expectedType);
                            Push(new(TypeRef.Bool, identity, knownType, expectedType is not null && (actual.Type.Kind & TypeKind.Type) == 0
                                ? new(Test.Type, actual.Identity, Type: expectedType) : null)); break;
                        case OpCode.Negate:
                            var number = Pop(); Push(new(TypeRef.Number, identity, number.Constant is NumberValue n ? new NumberValue(-n.Data) : null)); break;
                        case OpCode.Convert:
                            var converted = Pop();
                            Push(new(instruction.Text == "Path" ? TypeRef.Path : TypeRef.String, identity,
                                instruction.Text == "Path" && converted.Constant is PathValue || instruction.Text == "String" && converted.Constant is StringValue ? converted.Constant : null)); break;
                        case OpCode.SelectPath: Pop(); Push(Unknown(new(TypeKind.Range, TypeRef.Path))); break;
                        case OpCode.PathJoin: case OpCode.Qualify: Discard(2); Push(Unknown(TypeRef.Path)); break;
                        case OpCode.RegexMatch: Discard(instruction.Text is null ? 2 : 1); Push(Unknown(TypeRef.Bool)); break;
                        default:
                            var right = Pop(); var left = Pop();
                            Push(Binary(instruction.Op, left, right, identity)); break;
                    }
                    if (!starts[pc + 1]) continue;
                    Merge(pc + 1, state); break;
                }
            EndBlock:;
            }
        }

        private Fact Local(State state, int slot)
        {
            Fact value = state.Locals[slot];
            if (aliases.Count == 0) return value;
            int alias = aliases.Indices[slot];
            if (alias < 0 || state.Generations[alias] == state.Generation) return value;
            state.Generations[alias] = state.Generation;
            if (value.Type.Kind == TypeKind.Never) return value;
            return state.Locals[slot] = new(TypeRef.Any, Identity(state.InvalidatedAt, slot, invalidation: true));
        }

        private int Identity(int block, int slot, bool stack = false, bool invalidation = false)
        {
            var key = (block, slot, stack, invalidation);
            if (identities.TryGetValue(key, out var identity)) return identity;
            identity = nextIdentity--; identities.Add(key, identity); return identity;
        }

        private void Assume(State state, Fact condition, bool truth)
        {
            var originalTruth = truth;
            var predicate = condition.Predicate ?? new(Test.Boolean, condition.Identity);
            truth ^= predicate.Negated;
            Fact Refine(Fact value)
            {
                if (value.Identity == condition.Identity) value = value with { Type = TypeRef.Bool, Constant = Bool(originalTruth) };
                if (value.Identity != predicate.Subject) return value;
                return predicate.Test switch
                {
                    Test.Boolean => value with { Type = TypeRef.Bool, Constant = Bool(truth) },
                    Test.Null when truth => value with { Type = TypeRef.Null, Constant = Value.Null },
                    Test.Null => value with { Type = value.Type.WithoutNull, Constant = value.Constant is NullValue ? null : value.Constant },
                    Test.Type when predicate.Type is { } type => value with { Type = RefineType(value.Type, type, truth) },
                    Test.Equal when truth && predicate.Value is { } expected => RefineEquality(value, expected),
                    Test.Equal when predicate.Value is BoolValue boolean && value.Type == TypeRef.Bool => value with { Constant = Bool(!boolean.Data) },
                    _ => value
                };
            }
            for (var slot = 0; slot < state.Locals.Length; slot++) state.Locals[slot] = Refine(Local(state, slot));
            for (var slot = 0; slot < state.Stack.Count; slot++) state.Stack[slot] = Refine(state.Stack[slot]);
        }

        private void Merge(int target, State incoming, bool borrowed = false)
        {
            if (states[target] is not { } previous)
            {
                // Block replay always copies its input. Only an iterator's
                // exhausted edge still shares scratch with the yielding edge.
                states[target] = borrowed ? incoming.Copy() : incoming;
                Enqueue(target);
                return;
            }
            var changed = false;
            Fact Join(Fact left, Fact right, int slot, bool stack = false)
            {
                if (left.Type.Kind == TypeKind.Never) return right;
                if (right.Type.Kind == TypeKind.Never) return left;
                var identity = left.Identity == right.Identity ? left.Identity : Identity(target, slot, stack);
                return new(TypeRef.Union(left.Type, right.Type), identity,
                    left.Constant is not null && ValueRepresentation.Same(left.Constant, right.Constant) ? left.Constant : null,
                    left.Predicate == right.Predicate ? left.Predicate : null);
            }
            for (var slot = 0; slot < previous.Stack.Count; slot++)
            {
                var joined = Join(previous.Stack[slot], incoming.Stack[slot], slot, stack: true);
                changed |= !Same(joined, previous.Stack[slot]); previous.Stack[slot] = joined;
            }
            for (var slot = 0; slot < previous.Locals.Length; slot++)
            {
                var joined = Join(Local(previous, slot), Local(incoming, slot), slot);
                changed |= !Same(joined, previous.Locals[slot]); previous.Locals[slot] = joined;
            }
            if (changed) Enqueue(target);
        }

        private void Enqueue(int target) { if (!queued[target]) { work.Enqueue(target); queued[target] = true; } }
        private static Value[]? ConstantArguments(List<Fact> stack, int count)
        {
            int first = stack.Count - count;
            for (int index = first; index < stack.Count; index++)
                if (stack[index].Constant is null) return null;
            if (count == 0) return [];
            var arguments = new Value[count];
            for (int index = 0; index < count; index++) arguments[index] = stack[first + index].Constant!;
            return arguments;
        }

        private Instruction Literal(Value value, SourceSpan? span)
        {
            if (value is NullValue) return new(OpCode.Null, Span: span);
            if (value is BoolValue boolean) return new(boolean.Data ? OpCode.True : OpCode.False, Span: span);
            return new(OpCode.Constant, interner.Intern(value), Span: span);
        }

        private static bool Immutable(Value value) => value is NullValue or BoolValue or NumberValue or StringValue or PathValue or TypeValue or ConnectionValue;
        private static Value Bool(bool value) => value ? Value.True : Value.False;
        private static bool Same(Fact left, Fact right) => left.Type == right.Type && left.Identity == right.Identity
            && ValueRepresentation.Same(left.Constant, right.Constant) && left.Predicate == right.Predicate;
        private static Fact RefineEquality(Fact actual, Value expected) => expected switch
        {
            // Numerical comparison identifies the value, not its zero sign or
            // NaN payload. Text comparison also accepts a Path on either side.
            NumberValue number when number.Data == 0 || double.IsNaN(number.Data) => actual with { Type = TypeRef.Number },
            NumberValue or BoolValue or TypeValue => actual with { Type = expected.Type, Constant = expected },
            StringValue when actual.Type == TypeRef.String => actual with { Constant = expected },
            _ => actual
        };
        private static TypeRef RefineType(TypeRef actual, TypeRef expected, bool truth)
        {
            if (truth) return new(actual.Kind & expected.Kind, expected.Element ?? actual.Element);
            var excluded = expected.Element is null ? expected.Kind : expected.Kind & ~TypeKind.Range;
            return new(actual.Kind & ~excluded, actual.Element);
        }
        private static Value? KnownTypeTest(TypeRef actual, TypeRef expected)
        {
            if ((actual.Kind & TypeKind.Type) != 0) return null;
            if (actual.Kind == TypeKind.Never) return null;
            if ((actual.Kind & expected.Kind) == 0) return Value.False;
            if (!expected.Accepts(actual)) return null;
            if (expected.Element is null || (actual.Kind & TypeKind.Range) == 0) return Value.True;
            return actual.Element is { } element && KnownTypeTest(element, expected.Element) is BoolValue { Data: true } ? Value.True : null;
        }
        private static Predicate Negate(Predicate predicate) => predicate with { Negated = !predicate.Negated };
        private static Fact Binary(OpCode op, Fact left, Fact right, int identity)
        {
            Value? constant = (op, left.Constant, right.Constant) switch
            {
                (OpCode.Add, NumberValue a, NumberValue b) => new NumberValue(a.Data + b.Data),
                (OpCode.Subtract, NumberValue a, NumberValue b) => new NumberValue(a.Data - b.Data),
                (OpCode.Multiply, NumberValue a, NumberValue b) => new NumberValue(a.Data * b.Data),
                (OpCode.Divide, NumberValue a, NumberValue b) => new NumberValue(a.Data / b.Data),
                (OpCode.Remainder, NumberValue a, NumberValue b) => new NumberValue(a.Data % b.Data),
                (OpCode.Add, StringValue a, StringValue b) => new StringValue(a.Data + b.Data),
                _ => null
            };
            var comparison = Compare(left.Constant, right.Constant);
            if (comparison is { } order)
                constant = op switch
                {
                    OpCode.Equal => Bool(order == 0),
                    OpCode.NotEqual => Bool(order != 0),
                    OpCode.Less => Bool(order < 0),
                    OpCode.LessEqual => Bool(order <= 0),
                    OpCode.Greater => Bool(order > 0),
                    OpCode.GreaterEqual => Bool(order >= 0),
                    _ => constant
                };
            var isComparison = op is OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual;
            Predicate? predicate = null;
            if (op is OpCode.Equal or OpCode.NotEqual)
            {
                if (right.Constant is { } expected) predicate = new(Test.Equal, left.Identity, expected, Negated: op == OpCode.NotEqual);
                else if (left.Constant is { } expectedLeft) predicate = new(Test.Equal, right.Identity, expectedLeft, Negated: op == OpCode.NotEqual);
            }
            return new(isComparison ? TypeRef.Bool : op == OpCode.Add ? TypeRef.Union(left.Type, right.Type) : TypeRef.Number, identity, constant, predicate);
        }

        private static int? Compare(Value? left, Value? right) => (left, right) switch
        {
            (NumberValue a, NumberValue b) => NumberSemantics.Compare(a.Data, b.Data),
            (BoolValue a, BoolValue b) => a.Data.CompareTo(b.Data),
            (StringValue a, StringValue b) => TextSemantics.CompareOrdinal(a.Data, b.Data),
            (TypeValue a, TypeValue b) => a.Data == b.Data ? 0 : 1,
            _ => null
        };

        private static bool Replaceable(Instruction instruction, out int consumed)
        {
            consumed = instruction.Op switch
            {
                OpCode.LoadLocal => 0,
                OpCode.Not or OpCode.IsNull or OpCode.Negate or OpCode.Convert => 1,
                OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual
                    or OpCode.IsType or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder => 2,
                OpCode.NativeCall => instruction.A,
                _ => -1
            };
            return consumed >= 0;
        }
    }

    private static (int[] Indices, int Count) AliasIndices(Module module, FunctionCode function)
    {
        int[] slots = CaptureAliases(module, function);
        if (slots.Length == 0) return ([], 0);
        int[] indices = new int[function.LocalCount];
        Array.Fill(indices, -1);
        for (int index = 0; index < slots.Length; index++) indices[slots[index]] = index;
        return (indices, slots.Length);
    }

    // Raw locations flow separately from payload facts: LoadLocal dereferences,
    // whereas LoadCell/LoadStorage/GetCapture can establish another write alias.
    internal static int[] CaptureAliases(Module module, FunctionCode function)
    {
        var code = function.Instructions;
        var aliases = Enumerable.Range(0, function.CaptureCount + function.ParameterCount).ToHashSet();
        if (code.Count == 0) return aliases.Order().ToArray();
        var graph = new ControlFlowGraph(function);
        var states = new bool[]?[graph.Blocks.Count];
        var queued = new bool[graph.Blocks.Count];
        var work = new Queue<int>();
        states[0] = [];
        queued[0] = true;
        work.Enqueue(0);
        while (work.TryDequeue(out int block))
        {
            queued[block] = false;
            Visit(block, states[block]!.ToList());
        }
        return aliases.Order().ToArray();

        void Merge(int pc, List<bool> stack)
        {
            int target = graph.BlockAt(pc);
            if (target < 0) return;
            var changed = false;
            if (states[target] is not { } previous) { states[target] = stack.ToArray(); changed = true; }
            else for (var index = 0; index < previous.Length; index++)
                if (stack[index] && !previous[index]) { previous[index] = true; changed = true; }
            if (changed && !queued[target]) { work.Enqueue(target); queued[target] = true; }
        }

        void Visit(int block, List<bool> stack)
        {
            bool Pop() { var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
            void Consume(int count) => stack.RemoveRange(stack.Count - count, count);
            var range = graph.Blocks[block];
            // One mutable scratch stack per block, with snapshots only at
            // edges. Literal operands must not create a quadratic copy chain.
            for (int pc = range.Start; pc < range.End; pc++)
            {
                var instruction = code[pc];
                switch (instruction.Op)
                {
                    case OpCode.Nop: case OpCode.Barrier: break;
                    case OpCode.LoadCell: case OpCode.LoadStorage: aliases.Add(instruction.A); stack.Add(true); break;
                    case OpCode.GetCapture: Pop(); stack.Add(true); break;
                    case OpCode.PublishStatic: Consume(ScopeOperands.Construction(module, instruction).Fields.Count); break;
                    case OpCode.StoreStaticField: Pop(); stack.Add(false); break;
                    case OpCode.LoadStaticScope: case OpCode.LoadStaticField: case OpCode.LoadLocal: case OpCode.LoadBuiltin: case OpCode.Constant: case OpCode.Null: case OpCode.True: case OpCode.False: stack.Add(false); break;
                    case OpCode.StoreLocal: case OpCode.InitializeLocal: if (Pop()) aliases.Add(instruction.A); break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Dup: stack.Add(stack[^1]); break;
                    case OpCode.Jump: Merge(instruction.A, stack); return;
                    case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(); Merge(instruction.A, stack); break;
                    case OpCode.JumpIfStaticReady: Merge(instruction.A, stack); break;
                    case OpCode.Return: return;
                    case OpCode.IteratorNext: Merge(instruction.B, stack); stack.Add(true); break;
                    case OpCode.MakeClosure: Consume(instruction.B); stack.Add(false); break;
                    case OpCode.NewScope: Consume(ScopeOperands.Construction(module, instruction).Fields.Count); stack.Add(false); break;
                    case OpCode.MakeRange: case OpCode.MakeLazyRange: Consume(instruction.A); stack.Add(false); break;
                    case OpCode.Call: Consume(instruction.A + 1); stack.Add(true); break;
                    case OpCode.ProcessCall: Consume(instruction.A + 1); stack.Add(false); break;
                    case OpCode.NativeCall: Consume(instruction.A); stack.Add(instruction.Text?.StartsWith("ranges.", StringComparison.Ordinal) == true); break;
                    case OpCode.Acquire: break;
                    case OpCode.AdmitScope: break;
                    case OpCode.LoadField:
                    case OpCode.SpecializeClosure:
                    case OpCode.Not:
                    case OpCode.IsNull:
                    case OpCode.Negate:
                    case OpCode.Convert:
                    case OpCode.SelectPath:
                    case OpCode.GetIterator:
                    case OpCode.DeferRange: Pop(); stack.Add(false); break;
                    case OpCode.RegexMatch: Consume(instruction.Text is null ? 2 : 1); stack.Add(false); break;
                    default: Consume(2); stack.Add(false); break;
                }
            }
            Merge(range.End, stack);
        }
    }
}
