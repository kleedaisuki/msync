namespace Msync.Optimization;

internal static class MemoryEffects
{
    internal static IReadOnlyList<FunctionMemoryEffects> Analyze(Module module) => AnalyzeWithCalls(module, new(module));

    internal static IReadOnlyList<FunctionMemoryEffects> AnalyzeWithCalls(Module module, CallTargetGraph calls)
    {
        var summaries = Enumerable.Repeat(FunctionMemoryEffects.Empty, module.Functions.Count).ToArray();
        var exact = calls.Targets;
        var targets = module.Functions.Select((function, index) => PotentialTargets(module, function, exact[index])).ToArray();
        var components = CallTargetGraph.Components(targets);
        var callers = module.Functions.Select(_ => new HashSet<int>()).ToArray();
        var contexts = new FunctionAnalysisContext?[summaries.Length];
        var work = new Queue<int>(Enumerable.Range(0, summaries.Length));
        var queued = Enumerable.Repeat(true, summaries.Length).ToArray();
        while (work.TryDequeue(out int index))
        {
            queued[index] = false;
            var next = Trivial(module, module.Functions[index])
                ?? new Analysis(module, index, summaries, components, contexts, callee => callers[callee].Add(index)).Run();
            if (Same(summaries[index], next)) continue;
            summaries[index] = next;
            foreach (int caller in callers[index])
                if (!queued[caller]) { queued[caller] = true; work.Enqueue(caller); }
        }
        return summaries;
    }

