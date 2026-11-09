namespace Msync.Core;

internal static class GlobalRedundancy
{
    private readonly record struct Atom(int Identity, TypeRef Type, Value? Constant = null, bool Storage = false);
    private readonly record struct Expression(OpCode Op, int Left, int Right = 0);
    private readonly record struct Edge(int Source, int Target);
    private sealed record State(List<Atom> Stack, Atom[] Locals)
    {
        internal State Copy() => new(Stack.ToList(), Locals.ToArray());
    }
    private sealed record Occurrence(int Position, Expression Expression, Atom[] Operands, int ExistingLocal, TypeRef Result);
    private sealed record Recipe(Instruction Instruction);
    private sealed class Block(int start, int end)
    {
        internal readonly int Start = start;
        internal readonly int End = end;
        internal readonly List<int> Predecessors = [];
        internal readonly List<int> Successors = [];
        internal readonly List<Occurrence> Expressions = [];
        internal State? Input;
        internal State? Output;
        internal bool Loop;
    }

    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants) =>
        OptimizeWithPool(module, function, constants, new(constants));

    internal static FunctionCode OptimizeWithPool(Module module, FunctionCode function, List<Value> constants, ConstantInterner interner) =>
        new Analysis(module, function, constants, interner).Optimize();

    private sealed class Analysis(Module module, FunctionCode function, List<Value> constants, ConstantInterner interner)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly Dictionary<(int Kind, int Position, int Slot), int> identities = [];
        private readonly Dictionary<Value, int> literalIdentities = new(ValueRepresentation.Comparer);
        private readonly Dictionary<Expression, int> expressionIdentities = [];
        private readonly HashSet<int> aliases = Enumerable.Range(0, function.CaptureCount).Concat(function.Instructions
            .Where(instruction => instruction.Op is OpCode.LoadCell or OpCode.LoadStorage).Select(instruction => instruction.A)).ToHashSet();
        private Block[] blocks = [];
        private int[] blockAt = [];
        private int nextIdentity = 1;

        internal FunctionCode Optimize()
        {
            if (code.Count == 0) return function;
            BuildBlocks();
            if (blocks.Length == 1) return function;
            Discover();
            foreach (var block in blocks.Where(block => block.Input is not null)) Transfer(block, collect: true);
            var available = Availability();
            var slots = new Dictionary<Expression, int>();
            var replacements = new Dictionary<int, int>();
            var insertions = new Dictionary<Edge, List<(Occurrence Expression, Recipe[] Operands)>>();
            var occurrences = blocks.Where(block => !block.Loop).SelectMany(block => block.Expressions)
                .GroupBy(expression => expression.Expression).ToDictionary(group => group.Key, group => group.Count());
            var localCount = function.LocalCount;
            int Slot(Expression expression)
            {
                if (slots.TryGetValue(expression, out var slot)) return slot;
                slot = localCount++; slots.Add(expression, slot); return slot;
            }
            for (var index = 0; index < blocks.Length; index++)
            {
                var block = blocks[index];
                if (block.Input is null || block.Loop) continue;
                var prefix = new HashSet<Expression>(available.Input[index]);
                foreach (var occurrence in block.Expressions)
                {
                    var local = occurrence.ExistingLocal;
                    if (local >= 0)
                    {
                        replacements.Add(occurrence.Position, local);
                        prefix.Add(occurrence.Expression);
                        continue;
                    }
                    // Stack bytecode pays for saving a value. Cheap operations
                    // use an already available local; allocating a new result
                    // location is worthwhile for costly strings or repeated use.
                    if (!WorthSaving(occurrence, occurrences[occurrence.Expression])) { prefix.Add(occurrence.Expression); continue; }
                    if (prefix.Contains(occurrence.Expression))
                    {
                        replacements.Add(occurrence.Position, Slot(occurrence.Expression));
                        continue;
                    }
                    var predecessors = block.Predecessors.Where(predecessor => blocks[predecessor].Output is not null).ToArray();
                    if (predecessors.Length < 2 || predecessors.Any(predecessor => blocks[predecessor].Loop)
                        || !predecessors.Any(predecessor => available.Output[predecessor].Contains(occurrence.Expression)))
                    { prefix.Add(occurrence.Expression); continue; }
                    var missing = predecessors.Where(predecessor => !available.Output[predecessor].Contains(occurrence.Expression)).ToArray();
                    var recipes = missing.Select(predecessor => Materialize(occurrence.Operands, blocks[predecessor].Output!.Locals, occurrence.Position)).ToArray();
                    if (recipes.Any(recipe => recipe is null)) { prefix.Add(occurrence.Expression); continue; }
                    for (var edge = 0; edge < missing.Length; edge++)
                    {
                        var key = new Edge(missing[edge], index);
                        if (!insertions.TryGetValue(key, out var list)) insertions.Add(key, list = []);
                        list.Add((occurrence, recipes[edge]!));
                    }
                    replacements.Add(occurrence.Position, Slot(occurrence.Expression));
                    prefix.Add(occurrence.Expression);
                }
            }
            if (replacements.Count == 0) return function;
            return Rewrite(slots, replacements, insertions, localCount);
        }

        private static bool WorthSaving(Occurrence occurrence, int count) => count >= 3
            || occurrence.Operands.All(operand => operand.Type == TypeRef.String)
            && occurrence.Expression.Op is OpCode.Add or OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual;

        private int Identity(int kind, int position, int slot = 0)
        {
            var key = (kind, position, slot);
            if (identities.TryGetValue(key, out var identity)) return identity;
            identity = nextIdentity++; identities.Add(key, identity); return identity;
        }

        private Atom Literal(Value value, int position)
        {
            if (value is ScopeValue or CellValue or FunctionValue or RangeValue or ProcessValue)
                return new(Identity(0, position), value.Type);
            if (!literalIdentities.TryGetValue(value, out var identity)) literalIdentities.Add(value, identity = nextIdentity++);
            return new(identity, value.Type, value);
        }

        private void BuildBlocks()
        {
            var starts = new SortedSet<int> { 0, code.Count };
            for (var pc = 0; pc < code.Count; pc++)
            {
                var instruction = code[pc];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) starts.Add(instruction.A);
                if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext or OpCode.Return) starts.Add(pc + 1);
            }
            var positions = starts.ToArray();
            blocks = positions.Zip(positions.Skip(1), (start, end) => new Block(start, end)).ToArray();
            blockAt = new int[code.Count + 1];
            for (var block = 0; block < blocks.Length; block++)
                for (var pc = blocks[block].Start; pc < blocks[block].End; pc++) blockAt[pc] = block;
            blockAt[^1] = blocks.Length;
            for (var block = 0; block < blocks.Length; block++)
            {
                var instruction = code[blocks[block].End - 1];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse) Connect(block, instruction.A);
                if (instruction.Op == OpCode.IteratorNext) Connect(block, instruction.B);
                if (instruction.Op is not (OpCode.Jump or OpCode.Return)) Connect(block, blocks[block].End);
            }
            MarkLoops();
        }

        private void Connect(int source, int position)
        {
            if (position == code.Count) return;
            var target = blockAt[position];
            if (blocks[source].Successors.Contains(target)) return;
            blocks[source].Successors.Add(target); blocks[target].Predecessors.Add(source);
        }

        private void MarkLoops()
        {
            var indices = Enumerable.Repeat(-1, blocks.Length).ToArray();
            var lows = new int[blocks.Length];
            var active = new bool[blocks.Length];
            var stack = new Stack<int>(); var next = 0;
            void Visit(int index)
            {
                indices[index] = lows[index] = next++; active[index] = true; stack.Push(index);
                foreach (var successor in blocks[index].Successors)
                {
                    if (indices[successor] < 0) { Visit(successor); lows[index] = Math.Min(lows[index], lows[successor]); }
                    else if (active[successor]) lows[index] = Math.Min(lows[index], indices[successor]);
                }
                if (lows[index] != indices[index]) return;
                var component = new List<int>(); int member;
                do { member = stack.Pop(); active[member] = false; component.Add(member); } while (member != index);
                if (component.Count > 1 || blocks[index].Successors.Contains(index)) foreach (var block in component) blocks[block].Loop = true;
            }
            for (var index = 0; index < blocks.Length; index++) if (indices[index] < 0) Visit(index);
        }

        private void Discover()
        {
            var locals = Enumerable.Range(0, function.LocalCount).Select(slot => new Atom(Identity(1, slot),
                slot >= function.CaptureCount && slot < function.CaptureCount + function.ParameterCount && function.ParameterTypes is { } types
                    ? types[slot - function.CaptureCount] : TypeRef.Any)).ToArray();
            blocks[0].Input = new([], locals);
            Queue<int> work = new([0]); var queued = new bool[blocks.Length]; queued[0] = true;
            while (work.TryDequeue(out var index))
            {
                queued[index] = false;
                foreach (var (successor, state) in Transfer(blocks[index], collect: false))
                {
                    var target = blocks[successor];
                    if (!Merge(target, state)) continue;
                    if (!queued[successor]) { work.Enqueue(successor); queued[successor] = true; }
                }
            }
        }

        private bool Merge(Block block, State incoming)
        {
            if (block.Input is null) { block.Input = incoming.Copy(); return true; }
            var previous = block.Input; var changed = false;
            if (previous.Stack.Count != incoming.Stack.Count)
                throw new MsyncException($"Global value analysis stack heights disagree in '{function.Name}' at instruction {block.Start}");
            Atom Join(Atom left, Atom right, int slot, bool stack)
            {
                var identity = left.Identity == right.Identity ? left.Identity : Identity(stack ? 2 : 3, block.Start, slot);
                var type = TypeRef.Union(left.Type, right.Type);
                return new(identity, type, ValueRepresentation.Same(left.Constant, right.Constant) ? left.Constant : null, left.Storage || right.Storage);
            }
            for (var index = 0; index < previous.Stack.Count; index++)
            {
                var joined = Join(previous.Stack[index], incoming.Stack[index], index, stack: true);
                changed |= !Same(joined, previous.Stack[index]); previous.Stack[index] = joined;
            }
            for (var index = 0; index < previous.Locals.Length; index++)
            {
                var joined = Join(previous.Locals[index], incoming.Locals[index], index, stack: false);
                changed |= !Same(joined, previous.Locals[index]); previous.Locals[index] = joined;
            }
            return changed;
        }

        private List<(int Target, State State)> Transfer(Block block, bool collect)
        {
            if (collect) block.Expressions.Clear();
            var state = block.Input!.Copy();
            var outgoing = new List<(int, State)>();
            var localValues = collect ? state.Locals.Select((value, slot) => (value, slot)).Where(pair => !pair.value.Storage)
                .GroupBy(pair => pair.value.Identity).ToDictionary(group => group.Key, group => group.Select(pair => pair.slot).ToHashSet()) : null;
            void Local(int slot, Atom value)
            {
                if (localValues is not null)
                {
                    var previous = state.Locals[slot];
                    if (!previous.Storage && localValues.TryGetValue(previous.Identity, out var old))
                    { old.Remove(slot); if (old.Count == 0) localValues.Remove(previous.Identity); }
                    if (!value.Storage)
                    {
                        if (!localValues.TryGetValue(value.Identity, out var next)) localValues.Add(value.Identity, next = []);
                        next.Add(slot);
                    }
                }
                state.Locals[slot] = value;
            }
            Atom Pop() { var value = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return value; }
            Atom[] PopMany(int count) { var values = new Atom[count]; for (var index = count - 1; index >= 0; index--) values[index] = Pop(); return values; }
            void Unknown(int position, TypeRef? type = null, bool storage = false) => state.Stack.Add(new(Identity(0, position), type ?? TypeRef.Any, Storage: storage));
            void Invalidate(int position)
            {
                foreach (var slot in aliases) Local(slot, new(Identity(4, position, slot), TypeRef.Any));
            }
            void Next(int position, State output)
            {
                if (position < code.Count) outgoing.Add((blockAt[position], output.Copy()));
            }
            for (var pc = block.Start; pc < block.End; pc++)
            {
                var instruction = code[pc];
                if (TryExpression(instruction, state.Stack, out var arity, out var type))
                {
                    var operands = PopMany(arity);
                    var key = new Expression(instruction.Op, operands[0].Identity, arity == 2 ? operands[1].Identity : 0);
                    if (!expressionIdentities.TryGetValue(key, out var identity)) expressionIdentities.Add(key, identity = nextIdentity++);
                    if (collect && !block.Loop) block.Expressions.Add(new(pc, key, operands,
                        localValues!.TryGetValue(identity, out var locations) ? locations.First() : -1, type));
                    state.Stack.Add(new(identity, type, Evaluate(instruction.Op, operands)));
                    continue;
                }
                switch (instruction.Op)
                {
                    case OpCode.Nop: case OpCode.Barrier: break;
                    case OpCode.Constant: state.Stack.Add(Literal(constants[instruction.A], pc)); break;
                    case OpCode.Null: state.Stack.Add(Literal(Value.Null, pc)); break;
                    case OpCode.True: state.Stack.Add(Literal(Value.True, pc)); break;
                    case OpCode.False: state.Stack.Add(Literal(Value.False, pc)); break;
                    case OpCode.LoadLocal: state.Stack.Add(state.Locals[instruction.A] with { Storage = false }); break;
                    case OpCode.LoadCell: case OpCode.LoadStorage: Unknown(pc, state.Locals[instruction.A].Type, storage: true); break;
                    case OpCode.StoreLocal:
                    case OpCode.InitializeLocal:
                        var value = Pop();
                        if (value.Storage) aliases.Add(instruction.A);
                        if (instruction.Op == OpCode.StoreLocal && aliases.Contains(instruction.A)) Invalidate(pc);
                        Local(instruction.A, value.Storage ? new(Identity(5, pc, instruction.A), value.Type) : value);
                        break;
                    case OpCode.Dup: var duplicate = Pop(); state.Stack.Add(duplicate); state.Stack.Add(duplicate); break;
                    case OpCode.Pop: Pop(); break;
                    case OpCode.Jump: Next(instruction.A, state); break;
                    case OpCode.JumpIfTrue:
                    case OpCode.JumpIfFalse:
                        var condition = Pop();
                        if (condition.Constant is not BoolValue boolean || boolean.Data == (instruction.Op == OpCode.JumpIfTrue)) Next(instruction.A, state);
                        if (condition.Constant is not BoolValue known || known.Data != (instruction.Op == OpCode.JumpIfTrue)) Next(pc + 1, state);
                        break;
                    case OpCode.Return: if (state.Stack.Count > 0) Pop(); break;
                    case OpCode.MakeClosure: PopMany(instruction.B); Unknown(pc, TypeRef.Function); break;
                    case OpCode.Call:
                    case OpCode.ProcessCall:
                        PopMany(instruction.A + 1); Invalidate(pc);
                        Unknown(pc, instruction.Op == OpCode.ProcessCall ? TypeRef.Process : instruction.B > 0 ? module.Functions[instruction.B - 1].ReturnType : TypeRef.Any); break;
                    case OpCode.NativeCall:
                        PopMany(instruction.A);
                        if ((EffectAnalysis.NativeEffects(instruction.Text) & (InstructionEffect.Write | InstructionEffect.Invoke)) != 0) Invalidate(pc);
                        Unknown(pc, instruction.Text is { } native && NativeSignatures.TryGet(native, out var signature) ? signature.Result : TypeRef.Any); break;
                    case OpCode.GetSingleton: Unknown(pc, new(TypeKind.Scope | TypeKind.Null)); break;
                    case OpCode.SetSingleton: Pop(); Unknown(pc, TypeRef.Scope); break;
                    case OpCode.LoadBuiltin: Unknown(pc, instruction.Text?.TrimStart('$') is "std" or "view" or "views" or "ranges" ? TypeRef.Scope : TypeRef.Connection); break;
                    case OpCode.MakeScope: PopMany(instruction.A); Unknown(pc, TypeRef.Scope); break;
                    case OpCode.MakeRange: case OpCode.MakeLazyRange: PopMany(instruction.A); Unknown(pc, new(TypeKind.Range)); break;
                    case OpCode.SpecializeClosure: Pop(); Unknown(pc, TypeRef.Function); break;
                    case OpCode.GetCapture: Pop(); Unknown(pc, storage: true); break;
                    case OpCode.GetMember: Pop(); Unknown(pc); break;
                    case OpCode.Not: case OpCode.IsNull: Pop(); Unknown(pc, TypeRef.Bool); break;
                    case OpCode.Negate: Pop(); Unknown(pc, TypeRef.Number); break;
                    case OpCode.Convert: Pop(); Unknown(pc, instruction.Text == "Path" ? TypeRef.Path : TypeRef.String); break;
                    case OpCode.SelectPath: case OpCode.DeferRange: Pop(); Unknown(pc, new(TypeKind.Range)); break;
                    case OpCode.GetIterator: Pop(); Invalidate(pc); Unknown(pc); break;
                    case OpCode.IteratorNext:
                        Invalidate(pc); Next(instruction.B, state); Unknown(pc); Next(pc + 1, state); break;
                    case OpCode.Acquire: if (instruction.A != 0) { var process = Pop(); state.Stack.Add(process); } Invalidate(pc); break;
                    case OpCode.RegexMatch: PopMany(instruction.Text is null ? 2 : 1); Unknown(pc, TypeRef.Bool); break;
                    case OpCode.SetMember: PopMany(2); Invalidate(pc); state.Stack.Add(Literal(Value.Null, pc)); break;
                    default:
                        PopMany(2);
                        Unknown(pc, instruction.Op is OpCode.Transfer or OpCode.LocalTransfer or OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.IsType ? TypeRef.Bool
                            : instruction.Op is OpCode.PathJoin or OpCode.Qualify ? TypeRef.Path : TypeRef.Any);
                        break;
                }
            }
            block.Output = state;
            var last = code[block.End - 1];
            if (last.Op is not (OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext or OpCode.Return)) Next(block.End, state);
            return outgoing;
        }

        private (HashSet<Expression>[] Input, HashSet<Expression>[] Output) Availability()
        {
            var input = blocks.Select(_ => new HashSet<Expression>()).ToArray();
            var output = blocks.Select(_ => new HashSet<Expression>()).ToArray();
            var work = new Queue<int>(Enumerable.Range(0, blocks.Length)); var queued = Enumerable.Repeat(true, blocks.Length).ToArray();
            while (work.TryDequeue(out var index))
            {
                queued[index] = false; var block = blocks[index];
                if (block.Input is null || block.Loop) continue;
                var predecessors = block.Predecessors.Where(predecessor => blocks[predecessor].Output is not null).ToArray();
                var incoming = predecessors.Length == 0 ? [] : new HashSet<Expression>(output[predecessors[0]]);
                foreach (var predecessor in predecessors.Skip(1)) incoming.IntersectWith(output[predecessor]);
                var outgoing = new HashSet<Expression>(incoming); outgoing.UnionWith(block.Expressions.Select(expression => expression.Expression));
                input[index] = incoming;
                if (outgoing.SetEquals(output[index])) continue;
                output[index] = outgoing;
                foreach (var successor in block.Successors) if (!queued[successor]) { queued[successor] = true; work.Enqueue(successor); }
            }
            return (input, output);
        }

        private Recipe[]? Materialize(Atom[] operands, Atom[] locals, int position)
        {
            var result = new Recipe[operands.Length];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                var value = operands[operand];
                if (value.Storage) return null;
                var slot = Array.FindIndex(locals, local => !local.Storage && local.Identity == value.Identity);
                if (slot >= 0) { result[operand] = new(new(OpCode.LoadLocal, slot, Span: code[position].Span)); continue; }
                if (value.Constant is null) return null;
                var instruction = value.Constant switch
                {
                    NullValue => new Instruction(OpCode.Null, Span: code[position].Span),
                    BoolValue boolean => new Instruction(boolean.Data ? OpCode.True : OpCode.False, Span: code[position].Span),
                    _ => new Instruction(OpCode.Constant, Constant(value.Constant), Span: code[position].Span)
                };
                result[operand] = new(instruction);
            }
            return result;
        }

        private int Constant(Value value)
        {
            return interner.Intern(value);
        }

        private FunctionCode Rewrite(Dictionary<Expression, int> slots, Dictionary<int, int> replacements,
            Dictionary<Edge, List<(Occurrence Expression, Recipe[] Operands)>> insertions, int localCount)
        {
            var output = new List<Instruction>();
            var positions = new int[code.Count + 1];
            var fixups = new List<(int Position, bool B, int Target, Edge? Split)>();
            var taken = new Dictionary<Edge, int>();
            var expressions = blocks.SelectMany(block => block.Expressions).ToDictionary(occurrence => occurrence.Position);
            void Insert(Edge edge)
            {
                foreach (var (expression, operands) in insertions[edge])
                {
                    output.AddRange(operands.Select(recipe => recipe.Instruction));
                    output.Add(code[expression.Position]);
                    output.Add(new(OpCode.InitializeLocal, slots[expression.Expression], Span: code[expression.Position].Span));
                }
            }
            for (var pc = 0; pc < code.Count; pc++)
            {
                positions[pc] = output.Count;
                var instruction = code[pc]; var block = blockAt[pc];
                if (instruction.Op == OpCode.Jump)
                {
                    var edge = new Edge(block, instruction.A < code.Count ? blockAt[instruction.A] : blocks.Length);
                    if (insertions.ContainsKey(edge)) Insert(edge);
                }
                if (replacements.TryGetValue(pc, out var slot))
                {
                    for (var operand = 0; operand < expressions[pc].Operands.Length; operand++) output.Add(new(OpCode.Pop, Span: instruction.Span));
                    output.Add(new(OpCode.LoadLocal, slot, Span: instruction.Span));
                }
                else
                {
                    if (instruction.Op is OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.IteratorNext)
                    {
                        var target = instruction.Op == OpCode.IteratorNext ? instruction.B : instruction.A;
                        var edge = new Edge(block, target < code.Count ? blockAt[target] : blocks.Length);
                        fixups.Add((output.Count, instruction.Op == OpCode.IteratorNext, target,
                            instruction.Op != OpCode.Jump && insertions.ContainsKey(edge) ? edge : null));
                    }
                    output.Add(instruction);
                    if (expressions.TryGetValue(pc, out var expression) && slots.TryGetValue(expression.Expression, out var saved))
                    {
                        output.Add(new(OpCode.Dup, Span: instruction.Span));
                        output.Add(new(OpCode.InitializeLocal, saved, Span: instruction.Span));
                    }
                }
                if (pc == blocks[block].End - 1 && instruction.Op is not (OpCode.Jump or OpCode.Return) && pc + 1 < code.Count)
                {
                    var edge = new Edge(block, blockAt[pc + 1]);
                    if (insertions.ContainsKey(edge)) Insert(edge);
                }
            }
            positions[^1] = output.Count;
            var splitEdges = fixups.Where(fixup => fixup.Split is not null).Select(fixup => fixup.Split!.Value).Distinct().ToArray();
            var skip = -1;
            if (splitEdges.Length > 0 && code[^1].Op is not (OpCode.Return or OpCode.Jump))
            { skip = output.Count; output.Add(new(OpCode.Jump)); }
            foreach (var edge in splitEdges)
            {
                taken.Add(edge, output.Count); Insert(edge);
                fixups.Add((output.Count, false, blocks[edge.Target].Start, null));
                output.Add(new(OpCode.Jump, Span: code[blocks[edge.Source].End - 1].Span));
            }
            if (skip >= 0) output[skip] = new(OpCode.Jump, output.Count);
            foreach (var fixup in fixups)
            {
                var target = fixup.Split is { } edge ? taken[edge] : fixup.Target == code.Count ? output.Count : positions[fixup.Target];
                var instruction = output[fixup.Position];
                output[fixup.Position] = fixup.B ? instruction with { B = target } : instruction with { A = target };
            }
            return function with { Instructions = output, LocalCount = localCount };
        }

        private static bool TryExpression(Instruction instruction, List<Atom> stack, out int arity, out TypeRef type)
        {
            arity = instruction.Op is OpCode.Not or OpCode.IsNull or OpCode.Negate ? 1 : 2; type = TypeRef.Bool;
            if (stack.Count < arity) return false;
            var left = stack[^arity]; var right = stack[^1];
            if (left.Storage || right.Storage) return false;
            bool Numeric() => left.Type == TypeRef.Number && right.Type == TypeRef.Number;
            bool Comparable() => Numeric() || left.Type == TypeRef.Bool && right.Type == TypeRef.Bool
                || left.Type == TypeRef.Type && right.Type == TypeRef.Type
                || (left.Type.Kind & ~(TypeKind.String | TypeKind.Path)) == 0 && (right.Type.Kind & ~(TypeKind.String | TypeKind.Path)) == 0;
            switch (instruction.Op)
            {
                case OpCode.IsNull: return true;
                case OpCode.Not: return left.Type == TypeRef.Bool;
                case OpCode.Negate: type = TypeRef.Number; return left.Type == TypeRef.Number;
                case OpCode.Add:
                    type = left.Type; return Numeric() || left.Type == TypeRef.String && right.Type == TypeRef.String;
                case OpCode.Subtract: case OpCode.Multiply: case OpCode.Divide: case OpCode.Remainder: type = TypeRef.Number; return Numeric();
                case OpCode.Equal: case OpCode.NotEqual: case OpCode.Less: case OpCode.LessEqual: case OpCode.Greater: case OpCode.GreaterEqual: return Comparable();
                case OpCode.IsType: return right.Type == TypeRef.Type;
                default: return false;
            }
        }

        private static Value? Evaluate(OpCode operation, Atom[] values) => operation switch
        {
            OpCode.IsNull when values[0].Constant is { } value => value is NullValue ? Value.True : Value.False,
            OpCode.Not when values[0].Constant is BoolValue boolean => boolean.Data ? Value.False : Value.True,
            _ => null
        };

        private static bool Same(Atom left, Atom right) => left.Identity == right.Identity && left.Type == right.Type
            && left.Storage == right.Storage && ValueRepresentation.Same(left.Constant, right.Constant);
    }
}
