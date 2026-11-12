namespace Msync.Core;

internal static class BytecodeValidation
{
    private static readonly TypeRef Unknown = new(TypeKind.Runtime);
    private const int Plain = -1, OpaqueCapture = -2;
    private readonly record struct Item(TypeRef Type, int? Function = null, bool Iterator = false, Value? Constant = null,
        int[]? Layouts = null, int Location = Plain, bool MayBePlain = false, bool Initialized = true, RangeFact? Range = null,
        Item[]? Captures = null);
    private sealed record RangeFact(Item Element, bool NonEmpty);
    private sealed record State(Item[] Stack, Item?[] Locals, HashSet<int> Ready);

    internal static void Validate(Module module, TypeRef[] runtimeTypes)
    {
        LayoutFacts layouts = new(module, runtimeTypes);
        foreach (var value in module.Constants) layouts.Constant(value);
        foreach (var function in module.Functions) new FunctionValidator(module, function, layouts).Validate();
    }

    private sealed class LayoutFacts(Module module, TypeRef[] runtimeTypes)
    {
        private readonly int[][] exact = Enumerable.Range(0, module.ScopeLayouts.Count).Select(id => new[] { id }).ToArray();
        private readonly int[]?[] domains = module.CodeTypes.Select(type => type.ScopeLayouts is { Count: > 0 } ids
            ? ids.ToArray() : null).ToArray();
        private readonly Dictionary<Value, Item> constants = new(ReferenceEqualityComparer.Instance);

        internal Item Typed(int type, bool precise = true) => new(runtimeTypes[type], Layouts: precise ? domains[type] : null);
        internal Item Scope(int layout) => new(TypeRef.Scope, Layouts: exact[layout]);
        internal bool RequiresLayout(int type) => domains[type] is not null;

        internal bool Fits(int type, Item value)
        {
            if (value.Iterator || value.Location != Plain) return false;
            var target = runtimeTypes[type];
            if (value.Type.Kind != TypeKind.Never && (target.Kind & value.Type.Kind) == 0) return false;
            return domains[type] is not { } domain || target.Accepts(value.Type)
                && ((value.Type.Kind & TypeKind.Scope) == 0 || value.Layouts is { } source && Covered(source, domain));
        }

        internal bool SameType(int left, int right)
        {
            while (left != right)
            {
                if (left < 0 || right < 0) return false;
                var first = module.CodeTypes[left];
                var second = module.CodeTypes[right];
                if (first.Kind != second.Kind || !domains[left].AsSpan().SequenceEqual(domains[right].AsSpan()))
                    return false;
                (left, right) = (first.Element, second.Element);
            }
            return true;
        }

        private static bool Covered(int[] subset, int[] superset)
        {
            foreach (int id in subset)
                if (Array.BinarySearch(superset, id) < 0) return false;
            return true;
        }

        internal Item Constant(Value value)
        {
            // Only aggregate or callable constants carry a structural proof.
            // Scalar literals already carry their complete type in the value.
            if (value is not (ScopeValue or FunctionValue { NativeName: null } or RangeValue { SnapshotValues: not null }))
                return new(value.Type, Constant: value);
            if (constants.TryGetValue(value, out var known)) return known;
            Item fact = value switch
            {
                ScopeValue scope => ScopeConstant(scope),
                FunctionValue { NativeName: null } callable => ClosureConstant(callable),
                RangeValue { SnapshotValues: { } snapshot } => RangeConstant(snapshot, value.Type),
                _ => new(value.Type, Constant: value)
            };
            constants.Add(value, fact);
            return fact;
        }

        private Item ScopeConstant(ScopeValue scope)
        {
            int id = scope.Layout.CodeLayoutId;
            if ((uint)id >= (uint)module.ScopeLayouts.Count)
                throw new MsyncException("A Scope constant has no portable layout identity");
            var fields = module.ScopeLayouts[id].Fields;
            if (fields.Count != scope.Count) throw new MsyncException("A Scope constant disagrees with its portable layout");
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (scope.Layout.Names[i] != field.Name || !Fits(field.StorageType, Constant(scope.GetField(i))))
                    throw new MsyncException($"Scope constant field '{field.Name}' disagrees with its portable layout");
            }
            return Scope(id);
        }

