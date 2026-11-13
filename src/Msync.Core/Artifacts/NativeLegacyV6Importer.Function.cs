namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed partial class FunctionImport
        {
            private sealed record Item(int Type, int Function = -1, int Iterator = -1, int Register = -1, int Environment = -1, int IteratorElement = -1, int KnownType = -1, int Producer = -1, TypeRef? Descriptor = null);
            private sealed record State(List<Item> Stack, Item[] Locals)
            {
                internal State Copy() => new([.. Stack], (Item[])Locals.Clone());
            }
            private readonly Importer owner;
            private readonly int index;
            private readonly LegacyV6Function function;
            private readonly State?[] states;
            private readonly List<NativeSlot> slots = [];
            private readonly List<NativeInstruction> code = [];
            private readonly List<(int Instruction, char Operand, int Target)> patches = [];
            private readonly Dictionary<int, int> iteratorIds = [];
            private readonly Dictionary<int, int> locations = [];
            private int[][] incoming = [];
            private bool emitting;
            private SourceSpan? source;
            internal int[] Map { get; }
            private int NullType => owner.Type(TypeKind.Null);
            private int BoolType => owner.Type(TypeKind.Bool);
            private int NumberType => owner.Type(TypeKind.Number);

            internal FunctionImport(Importer owner, int index, LegacyV6Function? sourceFunction = null)
            {
                this.owner = owner; this.index = index; function = sourceFunction ?? owner.Module.Functions[index];
                states = new State?[function.Instructions.Count + 1]; Map = new int[states.Length];
                for (int pc = 0; pc < function.Instructions.Count; pc++)
                {
                    LegacyV6Instruction instruction = function.Instructions[pc];
                    if (instruction.Op == LegacyV6Op.GetIterator) iteratorIds.Add(pc, iteratorIds.Count);
                    if (instruction.Op == LegacyV6Op.LoadCell) locations[instruction.A] = instruction.B - 1;
                }
                for (int capture = 0; capture < function.Signature.Captures.Count; capture++)
                    if (function.Signature.Captures[capture].Location) locations[capture] = function.Signature.Captures[capture].Type;
            }

            internal void Infer()
            {
                Array.Clear(states);
                Item[] locals = Enumerable.Repeat(new Item(NullType), function.LocalCount).ToArray();
                for (int capture = 0; capture < function.Signature.Captures.Count; capture++)
                    locals[capture] = new(function.Signature.Captures[capture].Type);
                for (int parameter = 0; parameter < function.Signature.Parameters.Count; parameter++)
                    locals[function.Signature.Captures.Count + parameter] = new(function.Signature.Parameters[parameter]);
                states[0] = new([], locals);
                Queue<int> pending = new(); pending.Enqueue(0);
                while (pending.TryDequeue(out int pc))
                {
                    if (pc == function.Instructions.Count) continue;
                    State state = states[pc]!.Copy();
                    foreach (var (target, next) in Step(pc, state))
                        if (Merge(target, next)) pending.Enqueue(target);
                }
            }

            internal NativeFunctionCode Run()
            {
                for (int local = 0; local < function.LocalCount; local++)
                {
                    int type = local < function.Signature.Captures.Count ? function.Signature.Captures[local].Type
                        : local < function.Signature.Captures.Count + function.Signature.Parameters.Count
                        ? function.Signature.Parameters[local - function.Signature.Captures.Count] : owner.Type(TypeKind.Never);
                    foreach (State? state in states)
                        if (state is not null && state.Locals[local].Iterator < 0) type = owner.Join(type, state.Locals[local].Type);
                    if (locations.TryGetValue(local, out int payload)) type = payload;
                    NativeSlotRole role = local < function.Signature.Captures.Count ? NativeSlotRole.Capture
                        : local < function.Signature.Captures.Count + function.Signature.Parameters.Count ? NativeSlotRole.Parameter : NativeSlotRole.Local;
                    slots.Add(new(type, role, locations.ContainsKey(local)));
                }
                emitting = true;
                incoming = states.Select(state => state is null ? [] : state.Stack.Select(item => Register(item.Type)).ToArray()).ToArray();
                int entries = function.Signature.Captures.Count + function.Signature.Parameters.Count;
                foreach (var (local, payload) in locations.OrderBy(item => item.Key))
                {
                    if (local < function.Signature.Captures.Count && function.Signature.Captures[local].Location) continue;
                    Item? initial = local < entries ? Result(payload, new(NativeOp.Copy, A: local)) : null;
                    int layout = LocationLayout(payload);
                    Item allocation = Result(owner.Scope(layout), new(NativeOp.Allocate, A: layout));
                    owner.Allocations.Add(new(code.Count - 1, layout, Function: index));
                    Item address = FieldAddress(allocation, layout, 0);
                    if (initial is not null) Add(new(NativeOp.Store, A: address.Register, B: initial.Register, C: 0, Type: payload));
                    Add(new(NativeOp.BindLocation, A: local, B: address.Register));
                }
                for (int local = entries; local < function.LocalCount; local++)
                    if (!locations.ContainsKey(local))
                    {
                        int nil = Constant(new(NativeKind.Null), NullType);
                        Add(new(NativeOp.Copy, local, nil));
                    }
                for (int pc = 0; pc < function.Instructions.Count; pc++)
                {
                    Map[pc] = code.Count;
                    if (states[pc] is not { } entry) continue;
                    State state = entry.Copy();
                    for (int item = 0; item < state.Stack.Count; item++) state.Stack[item] = state.Stack[item] with { Register = incoming[pc][item] };
                    var edges = Step(pc, state).ToArray();
                    if (edges.Length == 1) Edge(edges[0].Target, edges[0].State);
                    else if (edges.Length == 2)
                    {
                        // Branch destinations are edge blocks, so parallel stack assignments cannot overwrite one another.
                        int branch = code.Count - 1;
                        bool iterator = code[branch].Op == NativeOp.NextIterator;
                        if (!iterator) code[branch] = code[branch] with { B = code.Count };
                        Edge(edges[0].Target, edges[0].State);
                        code[branch] = iterator ? code[branch] with { B = code.Count } : code[branch] with { C = code.Count };
                        if (iterator) Add(new(NativeOp.CloseIterator, A: code[branch].A));
                        Edge(edges[1].Target, edges[1].State);
                    }
                }
                Map[^1] = code.Count;
                if (states[^1] is { } fallthrough)
                {
                    int value = fallthrough.Stack.Count == 0 ? Constant(new(NativeKind.Null), NullType) : incoming[^1][^1];
                    Add(new(NativeOp.Return, A: value));
                }
                else Add(new(NativeOp.Unreachable, Symbol: "Format-6 unreachable function end"));
                foreach (var patch in patches)
                    code[patch.Instruction] = patch.Operand == 'A' ? code[patch.Instruction] with { A = Map[patch.Target] }
                        : code[patch.Instruction] with { B = Map[patch.Target] };
                Compact();
                return new(function.Name, slots.ToArray(), code.ToArray(),
                    new(function.Signature.Parameters, function.Signature.Result,
                        function.Signature.Captures.Select(capture => new NativeCaptureContract(capture.Type,
                            capture.Location ? NativeCaptureMode.Location : NativeCaptureMode.Value)).ToArray()),
                    function.Signature.Parameters.Count, function.Signature.Captures.Count, iteratorIds.Count, function.Signature.IsTemplate)
                {
                    ReflectionSignature = new(function.Signature.Parameters.Select(owner.ReflectionType).ToArray(), owner.ReflectionType(function.Signature.Result))
                };
            }

            private bool Merge(int pc, State state)
            {
                if ((uint)pc >= (uint)states.Length) throw new MsyncException($"Invalid format-6 target {pc}", source);
                if (states[pc] is not { } previous) { states[pc] = state.Copy(); return true; }
                if (previous.Stack.Count != state.Stack.Count) throw new MsyncException($"Format-6 stack heights disagree at {pc} in '{function.Name}'", source);
                bool changed = false;
                for (int item = 0; item < state.Stack.Count; item++)
                { Item merged = Join(previous.Stack[item], state.Stack[item]); changed |= merged != previous.Stack[item]; previous.Stack[item] = merged; }
                for (int local = 0; local < state.Locals.Length; local++)
                { Item merged = Join(previous.Locals[local], state.Locals[local]); changed |= merged != previous.Locals[local]; previous.Locals[local] = merged; }
                return changed;
            }
            private Item Join(Item a, Item b) => new(owner.Join(a.Type, b.Type), a.Function == b.Function ? a.Function : -1,
                a.Iterator == b.Iterator ? a.Iterator : -1, Environment: a.Environment == b.Environment ? a.Environment : -1, IteratorElement: a.IteratorElement == b.IteratorElement ? a.IteratorElement : -1, KnownType: a.KnownType == b.KnownType ? a.KnownType : -1, Producer: a.Producer == b.Producer ? a.Producer : -1, Descriptor: a.Descriptor == b.Descriptor ? a.Descriptor : null);
            private int Register(int type) { if (!emitting) return -1; slots.Add(new(type)); return slots.Count - 1; }
            private void Add(NativeInstruction instruction) { if (emitting) code.Add(instruction with { Source = source }); }
            private int Constant(NativeConstant literal, int type)
            {
                int result = Register(type);
                if (emitting) { int constant = owner.Constants.Count; owner.Constants.Add(literal); Add(new(NativeOp.Constant, result, constant, Type: type)); }
                return result;
            }
            private void Edge(int target, State state)
            {
                if (!emitting) return;
                for (int item = 0; item < state.Stack.Count; item++)
                    Add(new(NativeOp.Copy, incoming[target][item], state.Stack[item].Register));
                patches.Add((code.Count, 'A', target)); Add(new(NativeOp.Jump));
            }
            private Item Result(int type, NativeInstruction instruction, int target = -1)
            {
                int result = Register(type); Add(instruction with { Result = result, Type = instruction.Type < 0 ? type : instruction.Type });
                return new(type, target, Register: result);
            }
            private Item As(Item value, int type)
            {
                Demand(value, type);
                if (owner.Accepts(type, value.Type)) return value;
                type = owner.Meet(type, value.Type);
                return value with { Type = type, Register = Result(type, new(NativeOp.Narrow, A: value.Register)).Register };
            }
            private Item PoolConstant(int constant)
            {
                LegacyV6Value recipe = owner.Module.Constants[constant];
                if (!emitting || recipe.Kind is not (LegacyV6ValueKind.Namespace or LegacyV6ValueKind.Range or LegacyV6ValueKind.Function)) return Literal(recipe);
                int declaration = owner.ConstantStatic(constant), layout = owner.Statics[declaration].Layout;
                Item ready = Result(BoolType, new(NativeOp.TestStatic, A: declaration));
                int branch = code.Count; Add(new(NativeOp.Branch, A: ready.Register, C: code.Count + 1));
                Item value = Literal(recipe);
                Item storage = Result(owner.Scope(layout), new(NativeOp.StaticAddress, A: declaration));
                StoreField(storage, layout, 0, value);
                Add(new(NativeOp.PublishStatic, A: declaration));
                code[branch] = code[branch] with { B = code.Count };
                storage = Result(owner.Scope(layout), new(NativeOp.StaticAddress, A: declaration));
                Item address = FieldAddress(storage, layout, 0);
                return value with { Register = Result(value.Type, new(NativeOp.Load, A: address.Register)).Register };
            }
            private static bool ExecutableType(TypeRef type) => (type.Kind & TypeKind.Storage) == 0
                && (type.Element is null || ExecutableType(type.Element));
            private Item Literal(LegacyV6Value value)
            {
                int type = owner.ValueType(value);
                if (value.Kind == LegacyV6ValueKind.Function)
                {
                    Item[] captures = (value.Items ?? []).Select(Literal).ToArray();
                    if (value.NativeName is null)
                        for (int capture = 0; capture < captures.Length; capture++)
                        {
                            LegacyV6Capture contract = owner.Module.Functions[value.Function].Signature.Captures[capture];
                            if (!contract.Location) continue;
                            int layout = LocationLayout(contract.Type);
                            Item allocation = Result(owner.Scope(layout), new(NativeOp.Allocate, A: layout));
                            if (emitting) owner.Allocations.Add(new(code.Count - 1, layout, Function: index));
                            Item address = FieldAddress(allocation, layout, 0);
                            Add(new(NativeOp.Store, A: address.Register, B: captures[capture].Register, C: 0, Type: contract.Type));
                            captures[capture] = address;
                        }
                    int environment = CaptureLayout(captures);
                    bool retired = value.NativeName is { } native && IsStorage(native);
                    int target = retired && emitting ? owner.StorageAdapter(value.NativeName!) : value.NativeName is null ? value.Function : -1;
                    return Result(type, new(NativeOp.MakeFunction, A: target, B: target >= 0 && (retired || value.HasEnvironment) ? environment : -1,
                        C: !retired && value.NativeName is null && !value.HasEnvironment ? 0 : -1,
                        Symbol: retired ? null : value.NativeName, Inputs: captures.Select(item => item.Register).ToArray()), value.Function) with { Environment = environment };
                }
                if (value.Kind == LegacyV6ValueKind.Range)
                    return Result(type, new(NativeOp.MakeRange, C: TypeLiteral(value.Type!.RuntimeType).Register,
                        Inputs: (value.Items ?? []).Select(item => Literal(item).Register).ToArray()));
                if (value.Kind != LegacyV6ValueKind.Namespace) return new(type, Register: Constant(Scalar(value), type), KnownType: value.Kind == LegacyV6ValueKind.Type && ExecutableType(value.Type!.RuntimeType) ? owner.Type(value.Type.RuntimeType) : -1,
                    Descriptor: value.Kind == LegacyV6ValueKind.Type ? value.Type!.RuntimeType : null);
                Item scope = Result(type, new(NativeOp.Allocate, A: value.Layout));
                if (emitting) owner.Allocations.Add(new(code.Count - 1, value.Layout, Function: index));
                for (int field = 0; field < value.Items!.Count; field++)
                    if (value.Layout < owner.Module.Layouts.Count) StoreNamespaceField(scope, value.Layout, field, Literal(value.Items[field]));
                    else StoreField(scope, value.Layout, field, Literal(value.Items[field]));
                return scope;
            }
            private Item FieldAddress(Item scope, int layout, int field) => Result(owner.Pointer(owner.Layouts[layout].Fields[field].Type),
                new(NativeOp.AddressOffset, A: scope.Register, B: layout, C: field));
            private void StoreNamespaceField(Item scope, int layout, int field, Item value)
            {
                int payload = owner.Module.Layouts[layout].Fields[field].Type;
                Demand(value, payload);
                if (!emitting)
                {
                    if (owner.Types[value.Type].Pointee >= 0) owner.IndirectFields.Add((layout, field));
                    if (value.Function >= 0 || value.Environment >= 0)
                    {
                        var key = (layout, field);
                        var fact = (value.Function, value.Environment);
                        if (owner.FieldFacts.TryGetValue(key, out var previous))
                            fact = (previous.Function == fact.Function ? fact.Function : -1,
                                previous.Environment == fact.Environment ? fact.Environment : -1);
                        if (!owner.FieldFacts.TryGetValue(key, out previous) || previous != fact)
                        { owner.FieldFacts[key] = fact; owner.FactsChanged = true; }
                    }
                    return;
                }
                if (!owner.IndirectFields.Contains((layout, field))) { StoreField(scope, layout, field, As(value, payload)); return; }
                if (owner.Types[value.Type].Pointee < 0) value = As(value, payload);
                if (owner.Types[value.Type].Pointee < 0)
                {
                    int cellLayout = LocationLayout(payload);
                    Item allocation = Result(owner.Scope(cellLayout), new(NativeOp.Allocate, A: cellLayout));
                    if (emitting) owner.Allocations.Add(new(code.Count - 1, cellLayout, Function: index));
                    Item address = FieldAddress(allocation, cellLayout, 0);
                    Add(new(NativeOp.Store, A: address.Register, B: value.Register, C: 0, Type: payload));
                    value = address;
                }
                StoreField(scope, layout, field, value);
            }
            private void StoreField(Item scope, int layout, int field, Item value)
            {
                Item address = FieldAddress(scope, layout, field);
                Add(new(NativeOp.Store, A: address.Register, B: value.Register, C: 0, Type: owner.Layouts[layout].Fields[field].Type));
            }
        }
    }
}
