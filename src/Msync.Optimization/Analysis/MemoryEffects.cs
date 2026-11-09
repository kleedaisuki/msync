namespace Msync.Optimization;

internal static class MemoryEffects
{
    internal static IReadOnlyList<FunctionMemoryEffects> Analyze(Module module)
    {
        var summaries = Enumerable.Repeat(FunctionMemoryEffects.Empty, module.Functions.Count).ToArray();
        var targets = module.Functions.Select(function => PotentialTargets(module, function)).ToArray();
        var components = Components(targets);
        var callers = module.Functions.Select(_ => new HashSet<int>()).ToArray();
        var work = new Queue<int>(Enumerable.Range(0, summaries.Length));
        var queued = Enumerable.Repeat(true, summaries.Length).ToArray();
        while (work.TryDequeue(out int index))
        {
            queued[index] = false;
            var next = Trivial(module, module.Functions[index])
                ?? new Analysis(module, index, summaries, components, callee => callers[callee].Add(index)).Run();
            if (Same(summaries[index], next)) continue;
            summaries[index] = next;
            foreach (int caller in callers[index])
                if (!queued[caller]) { queued[caller] = true; work.Enqueue(caller); }
        }
        return summaries;
    }

    private static Dictionary<int, int> PotentialTargets(Module module, FunctionCode function)
    {
        var targets = FunctionInlining.Targets(function, module.Constants, true, module.Functions).Values.ToHashSet();
        void ValueTargets(Value value)
        {
            if (value is not FunctionValue { NativeName: null } closure) return;
            targets.Add(closure.Function);
            foreach (var capture in closure.Captures ?? []) ValueTargets(capture);
        }
        foreach (var instruction in function.Instructions)
        {
            if (instruction.Op == OpCode.MakeClosure) targets.Add(instruction.A);
            if (instruction.Op == OpCode.Constant) ValueTargets(module.Constants[instruction.A]);
            if (instruction.Op != OpCode.Call) continue;
            if (instruction.B > 0) targets.Add(instruction.B - 1);
            if (instruction.Text is not null) foreach (int target in FunctionDispatch.Parse(instruction.Text).Values) targets.Add(target);
        }
        return targets.Select((target, index) => (target, index)).ToDictionary(pair => pair.index, pair => pair.target);
    }

    private static FunctionMemoryEffects? Trivial(Module module, FunctionCode function)
    {
        if (function.Instructions is { Count: 2 } code && code[1].Op == OpCode.Return)
        {
            if (code[0].Op is OpCode.Null or OpCode.True or OpCode.False) return FunctionMemoryEffects.Empty;
            if (code[0].Op == OpCode.Constant)
            {
                var value = module.Constants[code[0].A];
                return value is ScopeValue or FunctionValue or RangeValue or CellValue
                    ? FunctionMemoryEffects.Empty with { UnknownReturnAlias = true, HasDeferredInvocation = value is RangeValue }
                    : FunctionMemoryEffects.Empty;
            }
        }
        if (function.CaptureCount != 0 || !Scalar(function.ReturnType)
            || function.ParameterTypes?.Any(type => !Scalar(type)) == true || function.ParameterCount > 0 && function.ParameterTypes is null) return null;
        return function.Instructions.All(instruction => instruction.Op == OpCode.Constant
            ? module.Constants[instruction.A] is not CellValue && Scalar(module.Constants[instruction.A].Type)
            : instruction.Op is OpCode.Null or OpCode.True or OpCode.False
            or OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Pop or OpCode.Dup or OpCode.Nop
            or OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.Return or OpCode.Not or OpCode.IsNull or OpCode.IsType
            or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Remainder or OpCode.Negate or OpCode.Equal or OpCode.NotEqual
            or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual or OpCode.PathJoin or OpCode.Convert
            or OpCode.Transfer or OpCode.LocalTransfer or OpCode.Acquire or OpCode.Barrier)
            ? FunctionMemoryEffects.Empty : null;
    }

    private static bool Scalar(TypeRef type) => (type.Kind & (TypeKind.Scope | TypeKind.Function | TypeKind.Range)) == 0;