    private static Dictionary<int, int> PotentialTargets(Module module, FunctionCode function, Dictionary<int, int> exact)
    {
        var targets = exact.Values.ToHashSet();
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
            if (instruction.Text is not null) foreach (int target in FunctionDispatch.Targets(instruction.Text).Values) targets.Add(target);
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
                return value is CellValue || !Scalar(value.Type)
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
            or OpCode.Transfer or OpCode.LocalTransfer)
            ? function.Instructions.Any(instruction => instruction.Op == OpCode.StoreLocal && instruction.A < function.ParameterCount)
                ? FunctionMemoryEffects.Empty with { HasBindingWrites = true } : FunctionMemoryEffects.Empty
            : null;
    }

    private static bool Scalar(TypeRef type) =>
        (type.Kind & (TypeKind.Scope | TypeKind.Function | TypeKind.Range | TypeKind.Storage)) == 0;

    private static bool Same(FunctionMemoryEffects left, FunctionMemoryEffects right) => left.UnknownReads == right.UnknownReads
        && left.UnknownWrites == right.UnknownWrites && left.UnknownReturnAlias == right.UnknownReturnAlias
        && left.HasDeferredInvocation == right.HasDeferredInvocation && SetSame(left.Reads, right.Reads)
        && SetSame(left.Writes, right.Writes) && SetSame(left.Escapes, right.Escapes) && SetSame(left.ReturnAliases, right.ReturnAliases)
        && SetSame(left.Enumerates, right.Enumerates) && left.ReturnsCallbackFreeRange == right.ReturnsCallbackFreeRange
        && SetSame(left.IndexedReads, right.IndexedReads) && SetSame(left.IndexedWrites, right.IndexedWrites)
        && left.HasOrderingBoundary == right.HasOrderingBoundary && left.HasBindingWrites == right.HasBindingWrites;
    private static bool SetSame<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        if (left.Count == 0) return true;
        if (left.Count == 1) return EqualityComparer<T>.Default.Equals(left[0], right[0]);
        var values = new HashSet<T>(right);
        return left.All(values.Contains);
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

    private sealed class Analysis(Module module, int functionIndex, FunctionMemoryEffects[] summaries, int[] components,
        FunctionAnalysisContext?[] contexts, Action<int> dependency)
    {
        private readonly FunctionCode function = module.Functions[functionIndex];
        private readonly HashSet<MemoryAccess> reads = [], writes = [], escapes = [], returns = [], enumerates = [];
        private HashSet<IndexedFootprint>? indexedReads, indexedWrites;
        private bool unknownReads, unknownWrites, unknownReturn, deferred, ordering, bindingWrites;
        private bool sawRangeReturn, callbackFreeReturns = true;
        private readonly Queue<int> work = new();
        private readonly Dictionary<int, State> entries = [];
        private readonly HashSet<int> queued = [];
        private readonly HashSet<int> starts = [0];
        private readonly HashSet<int> indexedAllocations = [];
        private int instructionPosition;
        private long invalidationVersion;

        internal FunctionMemoryEffects Run()
        {
            var code = function.Instructions;
            if (code.Count == 0) return FunctionMemoryEffects.Empty;
            foreach (var instruction in code)
            {
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady) starts.Add(instruction.A);
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
                        if (!Scalar(function.ReturnType))
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
                    if (instruction.Op is OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady) Merge(instruction.A, state, instruction.A <= pc);
                    if (instruction.Op == OpCode.IteratorNext)
                    {
                        var exit = state.Copy(); Pop(exit); Merge(instruction.B, exit, false);
                    }
                    if (pc + 1 < code.Count && starts.Contains(pc + 1)) { Merge(pc + 1, state, false); break; }
                }
            }
            return new(reads.ToArray(), writes.ToArray(), escapes.ToArray(), returns.ToArray(), unknownReads, unknownWrites, unknownReturn, deferred)
            {
                Enumerates = enumerates.ToArray(),
                IndexedReads = indexedReads?.ToArray() ?? [],
                IndexedWrites = indexedWrites?.ToArray() ?? [],
                HasOrderingBoundary = ordering,
                HasBindingWrites = bindingWrites,
                ReturnsCallbackFreeRange = sawRangeReturn && callbackFreeReturns
            };
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
                bindingWrites |= cell.External || state.EscapedCells.Contains(cell);
                if (cell.External) writes.Add(new(new(MemoryRootKind.Capture, cell.Slot)));
                if (cell.External || state.EscapedCells.Contains(cell)) Escape(state, value, []);
                SetCell(state, cell, value);
            }
        }

        private void SetCell(State state, Cell cell, ValueInfo value)
        {
            if (state.Cells.TryGetValue(cell, out var previous) && ValueSame(previous, value)) return;
            state.Cells[cell] = value;
            invalidationVersion++;
        }

        private void Transfer(State state, Instruction instruction, int pc)
        {
            instructionPosition = pc;
            void Push(ValueInfo value) => state.Stack.Add(value);
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Barrier: ordering = true; break;
                case OpCode.LoadStaticScope:
                case OpCode.LoadStaticField: unknownReads = true; Push(ValueInfo.Top); break;
                case OpCode.PublishStatic:
                    foreach (var field in Arguments(state, ScopeOperands.Construction(module, instruction).Fields.Count))
                        Escape(state, field, []);
                    unknownWrites = true; break;
                case OpCode.StoreStaticField:
                    Escape(state, Pop(state), []); unknownWrites = true; Invalidate(state); Push(ValueInfo.Empty); break;
                case OpCode.Constant:
                    var literal = module.Constants[instruction.A];
                    Push(literal is FunctionValue callable ? ConstantFunction(callable)
                        : Scalar(literal.Type) ? ValueInfo.Empty : ValueInfo.Top);
                    break;
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
                    bindingWrites |= instruction.A < function.CaptureCount + function.ParameterCount;
                    if (state.Locals[instruction.A].UnknownCell) { unknownWrites = true; Invalidate(state); }
                    if (state.Locals[instruction.A].Cells.Length > 0) StoreCells(state, state.Locals[instruction.A], assigned);
                    else state.Locals[instruction.A] = assigned;
                    break;
                case OpCode.InitializeLocal: state.Locals[instruction.A] = Pop(state); break;
                case OpCode.Pop: Pop(state); break;
                case OpCode.Dup: var duplicated = Pop(state); Push(duplicated); Push(duplicated); break;
                case OpCode.NewScope:
                    var fields = ScopeOperands.Construction(module, instruction).Fields;
                    var items = Arguments(state, fields.Count);
                    MemoryAccess allocation = new(new(MemoryRootKind.Allocation, pc));
                    for (int index = 0; index < fields.Count; index++) state.Fields[allocation.Field(fields[index].Name)] = items[index];
                    Push(ValueInfo.At(allocation)); break;
                case OpCode.LoadField:
                    Push(ReadField(state, Dereference(state, Pop(state)), ScopeOperands.Projection(module, instruction).Name)); break;
                case OpCode.StoreField:
                    bindingWrites = true;
                    var value = Dereference(state, Pop(state)); var receiver = Dereference(state, Pop(state));
                    WriteField(state, receiver, ScopeOperands.Projection(module, instruction).Name, value); Push(ValueInfo.Empty); break;
                case OpCode.MakeClosure: Push(ValueInfo.Callable(new(instruction.A, Arguments(state, instruction.B)))); break;
                case OpCode.GetCapture:
                    var closure = Dereference(state, Pop(state)); var capture = ValueInfo.Empty;
                    foreach (var candidate in closure.Closures)
                        capture = Union(capture, instruction.A < candidate.Captures.Length ? candidate.Captures[instruction.A] : ValueInfo.Top);
                    if (closure.Unknown || closure.Origins.Length > 0) capture = Union(capture, ValueInfo.Top with { UnknownCell = true });
                    Push(capture); break;
                case OpCode.SpecializeClosure:
                    var specialized = Dereference(state, Pop(state));
                    if (instruction.A < 0) { Push(ValueInfo.Top); break; }
                    Push(specialized with
                    {
                        Closures = specialized.Closures.Select(candidate => candidate.Native is not null ? candidate
                        : candidate with { Function = instruction.Text is null ? instruction.A : FunctionDispatch.Targets(instruction.Text).GetValueOrDefault(candidate.Function, candidate.Function) }).ToArray()
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
                case OpCode.ExecutePlan:
                case OpCode.PlanResult:
                    Arguments(state, instruction.B);
                    ordering = true;
                    if (instruction.Op == OpCode.PlanResult) Push(ValueInfo.Empty);
                    break;
                case OpCode.Acquire:
                    ordering = true;
                    if (instruction.A != 0) Push(Pop(state)); break;
                case OpCode.JumpIfFalse: case OpCode.JumpIfTrue: Pop(state); break;
                case OpCode.AdmitScope: case OpCode.JumpIfStaticReady: case OpCode.Jump: break;
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
            function.Captures?.Select(ConstantCapture).ToArray() ?? [], function.NativeName));

        private static ValueInfo ConstantCapture(Value value) => value switch
        {
            FunctionValue nested => ConstantFunction(nested),
            CellValue => ValueInfo.Top with { UnknownCell = true },
            _ => Scalar(value.Type) ? ValueInfo.Empty : ValueInfo.Top,
        };

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
                else
                {
                    if (!ValueSame(FieldValue(state, field), value)) invalidationVersion++;
                    state.Fields[field] = value;
                }
            }
        }

        private ValueInfo Call(State state, ValueInfo callee, ValueInfo[] arguments, Instruction instruction)
        {
            if (instruction.B < 0)
            {
                Unknown(state, [callee, .. arguments]);
                deferred = true;
                return ValueInfo.Top;
            }
            var result = ValueInfo.Empty;
            if (callee.Unknown || callee.Origins.Length > 0 || callee.Closures.Length == 0)
            { Unknown(state, [callee, .. arguments]); result = ValueInfo.Top; }
            foreach (var closure in callee.Closures)
            {
                if (closure.Native is { } native) { result = Union(result, Native(state, native, arguments)); continue; }
                int target;
                if (instruction.Text is { } mapping)
                {
                    if (!FunctionDispatch.Targets(mapping).TryGetValue(closure.Function, out target))
                    { result = Union(result, ValueInfo.Top); continue; }
                }
                else target = instruction.B > 0 ? instruction.B - 1 : closure.Function;
                dependency(target);
                var summary = summaries[target];
                var returned = summary.ReturnAliases.Select(alias => ReturnAlias(state, alias, closure, arguments, target)).ToArray();
                var readPlan = summary.Reads.Select(access => (Access: access, Values: Map(state, access, closure, arguments, target,
                    access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0).ToArray())).ToArray();
                var writePlan = summary.Writes.Select(access => (Access: access, Values: Map(state, access, closure, arguments, target,
                    access.Root.Kind == MemoryRootKind.Capture && access.Fields.Count == 0).ToArray())).ToArray();
                var retained = summary.Escapes.Select(escape => ResolveAccess(state, escape, closure, arguments, true)).ToArray();
                foreach (var read in summary.IndexedReads) ProjectIndexed(read, false);
                foreach (var write in summary.IndexedWrites) ProjectIndexed(write, true);
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
                ordering |= summary.HasOrderingBoundary;
                bindingWrites |= summary.HasBindingWrites;
                if (summary.ReturnsCallbackFreeRange) result = result with { CallbackFreeRange = true };
                if (summary.HasDeferredInvocation && module.Functions[target].ReturnType.Kind == TypeKind.Range && !summary.ReturnsCallbackFreeRange)
                    result = result with { Deferred = true };

                void ProjectIndexed(IndexedFootprint effect, bool write)
                {
                    // A rebinding via one input can change a path rooted in
                    // a different, aliasing input before the indexed access.
                    // Only project through the caller's entry aliases if
                    // the receiver path cannot have been replaced.
                    if (effect.Receiver is not { } receiver || receiver.Descendants
                        || summary.HasBindingWrites && (receiver.Fields.Count > 0 || receiver.Root.Kind == MemoryRootKind.Capture)
                        || summary.IndexedWrites.Count > 0 && receiver.Fields.Contains(IndexedContents))
                    {
                        AddIndexed(effect with { Receiver = null }, write);
                        return;
                    }
                    RecordIndexed(ReturnAlias(state, receiver, closure, arguments, target), effect.Region, effect.Index, write);
                }
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
                        if (write) SetCell(state, cell, ValueInfo.Top);
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
            if (state.Clobbers.Add(location)) invalidationVersion++;
            if (!location.Descendants)
            {
                if (state.Fields.TryGetValue(location, out var value) && value.Cells.Length > 0)
                    StoreCells(state, value, ValueInfo.Top);
                else if (state.Fields.Remove(location)) invalidationVersion++;
                return;
            }
            foreach (var field in state.Fields.Keys.Where(location.PrefixOf).ToArray())
            {
                var value = state.Fields[field];
                foreach (var child in Dereference(state, value).Origins) Clobber(state, child.Subtree(), visited);
                if (value.Cells.Length > 0) StoreCells(state, value, ValueInfo.Top);
                else if (state.Fields.Remove(field)) invalidationVersion++;
            }
        }

        private ValueInfo Native(State state, string name, ValueInfo[] arguments)
        {
            if (!NativeSignatures.TryGet(name, out var signature)) { Unknown(state, arguments); return ValueInfo.Top; }
            if (signature.ResultRule == NativeResultRule.CastTarget && arguments.Length > 0) return arguments[0];
            NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
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
            if (signature.ElementCallback is { } callback)
            {
                var source = arguments[callback.RangeArgument];
                if (source.Elements is not { Length: 0 })
                    InvokeElements(state, arguments[callback.FunctionArgument], source);
            }
            else if ((behavior & NativeBehavior.Invokes) != 0) Unknown(state, arguments);
            if (signature.ResultRule == NativeResultRule.RangeItem && arguments.Length > 0)
                return RangeContents(state, arguments[0]);
            return signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length > 0 ? arguments[0]
                : signature.Result.Kind == TypeKind.Range ? new([], [], [], CallbackFreeRange: true) : ValueInfo.Empty;
        }

        private const string IndexedContents = "<indexed-items>";

        private void InvokeElements(State state, ValueInfo callback, ValueInfo source)
        {
            // A callback can redirect captured locations between elements. Its
            // summary invalidates writes, so remap until those invalidations
            // stabilize rather than treating the terminal as a single call.
            long previous;
            do
            {
                previous = invalidationVersion;
                Call(state, callback, [RangeContents(state, source)], new(OpCode.Call, 1));
            }
            while (previous != invalidationVersion);
        }

        private ValueInfo RangeContents(State state, ValueInfo value) => value.Elements is { } elements
            ? elements.Aggregate(ValueInfo.Empty, Union) : ReadField(state, value, IndexedContents);

        private ValueInfo IndexedStorage(State state, string name, NativeSignature signature, ValueInfo[] arguments)
        {
            if (name == "storage.vector_new")
            {
                if (MayEnumerate(arguments[0])) Enumerate(state, arguments[0]);
                return OwnedStorage(state, RangeContents(state, arguments[0]));
            }
            if (name is "storage.vector_empty" or "storage.vector_empty_like")
                return OwnedStorage(state, ValueInfo.Empty);
            var effect = IndexedNativeEffect.For(name);
            if (effect == default) { Unknown(state, arguments); return ValueInfo.Top; }
            var receiver = arguments[0];
            if (receiver.Unknown || receiver.UnknownCell || receiver.Origins.Any(External))
            {
                RecordIndexed(receiver, effect.Reads & IndexedRegion.Length, null, false);
                RecordIndexed(receiver, effect.Writes & IndexedRegion.Length, null, true);
                for (int index = 0; index < Math.Max(1, effect.Indices); index++)
                {
                    int? subscript = effect.Indices == 0 ? null : ConstantSubscript(index + 1, effect.Writes != 0);
                    RecordIndexed(receiver, effect.Reads & IndexedRegion.Contents, subscript, false);
                    RecordIndexed(receiver, effect.Writes & IndexedRegion.Contents, subscript, true);
                }
            }
            // Membership witnesses and value aliases are different facts. A
            // size observation does not inspect the stored values at all.
            if (IndexedNativeEffect.Read(name) == IndexedReadKind.Count) return ValueInfo.Empty;
            ValueInfo contents = ReadField(state, arguments[0], IndexedContents);
            if (name == "storage.vector_snapshot") return OwnedStorage(state, contents);
            if (signature.Effect == NativeEffect.Write)
            {
                ValueInfo next = name switch
                {
                    "storage.vector_clear" or "storage.vector_take" => ValueInfo.Empty,
                    "storage.vector_push" => Union(contents, arguments[1]),
                    "storage.vector_set" or "storage.vector_insert" =>
                        Union(contents, arguments[2]),
                    _ => contents
                };
                WriteField(state, arguments[0], IndexedContents, next);
            }
            if (name == "storage.vector_take") return OwnedStorage(state, contents);
            return signature.ResultRule == NativeResultRule.RangeItem ? contents : ValueInfo.Empty;
        }

        private void RecordIndexed(ValueInfo receiver, IndexedRegion region, int? index, bool write)
        {
            if (region == IndexedRegion.None) return;
            if (receiver.Unknown || receiver.UnknownCell)
            {
                if (write) unknownWrites = true; else unknownReads = true;
            }
            foreach (var origin in receiver.Origins)
            {
                if (!External(origin)) continue;
                AddIndexed(new(origin, region, index), write);
            }
        }

        private void AddIndexed(IndexedFootprint effect, bool write) =>
            (write ? indexedWrites ??= [] : indexedReads ??= []).Add(effect);

        private int? ConstantSubscript(int argument, bool write)
        {
            var code = function.Instructions;
            var native = code[instructionPosition];
            if (native.Op != OpCode.NativeCall) return null;
            int position = instructionPosition - (native.A - argument);
            if (position >= 0 && ReadLiteral(position) is int adjacent)
            {
                int pc = position + 1;
                while (pc <= instructionPosition && !starts.Contains(pc) && (pc == instructionPosition
                    || code[pc].Op is OpCode.LoadLocal or OpCode.LoadStorage or OpCode.Constant or OpCode.True or OpCode.False or OpCode.Null))
                    pc++;
                if (pc > instructionPosition) return adjacent;
            }
            // Exact writes can keep other reads available. Nonliteral read
            // metadata alone needn't build a graph when the fast case missed.
            if (!write) return null;
            var definitions = (contexts[functionIndex] ??= new(module)).DefUse(function).Inputs(instructionPosition)[argument];
            int? result = null;
            foreach (int definition in definitions)
            {
                if (definition < 0 || ReadLiteral(definition / 2) is not int value
                    || result is int previous && previous != value) return null;
                result = value;
            }
            return result;

            int? ReadLiteral(int pc) => code[pc] is { Op: OpCode.Constant, A: var id }
                && module.Constants[id] is NumberValue number ? IndexedNativeEffect.ExactIndex(number.Data) : null;
        }

        private ValueInfo OwnedStorage(State state, ValueInfo contents)
        {
            MemoryAccess allocation = new(new(MemoryRootKind.Allocation, instructionPosition));
            indexedAllocations.Add(instructionPosition);
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
        private void Invalidate(State state)
        {
            foreach (var cell in state.Cells.Keys.Where(cell => cell.External).ToArray()) SetCell(state, cell, ValueInfo.Top);
            if (!state.AllFieldsUnknown || state.Fields.Count != 0) invalidationVersion++;
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
            if (origin.Root.Kind != MemoryRootKind.Allocation || origin.Fields.Count != 0) yield break;
            var instruction = function.Instructions[origin.Root.Index];
            if (instruction.Op == OpCode.NewScope)
                foreach (var field in ScopeOperands.Construction(module, instruction).Fields)
                    yield return FieldValue(state, origin.Field(field.Name));
            else if (indexedAllocations.Contains(origin.Root.Index))
                yield return FieldValue(state, origin.Field(IndexedContents));
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