        private Item ClosureConstant(FunctionValue value)
        {
            var signature = module.Functions[value.Function].CodeSignature!;
            var operands = new Item[signature.Captures.Count];
            for (int i = 0; i < signature.Captures.Count; i++)
            {
                var capture = signature.Captures[i];
                operands[i] = Constant(value.Captures![i]);
                if (capture.Location || !signature.IsTemplate && !Fits(capture.Type, operands[i]))
                    throw new MsyncException("A function constant disagrees with its portable capture contract");
            }
            return new(TypeRef.Function, value.Function, Constant: value, Captures: operands);
        }

        private Item RangeConstant(IReadOnlyList<Value> values, TypeRef type)
        {
            Item element = new(new(TypeKind.Never));
            foreach (var value in values) element = Join(element, Constant(value));
            return new(type, Range: new(element, values.Count != 0));
        }

        internal Item Join(Item left, Item right)
        {
            if (left.Type.Kind == TypeKind.Never && left.Location == Plain) return right;
            if (right.Type.Kind == TypeKind.Never && right.Location == Plain) return left;
            if (left.Iterator != right.Iterator) throw new MsyncException("Invalid iterator ownership at bytecode join");
            if (left.Location >= 0 && right.Location >= 0 && !SameType(left.Location, right.Location))
                throw new MsyncException("Incompatible captured location types at bytecode join");
            int location = left.Location == OpaqueCapture || right.Location == OpaqueCapture
                ? OpaqueCapture : Math.Max(left.Location, right.Location);
            var range = JoinRange(left, right);
            var captures = left.Captures is { } known && right.Captures is { } other
                && known.AsSpan().SequenceEqual(other) ? known : null;
            return new(TypeRef.Union(left.Type, right.Type), left.Function == right.Function ? left.Function : null,
                left.Iterator, ValueRepresentation.Same(left.Constant, right.Constant) ? left.Constant : null,
                JoinLayouts(left, right), location,
                location >= 0 && (left.Location < 0 || right.Location < 0 || left.MayBePlain || right.MayBePlain),
                left.Initialized && right.Initialized, range, captures);
        }

        private static int[]? JoinLayouts(Item left, Item right)
        {
            if ((left.Type.Kind & TypeKind.Scope) == 0) return right.Layouts;
            if ((right.Type.Kind & TypeKind.Scope) == 0) return left.Layouts;
            if (left.Layouts is not { } l || right.Layouts is not { } r) return null;
            if (Covered(r, l)) return l;
            if (Covered(l, r)) return r;
            return l.Union(r).Order().ToArray();
        }