    private static bool Same(FunctionMemoryEffects left, FunctionMemoryEffects right) => left.UnknownReads == right.UnknownReads
        && left.UnknownWrites == right.UnknownWrites && left.UnknownReturnAlias == right.UnknownReturnAlias
        && left.HasDeferredInvocation == right.HasDeferredInvocation && SetSame(left.Reads, right.Reads)
        && SetSame(left.Writes, right.Writes) && SetSame(left.Escapes, right.Escapes) && SetSame(left.ReturnAliases, right.ReturnAliases)
        && SetSame(left.Enumerates, right.Enumerates) && left.ReturnsCallbackFreeRange == right.ReturnsCallbackFreeRange;
    private static bool SetSame<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        if (left.Count == 0) return true;
        if (left.Count == 1) return EqualityComparer<T>.Default.Equals(left[0], right[0]);
        var values = new HashSet<T>(right);
        return left.All(values.Contains);
    }

    private static int[] Components(Dictionary<int, int>[] targets)
    {
        var edges = targets.Select(target => target.Values.Distinct().ToArray()).ToArray();
        var reverse = targets.Select(_ => new List<int>()).ToArray();
        for (int vertex = 0; vertex < edges.Length; vertex++) foreach (int target in edges[vertex]) reverse[target].Add(vertex);
        var seen = new bool[targets.Length];
        var order = new List<int>(targets.Length);
        var stack = new Stack<(int Vertex, int Next)>();
        for (int root = 0; root < edges.Length; root++)
        {
            if (seen[root]) continue;
            seen[root] = true; stack.Push((root, 0));
            while (stack.TryPop(out var frame))
            {
                if (frame.Next == edges[frame.Vertex].Length) { order.Add(frame.Vertex); continue; }
                stack.Push((frame.Vertex, frame.Next + 1));
                int next = edges[frame.Vertex][frame.Next];
                if (seen[next]) continue;
                seen[next] = true; stack.Push((next, 0));
            }
        }
        var result = new int[targets.Length]; Array.Fill(result, -1);
        var pending = new Stack<int>(); int component = 0;
        for (int position = order.Count - 1; position >= 0; position--)
        {
            int root = order[position];
            if (result[root] != -1) continue;
            result[root] = component; pending.Push(root);
            while (pending.TryPop(out int vertex))
                foreach (int predecessor in reverse[vertex])
                    if (result[predecessor] == -1) { result[predecessor] = component; pending.Push(predecessor); }
            component++;
        }
        return result;
    }

    private readonly record struct Cell(int Slot, bool External);
    private sealed record Closure(int Function, ValueInfo[] Captures, string? Native = null);
    private sealed record ValueInfo(MemoryAccess[] Origins, Cell[] Cells, Closure[] Closures, bool Unknown = false,
        ValueInfo[]? Elements = null, bool Deferred = false, bool UnknownCell = false, bool CallbackFreeRange = false)
    {
        internal static readonly ValueInfo Empty = new([], [], []);
        internal static readonly ValueInfo Top = new([], [], [], true);
        internal static ValueInfo At(MemoryAccess access) => new([access], [], []);
        internal static ValueInfo Raw(Cell cell) => new([], [cell], []);
        internal static ValueInfo Callable(Closure closure) => new([], [], [closure]);
    }
    private sealed record State(ValueInfo[] Locals, List<ValueInfo> Stack,
        Dictionary<Cell, ValueInfo> Cells, Dictionary<MemoryAccess, ValueInfo> Fields, HashSet<MemoryAccess> Clobbers, HashSet<Cell> EscapedCells)
    {
        internal bool AllFieldsUnknown { get; set; }
        internal State Copy() => new(Locals.ToArray(), Stack.ToList(), new(Cells), new(Fields), new(Clobbers), new(EscapedCells))
        { AllFieldsUnknown = AllFieldsUnknown };
    }

    private sealed class Analysis(Module module, int functionIndex, FunctionMemoryEffects[] summaries, int[] components, Action<int> dependency)
    {
        private readonly FunctionCode function = module.Functions[functionIndex];
        private readonly HashSet<MemoryAccess> reads = [], writes = [], escapes = [], returns = [], enumerates = [];
        private bool unknownReads, unknownWrites, unknownReturn, deferred;
        private bool sawRangeReturn, callbackFreeReturns = true;
        private readonly Queue<int> work = new();
        private readonly Dictionary<int, State> entries = [];
        private readonly HashSet<int> queued = [];
        private readonly HashSet<int> starts = [0];
        private readonly Dictionary<int, string[]> allocationFields = [];
        private int instructionPosition;

        internal FunctionMemoryEffects Run()
        {
            var code = function.Instructions;
            if (code.Count == 0) return FunctionMemoryEffects.Empty;
            foreach (var instruction in code)
            {
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue) starts.Add(instruction.A);
                if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
            }
            var locals = Enumerable.Repeat(ValueInfo.Empty, function.LocalCount).ToArray();
            Dictionary<Cell, ValueInfo> cells = [];
            for (int capture = 0; capture < function.CaptureCount; capture++)
            {
                Cell cell = new(capture, true);
                locals[capture] = ValueInfo.Raw(cell); cells.Add(cell, ValueInfo.At(new(new(MemoryRootKind.Capture, capture))));
            }
            for (int parameter = 0; parameter < function.ParameterCount; parameter++)
                locals[function.CaptureCount + parameter] = function.ParameterTypes?[parameter] is { } type && Scalar(type)
                    ? ValueInfo.Empty : ValueInfo.At(new(new(MemoryRootKind.Parameter, parameter)));
            entries.Add(0, new(locals, [], cells, [], [], [])); Enqueue(0);
            while (work.TryDequeue(out int start))
            {
                queued.Remove(start);
                if (start == code.Count) continue;
                var state = entries[start].Copy();
                for (int pc = start; pc < code.Count; pc++)
                {
                    var instruction = code[pc];
                    if (instruction.Op == OpCode.Return)
                    {
                        ValueInfo value = state.Stack.Count == 0 ? ValueInfo.Empty : Dereference(state, Pop(state));
                        if ((function.ReturnType.Kind & (TypeKind.Scope | TypeKind.Function | TypeKind.Range)) != 0)
                        {
                            foreach (var origin in value.Origins)
                                if (External(origin)) returns.Add(origin); else unknownReturn = true;
                            unknownReturn |= value.Unknown || value.Closures.Length > 0;
                        }
                        deferred |= value.Deferred;
                        if (function.ReturnType.Kind == TypeKind.Range) { sawRangeReturn = true; callbackFreeReturns &= value.CallbackFreeRange; }
                        EscapeContents(state, value, []);
                        break;
                    }
                    Transfer(state, instruction, pc);
                    if (instruction.Op == OpCode.Jump) { Merge(instruction.A, state, instruction.A <= pc); break; }
                    if (instruction.Op is OpCode.JumpIfFalse or OpCode.JumpIfTrue) Merge(instruction.A, state, instruction.A <= pc);
                    if (instruction.Op == OpCode.IteratorNext)
                    {
                        var exit = state.Copy(); Pop(exit); Merge(instruction.B, exit, false);
                    }
                    if (pc + 1 < code.Count && starts.Contains(pc + 1)) { Merge(pc + 1, state, false); break; }
                }
            }
            return new(reads.ToArray(), writes.ToArray(), escapes.ToArray(), returns.ToArray(), unknownReads, unknownWrites, unknownReturn, deferred)
            { Enumerates = enumerates.ToArray(), ReturnsCallbackFreeRange = sawRangeReturn && callbackFreeReturns };
        }

        private void Enqueue(int pc) { if (queued.Add(pc)) work.Enqueue(pc); }
        private static bool External(MemoryAccess access) => access.Root.Kind != MemoryRootKind.Allocation;
        private static ValueInfo Pop(State state)
        {
            if (state.Stack.Count == 0) return ValueInfo.Empty;
            var value = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return value;
        }
        private static ValueInfo[] Arguments(State state, int count)
        {
            var values = new ValueInfo[count];
            for (int index = count - 1; index >= 0; index--) values[index] = Pop(state);
            return values;
        }
        private static ValueInfo Dereference(State state, ValueInfo value)
        {
            if (value.UnknownCell) return ValueInfo.Top;
            if (value.Cells.Length == 0) return value;
            var result = value with { Cells = [] };
            foreach (var cell in value.Cells) result = Union(result, state.Cells.GetValueOrDefault(cell) ?? ValueInfo.Top);
            return result;
        }
        private void ReadCells(ValueInfo value)
        {
            foreach (var cell in value.Cells)
                if (cell.External) reads.Add(new(new(MemoryRootKind.Capture, cell.Slot)));
        }
        private void StoreCells(State state, ValueInfo location, ValueInfo value)
        {
            foreach (var cell in location.Cells)
            {
                if (cell.External) writes.Add(new(new(MemoryRootKind.Capture, cell.Slot)));
                if (cell.External || state.EscapedCells.Contains(cell)) Escape(state, value, []);
                state.Cells[cell] = value;
            }
        }

        private void Transfer(State state, Instruction instruction, int pc)
        {
            instructionPosition = pc;
            void Push(ValueInfo value) => state.Stack.Add(value);
            switch (instruction.Op)
            {
                case OpCode.Nop: case OpCode.Barrier: break;
                case OpCode.GetSingleton: unknownReads = true; Push(ValueInfo.Top); break;
                case OpCode.SetSingleton: Escape(state, Pop(state), []); unknownWrites = true; Push(ValueInfo.Top); break;
                case OpCode.Constant:
                    var literal = module.Constants[instruction.A];
                    Push(literal is FunctionValue callable ? ConstantFunction(callable) : literal is ScopeValue or RangeValue ? ValueInfo.Top : ValueInfo.Empty); break;
                case OpCode.Null: case OpCode.True: case OpCode.False: case OpCode.LoadBuiltin: Push(ValueInfo.Empty); break;
                case OpCode.LoadLocal:
                    var local = state.Locals[instruction.A]; ReadCells(local); unknownReads |= local.UnknownCell; Push(Dereference(state, local)); break;
                case OpCode.LoadStorage: Push(state.Locals[instruction.A]); break;
                case OpCode.LoadCell:
                    var raw = state.Locals[instruction.A];
                    if (raw.Cells.Length == 0)
                    {
                        Cell cell = new(instruction.A, false); state.Cells[cell] = raw;
                        state.Locals[instruction.A] = raw = ValueInfo.Raw(cell);
                    }
                    Push(raw); break;
                case OpCode.StoreLocal:
                    var assigned = Dereference(state, Pop(state));
                    if (state.Locals[instruction.A].UnknownCell) { unknownWrites = true; Invalidate(state); }
                    if (state.Locals[instruction.A].Cells.Length > 0) StoreCells(state, state.Locals[instruction.A], assigned);
                    else state.Locals[instruction.A] = assigned;
                    break;
                case OpCode.InitializeLocal: state.Locals[instruction.A] = Pop(state); break;
                case OpCode.Pop: Pop(state); break;
                case OpCode.Dup: var duplicated = Pop(state); Push(duplicated); Push(duplicated); break;
                case OpCode.MakeScope:
                    var items = Arguments(state, instruction.A);
                    if (!allocationFields.TryGetValue(pc, out var names))
                        allocationFields.Add(pc, names = instruction.Text?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? []);
                    MemoryAccess allocation = new(new(MemoryRootKind.Allocation, pc));
                    for (int index = 0; index < Math.Min(items.Length, names.Length); index++) state.Fields[allocation.Field(names[index])] = items[index];
                    Push(ValueInfo.At(allocation)); break;
                case OpCode.GetMember:
                    Push(ReadField(state, Dereference(state, Pop(state)), instruction.Text!)); break;
                case OpCode.SetMember:
                    var value = Dereference(state, Pop(state)); var receiver = Dereference(state, Pop(state));
                    WriteField(state, receiver, instruction.Text!, value); Push(ValueInfo.Empty); break;
                case OpCode.MakeClosure: Push(ValueInfo.Callable(new(instruction.A, Arguments(state, instruction.B)))); break;
                case OpCode.GetCapture:
                    var closure = Dereference(state, Pop(state)); var capture = ValueInfo.Empty;
                    foreach (var candidate in closure.Closures)
                        capture = Union(capture, instruction.A < candidate.Captures.Length ? candidate.Captures[instruction.A] : ValueInfo.Top);
                    if (closure.Unknown || closure.Origins.Length > 0) capture = Union(capture, ValueInfo.Top with { UnknownCell = true });
                    Push(capture); break;
                case OpCode.SpecializeClosure:
                    var specialized = Dereference(state, Pop(state));
                    Push(specialized with
                    {
                        Closures = specialized.Closures.Select(candidate => candidate.Native is not null ? candidate
                        : candidate with { Function = instruction.Text is null ? instruction.A : FunctionDispatch.Parse(instruction.Text).GetValueOrDefault(candidate.Function, candidate.Function) }).ToArray()
                    }); break;
                case OpCode.Call:
                    var arguments = Arguments(state, instruction.A).Select(argument => Dereference(state, argument)).ToArray();
                    Push(Call(state, Dereference(state, Pop(state)), arguments, instruction)); break;
                case OpCode.NativeCall:
                    Push(Native(state, instruction.Text!, Arguments(state, instruction.A).Select(argument => Dereference(state, argument)).ToArray())); break;
                case OpCode.ProcessCall:
                    var processArguments = Arguments(state, instruction.A).Select(argument => Dereference(state, argument)).ToArray();
                    var executable = Dereference(state, Pop(state));
                    Push(executable.Closures.Length > 0 || executable.Origins.Length > 0 || executable.Unknown
                        ? Call(state, executable, processArguments, new(OpCode.Call, instruction.A)) : ValueInfo.Empty); break;
                case OpCode.Transfer:
                case OpCode.LocalTransfer:
                    var transfer = Arguments(state, 2);
                    if (MayEnumerate(transfer[0])) Enumerate(state, transfer[0]);
                    Push(ValueInfo.Empty); break;
                case OpCode.MakeRange:
                    Push(new([], [], [], Elements: Arguments(state, instruction.A), CallbackFreeRange: true)); break;
                case OpCode.MakeLazyRange:
                    var lazy = Arguments(state, instruction.A); foreach (var item in lazy) Escape(state, item, []);
                    Push(new([], [], [], Deferred: true)); deferred = true; break;
                case OpCode.DeferRange:
                    Escape(state, Pop(state), []); Push(new([], [], [], Deferred: true)); deferred = true; break;
                case OpCode.GetIterator:
                    var range = Dereference(state, Pop(state));
                    if (range.Elements is null && !range.CallbackFreeRange) Enumerate(state, range);
                    Push(range); break;
                case OpCode.IteratorNext:
                    var iterator = Dereference(state, state.Locals[instruction.A]);
                    if (iterator.Elements is null) { if (!iterator.CallbackFreeRange) Enumerate(state, iterator); Push(ValueInfo.Top); }
                    else Push(iterator.Elements.Aggregate(ValueInfo.Empty, Union));
                    break;
                case OpCode.Acquire:
                    if (instruction.A != 0) Push(Pop(state)); break;
                case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(state); break;
                case OpCode.Jump: break;
                case OpCode.Qualify:
                    var qualifier = Arguments(state, 2)[0];
                    unknownReads |= qualifier.Unknown || qualifier.Origins.Length > 0;
                    Push(qualifier.Unknown || qualifier.Origins.Length > 0 ? ValueInfo.Top : ValueInfo.Empty); break;
                case OpCode.SelectPath: Pop(state); Push(new([], [], [], CallbackFreeRange: true)); break;
                case OpCode.Not:
                case OpCode.IsNull:
                case OpCode.Negate:
                case OpCode.Convert:
                    Pop(state); Push(ValueInfo.Empty); break;
                case OpCode.RegexMatch: Arguments(state, instruction.Text is null ? 2 : 1); Push(ValueInfo.Empty); break;
                default: Arguments(state, 2); Push(ValueInfo.Empty); break;
            }
        }

        private static ValueInfo ConstantFunction(FunctionValue function) => ValueInfo.Callable(new(function.Function,
            function.Captures?.Select(value => value is FunctionValue nested ? ConstantFunction(nested) : value is CellValue ? ValueInfo.Top with { UnknownCell = true } : value is ScopeValue or RangeValue ? ValueInfo.Top : ValueInfo.Empty).ToArray() ?? [], function.NativeName));

        private ValueInfo ReadField(State state, ValueInfo receiver, string name)
        {
            var result = receiver.Unknown ? ValueInfo.Top : ValueInfo.Empty;
            unknownReads |= receiver.Unknown;
            foreach (var origin in receiver.Origins)
            {
                var field = origin.Field(name);
                if (External(field)) reads.Add(field);
                var value = FieldValue(state, field); ReadCells(value);
                result = Union(result, Dereference(state, value));
            }
            return result;
        }
        private static ValueInfo FieldValue(State state, MemoryAccess field) => state.Fields.TryGetValue(field, out var value) ? value
            : state.AllFieldsUnknown || state.Clobbers.Any(write => write.PrefixOf(field)) ? ValueInfo.Top : ValueInfo.At(field);
        private void WriteField(State state, ValueInfo receiver, string name, ValueInfo value)
        {
            unknownWrites |= receiver.Unknown;
            foreach (var origin in receiver.Origins)
            {
                var field = origin.Field(name);
                if (External(field)) { writes.Add(field); Escape(state, value, []); }
                if (state.Fields.TryGetValue(field, out var previous) && previous.UnknownCell) { unknownWrites = true; Invalidate(state); }
                if (previous is not null && previous.Cells.Length > 0) StoreCells(state, previous, value);
                else state.Fields[field] = value;
            }
        }

        private ValueInfo Call(State state, ValueInfo callee, ValueInfo[] arguments, Instruction instruction)
        {
            var result = ValueInfo.Empty;
            if (callee.Unknown || callee.Origins.Length > 0 || callee.Closures.Length == 0)
            { Unknown(state, [callee, .. arguments]); result = ValueInfo.Top; }
            foreach (var closure in callee.Closures)
            {
                if (closure.Native is { } native) { result = Union(result, Native(state, native, arguments)); continue; }
                int target = instruction.B > 0 ? instruction.B - 1 : instruction.Text is null ? closure.Function
                    : FunctionDispatch.Parse(instruction.Text).GetValueOrDefault(closure.Function, closure.Function);
                dependency(target);
                var summary = summaries[target];
                var returned = summary.ReturnAliases.Select(alias => ReturnAlias(state, alias, closure, arguments, target)).ToArray();
                var readPlan = summary.Reads.Select(access => (Access: access, Values: Map(state, access, closure, arguments, target,
                    access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0).ToArray())).ToArray();
                var writePlan = summary.Writes.Select(access => (Access: access, Values: Map(state, access, closure, arguments, target,
                    access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0).ToArray())).ToArray();
                var retained = summary.Escapes.Select(escape => ResolveAccess(state, escape, closure, arguments, true)).ToArray();
                foreach (var read in readPlan) Apply(state, read.Access, read.Values, reads, false);
                foreach (var write in writePlan) Apply(state, write.Access, write.Values, writes, true);
                foreach (var enumeration in summary.Enumerates)
                {
                    var value = ResolveAccess(state, enumeration, closure, arguments);
                    if (MayEnumerate(value)) Enumerate(state, value);
                }
                foreach (var retainedValue in retained) Escape(state, retainedValue, []);
                if (summary.UnknownReads || summary.UnknownWrites)
                {
                    unknownReads |= summary.UnknownReads; unknownWrites |= summary.UnknownWrites;
                    if (summary.UnknownWrites) Invalidate(state);
                }
                foreach (var returnedValue in returned) result = Union(result, returnedValue);
                if (summary.UnknownReturnAlias) result = Union(result, ValueInfo.Top);
                deferred |= summary.HasDeferredInvocation;
                if (summary.ReturnsCallbackFreeRange) result = result with { CallbackFreeRange = true };
                if (summary.HasDeferredInvocation && module.Functions[target].ReturnType.Kind == TypeKind.Range && !summary.ReturnsCallbackFreeRange)
                    result = result with { Deferred = true };
            }
            return result;
        }

        private void Apply(State state, MemoryAccess access, IEnumerable<ValueInfo> values, HashSet<MemoryAccess> output, bool write)
        {
            foreach (var mapped in values)
            {
                if (mapped.Unknown) { if (write) unknownWrites = true; else unknownReads = true; }
                if (access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0)
                {
                    foreach (var cell in mapped.Cells)
                    {
                        if (cell.External) output.Add(new(new(MemoryRootKind.Capture, cell.Slot)));
                        if (write) state.Cells[cell] = ValueInfo.Top;
                    }
                    continue;
                }
                foreach (var origin in mapped.Origins)
                {
                    if (External(origin)) output.Add(origin);
                    if (write)
                    {
                        Clobber(state, origin, []);
                    }
                }
            }
        }

        private IEnumerable<ValueInfo> Map(State state, MemoryAccess access, Closure closure, ValueInfo[] arguments, int target, bool rawCapture = false)
        {
            var actual = access.Root.Kind == MemoryRootKind.Parameter
                ? access.Root.Index < arguments.Length ? arguments[access.Root.Index] : ValueInfo.Top
                : access.Root.Index < closure.Captures.Length ? closure.Captures[access.Root.Index] : ValueInfo.Top;
            if (rawCapture) { yield return actual; yield break; }
            actual = Dereference(state, actual);
            if (components[target] == components[functionIndex] && actual.Origins.Any(origin => origin.Fields.Count > 0))
            {
                if (actual.Unknown) yield return ValueInfo.Top;
                foreach (var origin in actual.Origins) yield return ValueInfo.At(origin.Subtree());
                yield break;
            }
            for (int index = 0; index + 1 < access.Fields.Count; index++) actual = ReadContents(state, actual, access.Fields[index]);
            if (actual.Unknown) yield return ValueInfo.Top;
            foreach (var origin in actual.Origins)
            {
                var mapped = access.Fields.Count == 0 ? origin : origin.Field(access.Fields[^1]);
                if (access.Descendants) mapped = mapped.Subtree();
                yield return ValueInfo.At(mapped);
            }
        }

        private void Clobber(State state, MemoryAccess location, HashSet<MemoryAccess> visited)
        {
            if (!visited.Add(location)) return;
            state.Clobbers.Add(location);
            if (!location.Descendants)
            {
                if (state.Fields.TryGetValue(location, out var value) && value.Cells.Length > 0)
                    StoreCells(state, value, ValueInfo.Top);
                else state.Fields.Remove(location);
                return;
            }
            foreach (var field in state.Fields.Keys.Where(location.PrefixOf).ToArray())
            {
                var value = state.Fields[field];
                foreach (var child in Dereference(state, value).Origins) Clobber(state, child.Subtree(), visited);
                if (value.Cells.Length > 0) StoreCells(state, value, ValueInfo.Top);
                else state.Fields.Remove(field);
            }
        }

        private ValueInfo Native(State state, string name, ValueInfo[] arguments)
        {
            if (!NativeSignatures.TryGet(name, out var signature)) { Unknown(state, arguments); return ValueInfo.Top; }
            if (signature.ResultRule == NativeResultRule.CastTarget && arguments.Length > 0) return arguments[0];
            if (signature.Behavior is { } behavior)
            {
                if ((behavior & NativeBehavior.LocalMemory) != 0)
                    return IndexedStorage(state, name, signature, arguments);
                if ((behavior & NativeBehavior.Deferred) != 0)
                {
                    foreach (var argument in arguments) Escape(state, argument, []);
                    deferred = true;
                    return new([], [], [], Deferred: true);
                }
                if ((behavior & NativeBehavior.Enumerates) != 0)
                    for (var index = 0; index < arguments.Length && index < signature.Parameters.Count; index++)
                        if ((signature.Parameters[index].Kind & TypeKind.Range) != 0 && MayEnumerate(arguments[index]))
                            Enumerate(state, arguments[index]);
                if ((behavior & NativeBehavior.Invokes) != 0) Unknown(state, arguments);
                if (signature.ResultRule == NativeResultRule.RangeItem && arguments.Length > 0)
                    return RangeContents(state, arguments[0]);
                return signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length > 0 ? arguments[0]
                    : signature.Result.Kind == TypeKind.Range ? new([], [], [], CallbackFreeRange: true) : ValueInfo.Empty;
            }
            if (name.StartsWith("ranges.", StringComparison.Ordinal))
            {
                if ((InstructionEffects.NativeEffects(name) & InstructionEffect.Invoke) != 0)
                {
                    if (arguments.Length > 0 && MayEnumerate(arguments[0])) Enumerate(state, arguments[0]);
                    if (arguments.Length > 1 && signature.Parameters.LastOrDefault()?.Kind == TypeKind.Function
                        && arguments[0].Elements is not { Length: 0 })
                    {
                        var element = arguments[0].Elements?.Aggregate(ValueInfo.Empty, Union) ?? ValueInfo.Top;
                        Call(state, arguments[^1], [element], new(OpCode.Call, 1));
                    }
                    return ValueInfo.Top;
                }
                foreach (var argument in arguments) Escape(state, argument, []);
                if (name is "ranges.of" or "ranges.single") return new([], [], [], Elements: arguments, CallbackFreeRange: true);
                if (arguments.Length > 0 && arguments[0].CallbackFreeRange && !signature.Parameters.Any(type => type.Kind == TypeKind.Function))
                    return arguments[0];
                deferred = true; return new([], [], [], Deferred: true);
            }
            return signature.Result.Kind == TypeKind.Range ? new([], [], [], CallbackFreeRange: true) : ValueInfo.Empty;
        }

        private const string IndexedContents = "<indexed-items>";

        private ValueInfo RangeContents(State state, ValueInfo value) => value.Elements is { } elements
            ? elements.Aggregate(ValueInfo.Empty, Union) : ReadField(state, value, IndexedContents);

        private ValueInfo IndexedStorage(State state, string name, NativeSignature signature, ValueInfo[] arguments)
        {
            if (name == "storage.buffer_new")
            {
                if (MayEnumerate(arguments[0])) Enumerate(state, arguments[0]);
                return OwnedStorage(state, RangeContents(state, arguments[0]));
            }
            ValueInfo contents = ReadField(state, arguments[0], IndexedContents);
            if (name == "storage.buffer_snapshot") return OwnedStorage(state, contents);
            if (signature.Effect == NativeEffect.Write)
            {
                ValueInfo next = name switch
                {
                    "storage.buffer_clear" => ValueInfo.Empty,
                    "storage.buffer_push" => Union(contents, arguments[1]),
                    "storage.buffer_set" or "storage.buffer_insert" => Union(contents, arguments[2]),
                    _ => contents
                };
                WriteField(state, arguments[0], IndexedContents, next);
            }
            return signature.ResultRule == NativeResultRule.RangeItem ? contents : ValueInfo.Empty;
        }

        private ValueInfo OwnedStorage(State state, ValueInfo contents)
        {
            MemoryAccess allocation = new(new(MemoryRootKind.Allocation, instructionPosition));
            allocationFields[instructionPosition] = [IndexedContents];
            state.Fields[allocation.Field(IndexedContents)] = contents;
            return ValueInfo.At(allocation) with { CallbackFreeRange = true };
        }
        private bool MayEnumerate(ValueInfo value) => !value.CallbackFreeRange && (value.Deferred || value.Unknown || value.UnknownCell
            || value.Elements is null && value.Origins.Any(origin => origin.Root.Kind == MemoryRootKind.Capture || origin.Fields.Count > 0
                || origin.Root.Kind == MemoryRootKind.Parameter && (function.ParameterTypes?[origin.Root.Index].Kind is null
                    || (function.ParameterTypes[origin.Root.Index].Kind & TypeKind.Range) != 0)));

        private void Enumerate(State state, ValueInfo value)
        {
            if (value.Elements is not null || value.CallbackFreeRange) return;
            foreach (var origin in value.Origins)
                if (External(origin)) enumerates.Add(origin); else unknownReads = unknownWrites = true;
            if (value.Unknown || value.Deferred || value.Origins.Length == 0) unknownReads = unknownWrites = true;
            Invalidate(state);
        }

        private ValueInfo ReturnAlias(State state, MemoryAccess access, Closure closure, ValueInfo[] arguments, int target)
        {
            var actual = access.Root.Kind == MemoryRootKind.Parameter
                ? access.Root.Index < arguments.Length ? arguments[access.Root.Index] : ValueInfo.Top
                : access.Root.Index < closure.Captures.Length ? closure.Captures[access.Root.Index] : ValueInfo.Top;
            actual = Dereference(state, actual);
            if (components[target] != components[functionIndex] || actual.Origins.All(origin => origin.Fields.Count == 0))
                return ResolveAccess(state, access, closure, arguments);
            var origins = actual.Origins.Select(origin => origin.Subtree()).ToArray();
            return new(origins, [], [], actual.Unknown);
        }

        private static ValueInfo ResolveAccess(State state, MemoryAccess access, Closure closure, ValueInfo[] arguments, bool escaping = false)
        {
            if (access.Descendants && !escaping) return ValueInfo.Top;
            var value = access.Root.Kind == MemoryRootKind.Parameter
                ? access.Root.Index < arguments.Length ? arguments[access.Root.Index] : ValueInfo.Top
                : access.Root.Index < closure.Captures.Length ? closure.Captures[access.Root.Index] : ValueInfo.Top;
            if (escaping && access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0) return value;
            value = Dereference(state, value);
            foreach (string name in access.Fields)
            {
                value = ReadContents(state, value, name);
            }
            return value;
        }

        private static ValueInfo ReadContents(State state, ValueInfo value, string name)
        {
            if (name == IndexedContents && value.Elements is { } elements)
                return elements.Aggregate(ValueInfo.Empty, Union);
            var next = value.Unknown ? ValueInfo.Top : ValueInfo.Empty;
            foreach (var origin in value.Origins) next = Union(next, Dereference(state, FieldValue(state, origin.Field(name))));
            return next;
        }

        private void Unknown(State state, IEnumerable<ValueInfo> values)
        {
            unknownReads = unknownWrites = true;
            foreach (var value in values) Escape(state, value, []);
            Invalidate(state);
        }
        private static void Invalidate(State state)
        {
            foreach (var cell in state.Cells.Keys.Where(cell => cell.External).ToArray()) state.Cells[cell] = ValueInfo.Top;
            state.AllFieldsUnknown = true;
            state.Fields.Clear();
        }
        private void Escape(State state, ValueInfo value, HashSet<MemoryAccess> visited)
        {
            foreach (var element in value.Elements ?? []) Escape(state, element, visited);
            foreach (var cell in value.Cells)
            {
                if (!state.EscapedCells.Add(cell)) continue;
                if (cell.External) escapes.Add(new(new(MemoryRootKind.Capture, cell.Slot), descendants: true));
                if (state.Cells.TryGetValue(cell, out var payload)) Escape(state, payload, visited);
            }
            foreach (var origin in value.Origins)
            {
                if (!visited.Add(origin)) continue;
                if (External(origin)) escapes.Add(origin.Subtree());
                else foreach (var member in Contents(state, origin)) Escape(state, member, visited);
            }
            foreach (var closure in value.Closures) foreach (var capture in closure.Captures) Escape(state, capture, visited);
        }
        private void EscapeContents(State state, ValueInfo value, HashSet<MemoryAccess> visited)
        {
            foreach (var element in value.Elements ?? []) Escape(state, element, visited);
            foreach (var origin in value.Origins.Where(origin => !External(origin)))
                foreach (var member in Contents(state, origin)) Escape(state, member, visited);
            foreach (var closure in value.Closures) foreach (var capture in closure.Captures) Escape(state, capture, visited);
        }

        private IEnumerable<ValueInfo> Contents(State state, MemoryAccess origin)
        {
            if (origin.Root.Kind != MemoryRootKind.Allocation || origin.Fields.Count != 0
                || !allocationFields.TryGetValue(origin.Root.Index, out var names)) yield break;
            foreach (string name in names) yield return FieldValue(state, origin.Field(name));
        }

        private void Merge(int target, State incoming, bool backedge)
        {
            if (!entries.TryGetValue(target, out var previous)) { entries.Add(target, incoming.Copy()); Enqueue(target); return; }
            bool changed = false;
            for (int slot = 0; slot < previous.Locals.Length; slot++)
            {
                var joined = Join(previous.Locals[slot], incoming.Locals[slot], backedge);
                changed |= !ValueSame(joined, previous.Locals[slot]); previous.Locals[slot] = joined;
            }
            for (int slot = 0; slot < previous.Stack.Count; slot++)
            {
                var joined = Join(previous.Stack[slot], incoming.Stack[slot], backedge);
                changed |= !ValueSame(joined, previous.Stack[slot]); previous.Stack[slot] = joined;
            }
            foreach (var cell in previous.Cells.Keys.Concat(incoming.Cells.Keys).Distinct().ToArray())
            {
                var left = previous.Cells.GetValueOrDefault(cell) ?? ValueInfo.Empty;
                var right = incoming.Cells.GetValueOrDefault(cell) ?? ValueInfo.Empty;
                var joined = Join(left, right, backedge);
                changed |= !ValueSame(left, joined); previous.Cells[cell] = joined;
            }
            foreach (var field in previous.Fields.Keys.Concat(incoming.Fields.Keys).Distinct().ToArray())
            {
                var left = FieldValue(previous, field); var right = FieldValue(incoming, field);
                var joined = Join(left, right, backedge);
                changed |= !ValueSame(left, joined); previous.Fields[field] = joined;
            }
            int count = previous.Clobbers.Count; previous.Clobbers.UnionWith(incoming.Clobbers); changed |= previous.Clobbers.Count != count;
            count = previous.EscapedCells.Count; previous.EscapedCells.UnionWith(incoming.EscapedCells); changed |= previous.EscapedCells.Count != count;
            if (incoming.AllFieldsUnknown && !previous.AllFieldsUnknown) { previous.AllFieldsUnknown = true; changed = true; }
            if (changed) Enqueue(target);
        }
        private static ValueInfo Join(ValueInfo left, ValueInfo right, bool backedge)
        {
            if (!backedge) return Union(left, right);
            var origins = right.Origins.Select(origin => left.Origins.FirstOrDefault(old => old.Root == origin.Root
                && old.Fields.Count < origin.Fields.Count && old.PrefixOf(origin))?.Subtree() ?? origin).ToArray();
            return Union(left, right with { Origins = origins });
        }
    }

    private static ValueInfo Union(ValueInfo left, ValueInfo right)
    {
        if (ReferenceEquals(left, ValueInfo.Empty)) return right;
        if (ReferenceEquals(right, ValueInfo.Empty)) return left;
        if (ValueSame(left, right)) return left;
        var origins = left.Origins.Concat(right.Origins).Distinct().ToArray();
        origins = origins.Where(origin => !origins.Any(other => other.Descendants && !other.Equals(origin) && other.PrefixOf(origin))).ToArray();
        var cells = left.Cells.Concat(right.Cells).Distinct().ToArray();
        var closures = left.Closures.Concat(right.Closures).GroupBy(closure => (closure.Function, closure.Native)).Select(group =>
        {
            var result = group.First();
            foreach (var item in group.Skip(1))
            {
                if (item.Captures.Length != result.Captures.Length) continue;
                result = result with { Captures = result.Captures.Zip(item.Captures, Union).ToArray() };
            }
            return result;
        }).ToArray();
        ValueInfo[]? elements = null;
        if (left.Elements is not null && right.Elements is not null)
            elements = [left.Elements.Concat(right.Elements).Aggregate(ValueInfo.Empty, Union)];
        else if (left == ValueInfo.Empty) elements = right.Elements;
        else if (right == ValueInfo.Empty) elements = left.Elements;
        return new(origins, cells, closures, left.Unknown || right.Unknown, elements, left.Deferred || right.Deferred, left.UnknownCell || right.UnknownCell, left.CallbackFreeRange && right.CallbackFreeRange);
    }
    private static bool ValueSame(ValueInfo left, ValueInfo right) => ReferenceEquals(left, right)
        || left.Unknown == right.Unknown && left.Deferred == right.Deferred && left.UnknownCell == right.UnknownCell && left.CallbackFreeRange == right.CallbackFreeRange && SetSame(left.Origins, right.Origins)
        && SetSame(left.Cells, right.Cells) && left.Closures.Length == right.Closures.Length
        && left.Closures.All(closure => right.Closures.Any(other => closure.Function == other.Function && closure.Native == other.Native
            && closure.Captures.Length == other.Captures.Length && closure.Captures.Zip(other.Captures, ValueSame).All(same => same)))
        && (left.Elements is null ? right.Elements is null : right.Elements is not null && left.Elements.Length == right.Elements.Length
            && left.Elements.Zip(right.Elements, ValueSame).All(same => same));
}