        private RangeFact? JoinRange(Item left, Item right)
        {
            if ((left.Type.Kind & TypeKind.Range) == 0) return right.Range;
            if ((right.Type.Kind & TypeKind.Range) == 0) return left.Range;
            if (left.Range is not { } l || right.Range is not { } r) return null;
            return new(Join(l.Element, r.Element), l.NonEmpty && r.NonEmpty);
        }
    }

    private sealed class FunctionValidator(Module module, FunctionCode function, LayoutFacts layouts)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly State?[] states = new State?[function.Instructions.Count + 1];
        private readonly Queue<int> work = new();
        private bool[] starts = [];

        internal void Validate()
        {
            if (code.Count == 0) throw new MsyncException($"Function '{function.Name}' has no bytecode body");
            starts = BlockStarts(code);
            var initial = new Item?[function.LocalCount];
            var signature = function.CodeSignature!;
            for (var i = 0; i < function.CaptureCount; i++) initial[i] = Capture(signature.Captures[i]);
            // Statically selected calls prove the entry contract; opaque calls
            // admit their actual receiver/arguments at the method boundary.
            for (var i = 0; i < function.ParameterCount; i++)
                initial[function.CaptureCount + i] = layouts.RequiresLayout(signature.Parameters[i])
                    ? layouts.Typed(signature.Parameters[i]) : new(Unknown);
            states[0] = new([], initial, new(signature.RequiredStatics ?? []));
            work.Enqueue(0);
            while (work.TryDequeue(out var start)) ValidateBlock(start);
        }

        private void ValidateBlock(int start)
        {
            if (start == code.Count) return;
            var input = states[start]!;
            var stack = input.Stack.ToList();
            // Abstract locals are mutable inside one control-flow block. Effects
            // do not require snapshots; only outgoing CFG edges publish states.
            var locals = input.Locals.ToArray();
            var ready = new HashSet<int>(input.Ready);
            for (var pc = start; pc < code.Count; pc++)
            {
                if (!Step(pc, stack, locals, ready)) return;
                if (!starts[pc + 1]) continue;
                Merge(pc + 1, stack, locals, ready);
                return;
            }
        }

        private bool[] BlockStarts(IReadOnlyList<Instruction> instructions)
        {
            var result = new bool[instructions.Count + 1];
            result[0] = result[^1] = true;
            for (var pc = 0; pc < instructions.Count; pc++)
            {
                var instruction = instructions[pc];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.JumpIfStaticReady)
                {
                    result[instruction.A] = result[pc + 1] = true;
                }
                else if (instruction.Op == OpCode.IteratorNext)
                {
                    result[instruction.B] = result[pc + 1] = true;
                }
                else if (instruction.Op == OpCode.Return) result[pc + 1] = true;
            }
            return result;
        }

        private Item Capture(CodeCapture capture) => capture.Location
            ? layouts.Typed(capture.Type) with { Location = capture.Type, Initialized = false }
            : new(Unknown);

        private bool Step(int pc, List<Item> stack, Item?[] locals, HashSet<int> ready)
        {
            var instruction = code[pc];
            MsyncException Error(string message) => new($"Invalid bytecode in '{function.Name}' at instruction {pc} ({instruction.Op}): {message}", instruction.Span);
            Item Pop()
            {
                if (stack.Count == 0) throw Error("operand stack underflow");
                var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item;
            }
            int OperandStart(int count)
            {
                if (count < 0 || count > stack.Count) throw Error($"operation requires {count} stack operands, has {stack.Count}");
                return stack.Count - count;
            }
            void Consume(int count) => stack.RemoveRange(OperandStart(count), count);
            void Require(Item item, TypeKind kinds)
            {
                if (item.Iterator || item.Location != Plain || (item.Type.Kind & kinds) == TypeKind.Never)
                    throw Error($"expected {new TypeRef(kinds)}, found {(item.Iterator ? "iterator" : item.Type)}");
            }
            void Push(TypeRef type, Value? constant = null) => stack.Add(new(type, Constant: constant));
            Item Local(int slot) => locals[slot] ?? throw Error($"local {slot} is not defined on every incoming path");
            void RequireStorage(int type, Item value)
            {
                if (!layouts.Fits(type, value)) throw Error($"value disagrees with portable storage type {type}");
            }
            Item StoredValue(int type) => layouts.RequiresLayout(type) ? layouts.Typed(type) : new(Unknown);
            void RequireReady(int declaration)
            {
                if (!ready.Contains(declaration)) throw Error($"static Scope {declaration} is not definitely initialized");
            }
            void Construct(int id)
            {
                var fields = module.ScopeLayouts[id].Fields;
                int start = OperandStart(fields.Count);
                for (int i = 0; i < fields.Count; i++)
                {
                    var value = stack[start + i];
                    int field = fields[i].StorageType;
                    if (value.Location != Plain && (value.Location < 0 || !value.Initialized || !layouts.SameType(value.Location, field)))
                        throw Error($"captured field '{fields[i].Name}' disagrees with the declared layout");
                    RequireStorage(field, value with { Location = Plain });
                }
                stack.RemoveRange(start, fields.Count);
            }
            Item Field(Item receiver, CodeFieldProjection projection, Item? assigned = null)
            {
                Require(receiver, TypeKind.Scope);
                if (projection.Cases.Count == 0)
                {
                    // A structural projection stays checked when inlining
                    // exposes more receiver layouts than its producer knew.
                    if (assigned is { Location: not Plain }) throw Error("cannot assign a raw location as a field value");
                    return new(Unknown);
                }
                if (receiver.Type.Kind != TypeKind.Scope || receiver.Layouts is not { } domain)
                    throw Error($"field '{projection.Name}' requires an admitted finite non-null Scope domain");
                Item result = new(new(TypeKind.Never));
                int at = 0;
                foreach (int id in domain)
                {
                    while (at < projection.Cases.Count && projection.Cases[at].Layout < id) at++;
                    if (at == projection.Cases.Count || projection.Cases[at].Layout != id)
                        throw Error($"field '{projection.Name}' has no case for receiver layout {id}");
                    int type = module.ScopeLayouts[id].Fields[projection.Cases[at].Ordinal].StorageType;
                    if (assigned is { } value) RequireStorage(type, value);
                    else result = layouts.Join(result, StoredValue(type));
                }
                return result;
            }
            Item SourceResult(int target)
            {
                var signature = module.Functions[target].CodeSignature!;
                if (signature.IsTemplate) throw Error("an open closure template must be specialized before invocation");
                foreach (int declaration in signature.RequiredStatics ?? []) RequireReady(declaration);
                return StoredValue(signature.Result);
            }
            void CheckCapture(CodeCapture capture, Item supplied)
            {
                if (capture.Location)
                {
                    if (supplied.Location < 0 || supplied.MayBePlain || !layouts.SameType(supplied.Location, capture.Type))
                        throw Error("closure captured location disagrees with its declared payload");
                }
                else RequireStorage(capture.Type, supplied);
            }
            void CaptureAbi(Item source, int target)
            {
                var after = module.Functions[target].CodeSignature!.Captures;
                if (source.Captures is { } supplied)
                {
                    if (supplied.Length != after.Count) throw Error("specialization changes the capture count");
                    for (int i = 0; i < supplied.Length; i++) CheckCapture(after[i], supplied[i]);
                }
            }
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Barrier: break;
                case OpCode.ExecutePlan: Consume(instruction.B); break;
                case OpCode.PlanResult: Consume(instruction.B); Push(TypeRef.Bool); break;
                case OpCode.Acquire:
                    if (instruction.A != 0) { var process = Pop(); Require(process, TypeKind.Process); stack.Add(process); }
                    break;
                case OpCode.Constant:
                    stack.Add(layouts.Constant(module.Constants[instruction.A])); break;
                case OpCode.Null: Push(TypeRef.Null, Value.Null); break;
                case OpCode.True: Push(TypeRef.Bool, Value.True); break;
                case OpCode.False: Push(TypeRef.Bool, Value.False); break;
                case OpCode.LoadLocal:
                    var local = Local(instruction.A);
                    if (local.Location == OpaqueCapture) local = new(Unknown);
                    if (local.Location >= 0)
                    {
                        var payload = StoredValue(local.Location);
                        local = local.Initialized ? payload : payload with { Type = payload.Type.WithNull };
                    }
                    stack.Add(local); break;
                case OpCode.LoadStorage: stack.Add(Local(instruction.A)); break;
                case OpCode.LoadCell:
                    var current = Local(instruction.A);
                    if (current.Location == OpaqueCapture) throw Error("cannot refine an opaque captured location");
                    int locationType = instruction.B - 1;
                    if (current.Location >= 0 && !layouts.SameType(current.Location, locationType))
                        throw Error("captured location cannot change its storage type");
                    bool initialized = current.Location >= 0 ? current.Initialized : current.Type != TypeRef.Null;
                    if (current.Location < 0 && initialized) RequireStorage(locationType, current);
                    var place = layouts.Typed(locationType) with { Location = locationType, Initialized = initialized };
                    locals[instruction.A] = place;
                    stack.Add(place);
                    break;
                case OpCode.StoreLocal:
                    var stored = Pop();
                    if (locals[instruction.A] is { Location: OpaqueCapture }) throw Error("cannot write an opaque captured location");
                    if (locals[instruction.A] is { Location: >= 0 } destination)
                    {
                        RequireStorage(destination.Location, stored);
                        locals[instruction.A] = destination with { Initialized = true };
                    }
                    else locals[instruction.A] = stored;
                    break;
                case OpCode.InitializeLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup:
                    var duplicate = Pop(); stack.Add(duplicate); stack.Add(duplicate); break;
                case OpCode.Jump: Merge(instruction.A, stack, locals, ready); return false;
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                    var condition = Pop(); Require(condition, TypeKind.Bool);
                    if (condition.Constant is BoolValue known)
                    {
                        if (known.Data == (instruction.Op == OpCode.JumpIfTrue)) { Merge(instruction.A, stack, locals, ready); return false; }
                    }
                    else if (NullTest(pc, out int checkedSlot, out bool inverted)
                        && locals[checkedSlot] is { Location: Plain } checkedValue)
                    {
                        bool takenIsNull = (instruction.Op == OpCode.JumpIfTrue) != inverted;
                        var isNull = new Item(TypeRef.Null, Constant: Value.Null);
                        var notNull = checkedValue with { Type = checkedValue.Type.WithoutNull };
                        locals[checkedSlot] = takenIsNull ? isNull : notNull;
                        Merge(instruction.A, stack, locals, ready);
                        locals[checkedSlot] = takenIsNull ? notNull : isNull;
                    }
                    else Merge(instruction.A, stack, locals, ready);
                    break;
                case OpCode.JumpIfStaticReady:
                    bool alreadyReady = !ready.Add(instruction.B);
                    Merge(instruction.A, stack, locals, ready);
                    if (alreadyReady) return false;
                    ready.Remove(instruction.B);
                    break;
                case OpCode.Return:
                    if (stack.Count > 1) throw Error($"return leaves {stack.Count} values on the operand stack");
                    int resultType = function.CodeSignature!.Result;
                    if (layouts.RequiresLayout(resultType)) RequireStorage(resultType, stack.Count == 0 ? new(TypeRef.Null) : stack[0]);
                    if (stack.Count != 0 && stack[0].Location != Plain) throw Error("return leaks a raw location");
                    return false;
                case OpCode.MakeClosure:
                    var closureSignature = module.Functions[instruction.A].CodeSignature!;
                    var captures = closureSignature.Captures;
                    if (instruction.B != captures.Count) throw Error("closure capture layout disagrees with function");
                    int start = OperandStart(instruction.B);
                    var captured = stack.GetRange(start, captures.Count).ToArray();
                    if (!closureSignature.IsTemplate)
                        for (int i = 0; i < captures.Count; i++) CheckCapture(captures[i], captured[i]);
                    Consume(instruction.B); stack.Add(new(TypeRef.Function, instruction.A, Captures: captured)); break;
                case OpCode.SpecializeClosure:
                    var adapted = Pop();
                    Require(adapted, TypeKind.Function);
                    int? adaptation = null;
                    if (adapted.Function is int original && instruction.A >= 0)
                    {
                        if (instruction.Text is null) adaptation = instruction.A;
                        else if (FunctionDispatch.Targets(instruction.Text).TryGetValue(original, out int selected))
                            adaptation = selected;
                        if (adaptation is int target) CaptureAbi(adapted, target);
                    }
                    stack.Add(new(TypeRef.Function, adaptation, Captures: adapted.Captures)); break;
                case OpCode.Call:
                case OpCode.ProcessCall:
                    {
                        int calleeAt = OperandStart(instruction.A + 1);
                        var callee = stack[calleeAt];
                        bool directSource = instruction.Op == OpCode.Call && instruction.B > 0 && instruction.Text is null;
                        Require(callee, directSource ? TypeKind.Function : TypeKind.Function | TypeKind.Path | TypeKind.String);
                        int? target = directSource ? instruction.B - 1 : null;
                        if (!directSource && instruction.Op == OpCode.Call && callee.Function is int identity)
                        {
                            if (instruction.B < 0)
                                target = module.SessionDispatches![-instruction.B - 1].Targets.TryGetValue(identity, out int selected) ? selected : null;
                            else if (instruction.Text is { } dispatch)
                                target = FunctionDispatch.Targets(dispatch).TryGetValue(identity, out int alternative) ? alternative : null;
                            else target = identity;
                        }
                        Item returned = new((callee.Type.Kind & ~(TypeKind.Path | TypeKind.String)) == 0 ? TypeRef.Process : Unknown);
                        if (target is int index)
                        {
                            if (index < 0 || index >= module.Functions.Count) throw Error($"callee {index} does not identify a function");
                            if (module.Functions[index].ParameterCount != instruction.A) throw Error("call argument count disagrees with function");
                            returned = SourceResult(index);
                            CaptureAbi(callee, index);
                            var parameters = module.Functions[index].CodeSignature!.Parameters;
                            for (int i = 0; i < parameters.Count; i++)
                                if (layouts.RequiresLayout(parameters[i])) RequireStorage(parameters[i], stack[calleeAt + i + 1]);
                        }
                        for (int i = calleeAt + 1; i < stack.Count; i++)
                            if (stack[i].Location != Plain) throw Error("call argument cannot be a raw location");
                        Consume(instruction.A + 1);
                        stack.Add(returned);
                        break;
                    }
                case OpCode.NativeCall:
                    if (instruction.Text == "_core.iterator_close")
                    {
                        if (instruction.A != 1 || !Pop().Iterator) throw Error("iterator cleanup requires one loop iterator");
                        Push(TypeRef.Null); break;
                    }
                    if (instruction.Text is null || !NativeSignatures.TryGet(instruction.Text, out var signature)) throw Error($"native '{instruction.Text}' is not registered");
                    int arguments = OperandStart(instruction.A);
                    if (!signature.Variadic && instruction.A != signature.Parameters.Count || signature.Variadic && instruction.A < signature.Parameters.Count)
                        throw Error("native argument count disagrees with registry signature");
                    for (var i = 0; i < signature.Parameters.Count; i++) Require(stack[arguments + i], signature.Parameters[i].Kind);
                    var source = instruction.A == 0 ? null : stack[arguments].Range;
                    stack.RemoveRange(arguments, instruction.A);
                    if (source is { NonEmpty: true } range
                        && instruction.Text is "ranges.front" or "ranges.first" or "ranges.back" or "ranges.last" or "ranges.at")
                        stack.Add(range.Element);
                    else Push(signature.Result);
                    break;
                case OpCode.LoadBuiltin: Push(instruction.Text?.TrimStart('$') is "std" ? TypeRef.Scope : TypeRef.Connection); break;
                case OpCode.MakeRange:
                    int first = OperandStart(instruction.A);
                    Item element = new(new(TypeKind.Never));
                    for (int i = first; i < stack.Count; i++)
                    {
                        if (stack[i].Location != Plain) throw Error("Range item cannot be a raw location");
                        element = layouts.Join(element, stack[i]);
                    }
                    Consume(instruction.A);
                    stack.Add(new(new(TypeKind.Range, element.Type), Range: new(element, instruction.A != 0)));
                    break;
                case OpCode.MakeLazyRange:
                    int items = OperandStart(instruction.A);
                    if (instruction.Text is { } mask && (mask.Length != instruction.A || mask.Any(c => c is not ('0' or '1')))) throw Error("lazy range mask disagrees with item table");
                    for (var i = 0; i < instruction.A; i++)
                    {
                        if (instruction.Text is null || instruction.Text[i] == '1') Require(stack[items + i], TypeKind.Function);
                        else if (stack[items + i].Location != Plain) throw Error("Range item cannot be a raw location");
                    }
                    stack.RemoveRange(items, instruction.A);
                    if (instruction.B != 0 && (instruction.B < 0 || instruction.B > module.Constants.Count || module.Constants[instruction.B - 1] is not TypeValue)) throw Error("lazy range item type does not identify a Type constant");
                    Push(new(TypeKind.Range)); break;
                case OpCode.DeferRange: Require(Pop(), TypeKind.Function); Push(new(TypeKind.Range)); break;
                case OpCode.NewScope:
                    Construct(instruction.A);
                    stack.Add(layouts.Scope(instruction.A));
                    break;
                case OpCode.LoadField:
                    stack.Add(Field(Pop(), module.FieldProjections[instruction.A]));
                    break;
                case OpCode.StoreField:
                    var assigned = Pop();
                    _ = Field(Pop(), module.FieldProjections[instruction.A], assigned);
                    Push(TypeRef.Null);
                    break;
                case OpCode.AdmitScope:
                    Require(Pop(), TypeKind.Scope);
                    stack.Add(layouts.Typed(instruction.A));
                    break;
                case OpCode.PublishStatic:
                    Construct(module.StaticScopes[instruction.A].Layout);
                    ready.Add(instruction.A);
                    break;
                case OpCode.LoadStaticScope:
                    RequireReady(instruction.A);
                    stack.Add(layouts.Scope(module.StaticScopes[instruction.A].Layout));
                    break;
                case OpCode.LoadStaticField:
                case OpCode.StoreStaticField:
                    RequireReady(instruction.A);
                    int field = module.ScopeLayouts[module.StaticScopes[instruction.A].Layout].Fields[instruction.B].StorageType;
                    if (instruction.Op == OpCode.StoreStaticField)
                    {
                        RequireStorage(field, Pop());
                        Push(TypeRef.Null);
                    }
                    else stack.Add(StoredValue(field));
                    break;
                case OpCode.GetCapture:
                    var closure = Pop();
                    Require(closure, TypeKind.Function);
                    if (closure.Captures is { } operands && (uint)instruction.A < (uint)operands.Length)
                        stack.Add(operands[instruction.A]);
                    else if (closure.Function is int owner && !module.Functions[owner].CodeSignature!.IsTemplate
                        && (uint)instruction.A < (uint)module.Functions[owner].CaptureCount)
                        stack.Add(Capture(module.Functions[owner].CodeSignature!.Captures[instruction.A]));
                    else stack.Add(new(Unknown, Location: OpaqueCapture));
                    break;
                case OpCode.Qualify: Require(Pop(), TypeKind.String | TypeKind.Path); Require(Pop(), TypeKind.Connection | TypeKind.Scope); Push(TypeRef.Path); break;
                case OpCode.PathJoin: Require(Pop(), TypeKind.String | TypeKind.Path); Require(Pop(), TypeKind.String | TypeKind.Path); Push(TypeRef.Path); break;
                case OpCode.SelectPath: Require(Pop(), TypeKind.String | TypeKind.Path); Push(new(TypeKind.Range, TypeRef.Path)); break;
                case OpCode.Convert:
                    Require(Pop(), TypeKind.Path | TypeKind.String);
                    if (instruction.Text is not ("Path" or "String")) throw Error($"unknown conversion '{instruction.Text}'");
                    Push(instruction.Text == "Path" ? TypeRef.Path : TypeRef.String); break;
                case OpCode.Transfer:
                case OpCode.LocalTransfer:
                    Require(Pop(), TypeKind.Path | TypeKind.String); Require(Pop(), TypeKind.Path | TypeKind.String | TypeKind.Range); Push(TypeRef.Bool); break;
                case OpCode.Not:
                    var boolean = Pop(); Require(boolean, TypeKind.Bool); Push(TypeRef.Bool, boolean.Constant is BoolValue b ? b.Data ? Value.False : Value.True : null); break;
                case OpCode.IsNull:
                    var tested = Pop(); Push(TypeRef.Bool, tested.Type == TypeRef.Null ? Value.True : !tested.Type.Nullable ? Value.False : null); break;
                case OpCode.IsType:
                    var expected = Pop(); Require(expected, TypeKind.Type); var actual = Pop();
                    var actualType = actual.Constant is TypeValue knownType ? knownType.Data : actual.Type;
                    var expectedType = (expected.Constant as TypeValue)?.Data;
                    Push(TypeRef.Bool, expectedType is null || actual.Constant is not TypeValue && (actual.Type.Kind & TypeKind.Type) != 0
                        ? null : expectedType.Accepts(actualType) ? Value.True : (expectedType.Kind & actualType.Kind) == 0 ? Value.False : null); break;
                case OpCode.RegexMatch:
                    if (instruction.Text is null) Require(Pop(), TypeKind.String | TypeKind.Path);
                    var regexInput = Pop(); Push(TypeRef.Bool, (regexInput.Type.Kind & (TypeKind.String | TypeKind.Path)) == 0 ? Value.False : null); break;
                case OpCode.Equal:
                case OpCode.NotEqual:
                case OpCode.Less:
                case OpCode.LessEqual:
                case OpCode.Greater:
                case OpCode.GreaterEqual:
                    Require(Pop(), TypeKind.Bool | TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Type); Require(Pop(), TypeKind.Bool | TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Type); Push(TypeRef.Bool); break;
                case OpCode.Add:
                    var right = Pop(); var left = Pop(); Require(right, TypeKind.Number | TypeKind.String); Require(left, TypeKind.Number | TypeKind.String); Push(TypeRef.Union(left.Type, right.Type)); break;
                case OpCode.Subtract:
                case OpCode.Multiply:
                case OpCode.Divide:
                case OpCode.Remainder:
                    Require(Pop(), TypeKind.Number); Require(Pop(), TypeKind.Number); Push(TypeRef.Number); break;
                case OpCode.Negate: Require(Pop(), TypeKind.Number); Push(TypeRef.Number); break;
                case OpCode.GetIterator:
                    var iterable = Pop();
                    Require(iterable, TypeKind.Range);
                    stack.Add(new(new(TypeKind.Range), Iterator: true, Range: iterable.Range));
                    break;
                case OpCode.IteratorNext:
                    if (locals[instruction.A] is not { Iterator: true } iterator) throw Error("iteration local does not contain an iterator");
                    Merge(instruction.B, stack, locals, ready);
                    stack.Add(iterator.Range?.Element ?? new(Unknown));
                    break;
                default: throw Error("operation has no executable definition");
            }
            return true;
        }

        private bool NullTest(int branch, out int slot, out bool inverted)
        {
            slot = -1;
            inverted = false;
            if (branch < 2 || starts[branch]) return false;
            int test = branch - 1;
            if (code[test].Op == OpCode.Not)
            {
                if (starts[test] || test < 2) return false;
                inverted = true;
                test--;
            }
            if (starts[test] || code[test].Op != OpCode.IsNull || code[test - 1] is not { Op: OpCode.LoadLocal, A: var local })
                return false;
            slot = local;
            return true;
        }
        private void Merge(int target, List<Item> stack, Item?[] locals, HashSet<int> ready)
        {
            var previous = states[target];
            if (previous is null) { states[target] = new(stack.ToArray(), locals.ToArray(), new(ready)); work.Enqueue(target); return; }
            if (previous.Stack.Length != stack.Count) throw new MsyncException($"Invalid bytecode in '{function.Name}': control-flow stack heights disagree at instruction {target}");
            var mergedStack = new Item[stack.Count]; var mergedLocals = new Item?[locals.Length]; var changed = false;
            for (var i = 0; i < stack.Count; i++) { mergedStack[i] = layouts.Join(previous.Stack[i], stack[i]); changed |= mergedStack[i] != previous.Stack[i]; }
            for (var i = 0; i < locals.Length; i++)
            {
                mergedLocals[i] = previous.Locals[i] is { } left && locals[i] is { } right ? layouts.Join(left, right) : null;
                changed |= mergedLocals[i] != previous.Locals[i];
            }
            var mergedReady = previous.Ready;
            if (!mergedReady.IsSubsetOf(ready))
            {
                mergedReady = new(mergedReady);
                mergedReady.IntersectWith(ready);
                changed = true;
            }
            if (changed) { states[target] = new(mergedStack, mergedLocals, mergedReady); work.Enqueue(target); }
        }
    }
}
