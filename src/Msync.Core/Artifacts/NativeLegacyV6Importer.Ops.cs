namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed partial class FunctionImport
        {
            private IEnumerable<(int Target, State State)> Step(int pc, State state)
            {
                LegacyV6Instruction instruction = function.Instructions[pc]; source = instruction.Source;
                Item Pop()
                {
                    if (state.Stack.Count == 0) throw new MsyncException($"Format-6 stack underflow at {pc} in '{function.Name}'", source);
                    Item item = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1); return item;
                }
                Item[] Take(int count)
                {
                    Item[] result = new Item[count];
                    for (int argument = count - 1; argument >= 0; argument--) result[argument] = Pop();
                    return result;
                }
                void Push(Item item) => state.Stack.Add(item);
                void Nil() => Push(new(NullType, Register: Constant(new(NativeKind.Null), NullType)));
                Item Unary(NativeUnary operation, int type)
                {
                    Item operand = Pop();
                    if (operation is NativeUnary.Not or NativeUnary.Negate) operand = As(operand, type);
                    return Result(type, new(NativeOp.Unary, A: operand.Register, Immediate: (long)operation));
                }
                Item Binary(NativeBinary operation, int type = -1)
                {
                    Item right = Pop(), left = Pop();
                    TypeKind allowed = operation switch
                    {
                        NativeBinary.Subtract or NativeBinary.Multiply or NativeBinary.Divide or NativeBinary.Remainder => TypeKind.Number,
                        NativeBinary.Add => TypeKind.Number | TypeKind.String,
                        NativeBinary.PathJoin or NativeBinary.RegexMatch => TypeKind.String | TypeKind.Path,
                        NativeBinary.Qualify => TypeKind.Any,
                        _ => TypeKind.Bool | TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Type
                    };
                    left = As(left, owner.Type(owner.Types[left.Type].Kind & allowed));
                    right = As(right, owner.Type(owner.Types[right.Type].Kind & allowed));
                    if (type < 0) type = owner.Join(left.Type, right.Type);
                    return Result(type, new(NativeOp.Binary, A: left.Register, B: right.Register, Immediate: (long)operation));
                }
                switch (instruction.Op)
                {
                    case LegacyV6Op.Nop: break;
                    case LegacyV6Op.Constant: Push(PoolConstant(instruction.A)); break;
                    case LegacyV6Op.Null: Nil(); break;
                    case LegacyV6Op.True: case LegacyV6Op.False:
                        Push(new(BoolType, Register: Constant(new(NativeKind.Bool, Boolean: instruction.Op == LegacyV6Op.True), BoolType))); break;
                    case LegacyV6Op.LoadLocal: case LegacyV6Op.LoadStorage:
                    {
                        Item local = state.Locals[instruction.A];
                        if (local.Iterator >= 0) { Push(local with { Register = Constant(new(NativeKind.Null), NullType) }); break; }
                        if (instruction.Op == LegacyV6Op.LoadLocal && locations.ContainsKey(instruction.A) && owner.Types[local.Type].Kind == TypeKind.Null)
                        { Nil(); break; }
                        int type = instruction.Op == LegacyV6Op.LoadStorage && locations.TryGetValue(instruction.A, out int payload)
                            ? owner.Pointer(payload) : local.Type;
                        if (locations.TryGetValue(instruction.A, out int locationType))
                        {
                            Item address = Result(owner.Pointer(locationType), new(NativeOp.FrameAddress, A: instruction.A));
                            if (instruction.Op == LegacyV6Op.LoadStorage) Push(address);
                            else
                            {
                                Item loaded = Result(locationType, new(NativeOp.Load, A: address.Register));
                                Push(local with { Register = As(loaded, type).Register });
                            }
                        }
                        else if (!emitting || slots[instruction.A].Type == type)
                            Push(local with { Register = instruction.A });
                        else Push(local with { Register = Result(type, new(NativeOp.Narrow, A: instruction.A)).Register });
                        break;
                    }
                    case LegacyV6Op.LoadCell:
                        Push(Result(owner.Pointer(instruction.B - 1), new(NativeOp.FrameAddress, A: instruction.A))); break;
                    case LegacyV6Op.StoreLocal: case LegacyV6Op.InitializeLocal:
                    {
                        Item value = Pop();
                        if (instruction.Op == LegacyV6Op.InitializeLocal && owner.Types[value.Type].Pointee >= 0)
                        {
                            int payloadType = owner.Types[value.Type].Pointee;
                            locations[instruction.A] = payloadType;
                            state.Locals[instruction.A] = value with { Type = payloadType, Register = -1 };
                            Add(new(NativeOp.BindLocation, A: instruction.A, B: value.Register));
                            break;
                        }
                        state.Locals[instruction.A] = value with { Register = -1 };
                        if (locations.TryGetValue(instruction.A, out int demanded)) Demand(value, demanded);
                        if (locations.TryGetValue(instruction.A, out int payload))
                        {
                            if (instruction.Op == LegacyV6Op.InitializeLocal)
                            {
                                int layout = LocationLayout(payload);
                                Item allocation = Result(owner.Scope(layout), new(NativeOp.Allocate, A: layout));
                                if (emitting) owner.Allocations.Add(new(code.Count - 1, layout, Function: index));
                                Item address = FieldAddress(allocation, layout, 0);
                                Add(new(NativeOp.BindLocation, A: instruction.A, B: address.Register));
                            }
                            if (owner.Types[value.Type].Kind != TypeKind.Null || (owner.Types[payload].Kind & TypeKind.Null) != 0)
                                Add(new(NativeOp.Copy, instruction.A, value.Register));
                        }
                        else if (value.Iterator < 0) Add(new(NativeOp.Copy, instruction.A, value.Register));
                        break;
                    }
                    case LegacyV6Op.Pop: Pop(); break;
                    case LegacyV6Op.Dup: { Item item = Pop(); Push(item); Push(item); break; }
                    case LegacyV6Op.Jump: yield return (instruction.A, state); yield break;
                    case LegacyV6Op.JumpIfFalse: case LegacyV6Op.JumpIfTrue:
                    {
                        Item condition = As(Pop(), BoolType); Add(new(NativeOp.Branch, A: condition.Register));
                        yield return (instruction.Op == LegacyV6Op.JumpIfTrue ? instruction.A : pc + 1, state);
                        yield return (instruction.Op == LegacyV6Op.JumpIfTrue ? pc + 1 : instruction.A, state); yield break;
                    }
                    case LegacyV6Op.JumpIfStaticReady:
                    {
                        Item ready = Result(BoolType, new(NativeOp.TestStatic, A: instruction.B));
                        Add(new(NativeOp.Branch, A: ready.Register));
                        yield return (instruction.A, state); yield return (pc + 1, state); yield break;
                    }
                    case LegacyV6Op.Return:
                    {
                        Item value = state.Stack.Count == 0 ? new(NullType, Register: Constant(new(NativeKind.Null), NullType)) : Pop();
                        Add(new(NativeOp.Return, A: As(value, function.Signature.Result).Register)); yield break;
                    }
                    case LegacyV6Op.MakeClosure:
                    {
                        Item[] captures = Take(instruction.B);
                        int layout = CaptureLayout(captures);
                        Push(Result(owner.Type(TypeKind.Function), new(NativeOp.MakeFunction, A: instruction.A, B: layout,
                            Inputs: captures.Select(item => item.Register).ToArray()), instruction.A) with { Environment = layout }); break;
                    }
                    case LegacyV6Op.SpecializeClosure:
                    {
                        Item original = Pop();
                        int target = instruction.Text is null ? instruction.A : original.Function >= 0
                            && FunctionDispatch.Targets(instruction.Text).TryGetValue(original.Function, out int selected) ? selected : -1;
                        Push(Result(owner.Type(TypeKind.Function), new(NativeOp.AdaptFunction, A: original.Register,
                            B: instruction.Text is null ? instruction.A : -1, Symbol: instruction.Text), target)
                            with { Environment = original.Environment }); break;
                    }
                    case LegacyV6Op.GetCapture:
                    {
                        Item closure = Pop(); int target = closure.Function;
                        if (target < 0 && closure.Environment < 0)
                        {
                            if (emitting) throw new MsyncException($"Format-6 capture environment cannot be inferred in '{function.Name}' at {pc}", source);
                            int inferred = owner.Type(TypeKind.Any);
                            if (pc + 1 < function.Instructions.Count && function.Instructions[pc + 1] is { Op: LegacyV6Op.InitializeLocal, A: var local }
                                && locations.TryGetValue(local, out int payload)) inferred = owner.Pointer(payload);
                            Push(new(inferred)); break;
                        }
                        int layout = closure.Environment >= 0 ? closure.Environment : EnvironmentLayout(target);
                        Item environment = Result(owner.Scope(layout), new(NativeOp.EnvironmentAddress, A: closure.Register, B: layout));
                        Item address = FieldAddress(environment, layout, instruction.A);
                        int type = owner.Layouts[layout].Fields[instruction.A].Type;
                        Push(Result(type, new(NativeOp.Load, A: address.Register))); break;
                    }
                    case LegacyV6Op.Call: case LegacyV6Op.ProcessCall:
                    {
                        Item[] arguments = Take(instruction.A); Item callee = Pop();
                        TypeKind kind = owner.Types[callee.Type].Kind;
                        bool process = instruction.Op == LegacyV6Op.ProcessCall || (kind & ~(TypeKind.Path | TypeKind.String)) == 0;
                        int target = instruction.Text is null && instruction.B > 0 ? instruction.B - 1 : callee.Function;
                        if (instruction.Text is { } dispatch && target >= 0)
                            target = FunctionDispatch.Targets(dispatch).TryGetValue(target, out int selected) ? selected : -1;
                        if (!process && target >= 0)
                            for (int argument = 0; argument < arguments.Length; argument++)
                                arguments[argument] = As(arguments[argument], owner.Module.Functions[target].Signature.Parameters[argument]);
                        if (!process) callee = As(callee, owner.Type(TypeKind.Function));
                        int type = process ? owner.Type(TypeKind.Process) : target >= 0 ? owner.Module.Functions[target].Signature.Result
                            : DispatchResult(instruction.Text);
                        Push(Result(type, new(process ? NativeOp.ProcessCall : NativeOp.Call, A: callee.Register,
                            B: process ? instruction.B : instruction.Text is null && instruction.B > 0 ? instruction.B - 1 : -1,
                            Symbol: instruction.Text, Inputs: arguments.Select(item => item.Register).ToArray()))); break;
                    }
                    case LegacyV6Op.NativeCall:
                    {
                        Item[] arguments = Take(instruction.A);
                        if (instruction.Text == "_core.iterator_close")
                        { Add(new(NativeOp.CloseIterator, A: arguments[0].Iterator)); Nil(); break; }
                        if (instruction.Text!.StartsWith("storage.", StringComparison.Ordinal) || instruction.Text.StartsWith("arena.", StringComparison.Ordinal)
                            || instruction.Text.StartsWith("slots.", StringComparison.Ordinal))
                        { Push(StorageCall(pc, instruction.Text, arguments)); break; }
                        if (StorageMetadata(instruction.Text, arguments, out Item metadata)) { Push(metadata); break; }
                        if (instruction.Text == "ranges.empty_of")
                        {
                            int element = arguments[0].KnownType >= 0 ? arguments[0].KnownType : owner.Type(TypeKind.Any);
                            Push(Result(owner.Type(TypeKind.Range, element), new(NativeOp.MakeRange, C: arguments[0].Register, Inputs: []))); break;
                        }
                        if (NativeSignatures.TryGet(instruction.Text!, out Msync.Core.NativeSignature? nativeSignature))
                            for (int argument = 0; argument < Math.Min(arguments.Length, nativeSignature.Parameters.Count); argument++)
                                arguments[argument] = As(arguments[argument], owner.Type(nativeSignature.Parameters[argument]));
                        int type = NativeResult(instruction.Text!, arguments);
                        int description = NativeRangeDescription(instruction.Text, arguments, type);
                        Push(Result(type, new(NativeOp.CallNative, C: description, Symbol: instruction.Text,
                            Inputs: arguments.Select(item => item.Register).ToArray()))); break;
                    }
                    case LegacyV6Op.LoadBuiltin:
                    {
                        string name = instruction.Text!.TrimStart('$');
                        if (name == "std") { Push(Literal(owner.BuiltinStd())); break; }
                        ConnectionKind kind = name switch { "local" or "pwd" or "file" => ConnectionKind.Local,
                            "wsl" => ConnectionKind.Wsl, "env" => ConnectionKind.Environment, "path" => ConnectionKind.SearchPath,
                            _ => throw new MsyncException($"Unknown format-6 builtin '${name}'", source) };
                        int type = owner.Type(TypeKind.Connection);
                        Push(new(type, Register: Constant(new(NativeKind.Connection, Text: name is "pwd" or "file" ? name : "", ConnectionKind: kind), type))); break;
                    }
                    case LegacyV6Op.MakeRange:
                    {
                        Item[] items = Take(instruction.A); int element = owner.Type(TypeKind.Never);
                        foreach (Item item in items) element = owner.Join(element, item.Type);
                        Item description = ElementDescription(items);
                        Push(Result(owner.Type(TypeKind.Range, element), new(NativeOp.MakeRange,
                            C: description.Register, Inputs: items.Select(item => item.Register).ToArray()))); break;
                    }
                    case LegacyV6Op.NewScope: case LegacyV6Op.PublishStatic:
                    {
                        int layout = instruction.Op == LegacyV6Op.NewScope ? instruction.A : owner.Module.Statics[instruction.A];
                        Item[] values = Take(owner.Layouts[layout].Fields.Count);
                        Item scope = Result(owner.Scope(layout), new(instruction.Op == LegacyV6Op.NewScope ? NativeOp.Allocate : NativeOp.StaticAddress,
                            A: instruction.Op == LegacyV6Op.NewScope ? layout : instruction.A));
                        if (emitting && instruction.Op == LegacyV6Op.NewScope) owner.Allocations.Add(new(code.Count - 1, layout, Function: index));
                        for (int field = 0; field < values.Length; field++) StoreNamespaceField(scope, layout, field, values[field]);
                        if (instruction.Op == LegacyV6Op.NewScope) Push(scope); else Add(new(NativeOp.PublishStatic, A: instruction.A));
                        break;
                    }
                    case LegacyV6Op.LoadField: case LegacyV6Op.StoreField:
                    {
                        Item? assigned = instruction.Op == LegacyV6Op.StoreField ? Pop() : null;
                        Push(ProjectedField(Pop(), instruction.A, assigned)); break;
                    }
                    case LegacyV6Op.AdmitScope:
                    { Item value = Pop(); Push(Result(instruction.A, new(NativeOp.Narrow, A: value.Register))); break; }
                    case LegacyV6Op.LoadStaticScope:
                        Push(Result(owner.Scope(owner.Module.Statics[instruction.A]), new(NativeOp.StaticAddress, A: instruction.A))); break;
                    case LegacyV6Op.LoadStaticField: case LegacyV6Op.StoreStaticField:
                    {
                        Item? assigned = instruction.Op == LegacyV6Op.StoreStaticField ? Pop() : null;
                        int layout = owner.Module.Statics[instruction.A];
                        Item scope = Result(owner.Scope(layout), new(NativeOp.StaticAddress, A: instruction.A));
                        Item fieldAddress = FieldAddress(scope, layout, instruction.B);
                        int payload = owner.Module.Layouts[layout].Fields[instruction.B].Type;
                        Item address = owner.IndirectFields.Contains((layout, instruction.B))
                            ? Result(owner.Pointer(payload), new(NativeOp.Load, A: fieldAddress.Register)) : fieldAddress;
                        if (assigned is not null) { Add(new(NativeOp.Store, A: address.Register, B: assigned.Register, C: 0, Type: payload)); Nil(); }
                        else Push(Result(payload, new(NativeOp.Load, A: address.Register)));
                        break;
                    }
                    case LegacyV6Op.GetIterator:
                    {
                        Item range = Pop(); int iterator = iteratorIds[pc];
                        Add(new(NativeOp.OpenIterator, A: range.Register, B: iterator));
                        Push(new(NullType, Iterator: iterator, Register: Constant(new(NativeKind.Null), NullType), IteratorElement: owner.Types[range.Type].Element)); break;
                    }
                    case LegacyV6Op.IteratorNext:
                    {
                        Item iterator = state.Locals[instruction.A]; int element = iterator.IteratorElement;
                        if (element < 0) element = owner.Type(TypeKind.Any);
                        State exhausted = state.Copy();
                        Push(Result(element, new(NativeOp.NextIterator, A: iterator.Iterator)));
                        yield return (pc + 1, state); yield return (instruction.B, exhausted); yield break;
                    }
                    case LegacyV6Op.Qualify: Push(Binary(NativeBinary.Qualify, owner.Type(TypeKind.Path))); break;
                    case LegacyV6Op.PathJoin: Push(Binary(NativeBinary.PathJoin, owner.Type(TypeKind.Path))); break;
                    case LegacyV6Op.SelectPath: { Item value = Pop(); Push(Result(owner.Type(TypeKind.Range, owner.Type(TypeKind.Path)), new(NativeOp.SelectPath, A: value.Register, Symbol: instruction.Text))); break; }
                    case LegacyV6Op.Convert: { Item value = Pop(); Push(Result(owner.Type(instruction.Text == "Path" ? TypeKind.Path : TypeKind.String), new(NativeOp.Convert, A: value.Register))); break; }
                    case LegacyV6Op.Transfer: case LegacyV6Op.LocalTransfer:
                    { Item destination = Pop(), from = Pop(); Push(Result(BoolType, new(NativeOp.Transfer, A: from.Register, B: destination.Register))); break; }
                    case LegacyV6Op.Not: Push(Unary(NativeUnary.Not, BoolType)); break;
                    case LegacyV6Op.Negate: Push(Unary(NativeUnary.Negate, NumberType)); break;
                    case LegacyV6Op.IsNull: Push(Unary(NativeUnary.IsNull, BoolType)); break;
                    case LegacyV6Op.IsType: { Item expected = Pop(), actual = Pop(); Push(TypeTest(actual, expected)); break; }
                    case LegacyV6Op.RegexMatch:
                        if (instruction.Text is not null) Push(new(owner.Type(TypeKind.String), Register: Constant(new(NativeKind.String, Text: instruction.Text), owner.Type(TypeKind.String))));
                        Push(Binary(NativeBinary.RegexMatch, BoolType)); break;
                    case LegacyV6Op.Add: Push(Binary(NativeBinary.Add)); break;
                    case LegacyV6Op.Subtract: Push(Binary(NativeBinary.Subtract, NumberType)); break;
                    case LegacyV6Op.Multiply: Push(Binary(NativeBinary.Multiply, NumberType)); break;
                    case LegacyV6Op.Divide: Push(Binary(NativeBinary.Divide, NumberType)); break;
                    case LegacyV6Op.Remainder: Push(Binary(NativeBinary.Remainder, NumberType)); break;
                    case LegacyV6Op.Equal: Push(Binary(NativeBinary.Equal, BoolType)); break;
                    case LegacyV6Op.NotEqual: Push(Binary(NativeBinary.NotEqual, BoolType)); break;
                    case LegacyV6Op.Less: Push(Binary(NativeBinary.Less, BoolType)); break;
                    case LegacyV6Op.LessEqual: Push(Binary(NativeBinary.LessEqual, BoolType)); break;
                    case LegacyV6Op.Greater: Push(Binary(NativeBinary.Greater, BoolType)); break;
                    case LegacyV6Op.GreaterEqual: Push(Binary(NativeBinary.GreaterEqual, BoolType)); break;
                    case LegacyV6Op.Barrier: Add(new(NativeOp.Barrier, A: instruction.A, B: instruction.B, Symbol: instruction.Text)); break;
                    case LegacyV6Op.Acquire:
                    { Item? process = instruction.A == 0 ? null : Pop(); Add(new(NativeOp.Acquire, process?.Register ?? -1, process?.Register ?? -1, instruction.B, Immediate: instruction.A, Symbol: instruction.Text)); if (process is not null) Push(process); break; }
                    case LegacyV6Op.ExecutePlan: Take(instruction.B); Add(new(NativeOp.ExecutePlan, A: instruction.A)); break;
                    case LegacyV6Op.PlanResult: Take(instruction.B); Push(Result(BoolType, new(NativeOp.PlanResult, A: instruction.A))); break;
                    default: throw new MsyncException($"Format-6 opcode {instruction.Op} has no native relocation", source);
                }
                yield return (pc + 1, state);
            }

            private int CaptureLayout(Item[] captures)
            {
                NativeCodeField[] fields = captures.Select((capture, ordinal) => new NativeCodeField(
                    ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), capture.Type)).ToArray();
                for (int layout = owner.Module.Layouts.Count; layout < owner.Layouts.Count; layout++)
                    if (owner.Layouts[layout].Fields.SequenceEqual(fields)) return layout;
                owner.Layouts.Add(new(fields)); return owner.Layouts.Count - 1;
            }
            private readonly Dictionary<int, int> locationLayouts = [];
            private int LocationLayout(int payload)
            {
                if (locationLayouts.TryGetValue(payload, out int layout)) return layout;
                layout = owner.Layouts.Count; owner.Layouts.Add(new([new("value", payload)]));
                locationLayouts.Add(payload, layout); return layout;
            }
            private readonly Dictionary<int, int> environmentLayouts = [];
            private int EnvironmentLayout(int target)
            {
                if (environmentLayouts.TryGetValue(target, out int layout)) return layout;
                layout = owner.Layouts.Count;
                owner.Layouts.Add(new(owner.Module.Functions[target].Signature.Captures.Select((capture, ordinal) =>
                    new NativeCodeField(ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture), capture.Location ? owner.Pointer(capture.Type) : capture.Type)).ToArray()));
                environmentLayouts[target] = layout; return layout;
            }
            private int DispatchResult(string? dispatch)
            {
                if (dispatch is null) return owner.Type(TypeKind.Any);
                int result = owner.Type(TypeKind.Never);
                foreach (int target in FunctionDispatch.Targets(dispatch).Values) result = owner.Join(result, owner.Module.Functions[target].Signature.Result);
                return result;
            }
            private int Structural(params (string Name, int Type)[] fields)
            {
                int[] layouts = owner.Module.Layouts.Select((layout, index) => (layout, index))
                    .Where(item => item.layout.Fields.Count == fields.Length && fields.All(field =>
                        item.layout.Fields.Any(candidate => candidate.Name == field.Name
                            && (owner.Types[candidate.Type].Kind & owner.Types[field.Type].Kind) != 0)))
                    .Select(item => item.index).ToArray();
                if (layouts.Length != 0) return owner.Type(TypeKind.Scope, layouts: layouts);
                NativeCodeField[] shape = fields.Select(field => new NativeCodeField(field.Name, field.Type)).ToArray();
                for (int layout = owner.Module.Layouts.Count; layout < owner.Layouts.Count; layout++)
                    if (owner.Layouts[layout].Fields.SequenceEqual(shape)) { owner.hostLayouts.Add(layout); return owner.Scope(layout); }
                int created = owner.Layouts.Count; owner.Layouts.Add(new(shape)); owner.hostLayouts.Add(created); return owner.Scope(created);
            }
            private int NativeResult(string name, Item[] arguments)
            {
                if (!NativeSignatures.TryGet(name, out Msync.Core.NativeSignature? signature))
                    throw new MsyncException($"Unknown format-6 native function '{name}'", source);
                if (owner.hostResults.TryGetValue(name, out int structural)) return structural;
                int first = arguments.Length == 0 ? owner.Type(TypeKind.Never) : arguments[0].Type;
                int element = owner.Types[first].Element;
                int ItemType(int type) => owner.Types[type].Element is int item && item >= 0 ? item : owner.Type(TypeKind.Any);
                int Range(int item) => owner.Type(TypeKind.Range, item);
                int Callback(int argument) => arguments.Length > argument && arguments[argument].Function >= 0
                    ? owner.Module.Functions[arguments[argument].Function].Signature.Result : owner.Type(TypeKind.Any);
                return signature.ResultRule switch
                {
                    NativeResultRule.FirstArgument or NativeResultRule.RangeMergeLeft => first,
                    NativeResultRule.CastTarget when arguments.Length > 1 && arguments[1].KnownType >= 0 => arguments[1].KnownType,
                    NativeResultRule.RangeItem => element < 0 ? owner.Type(signature.Result) : owner.Join(element, NullType),
                    NativeResultRule.RangeOfArguments => Range(arguments.Select(item => item.Type).Aggregate(owner.Type(TypeKind.Never), owner.Join)),
                    NativeResultRule.Transform => Range(Callback(1)),
                    NativeResultRule.RangeConcat or NativeResultRule.RangeMerge => Range(owner.Join(ItemType(first), ItemType(arguments[1].Type))),
                    NativeResultRule.RangeFlatten => Range(ItemType(ItemType(first))),
                    NativeResultRule.RangeNested or NativeResultRule.RangeRepeat => Range(first),
                    NativeResultRule.RangeOfType when arguments[0].KnownType >= 0 => Range(arguments[0].KnownType),
                    NativeResultRule.RangeScan => Range(owner.Join(arguments[1].Type, Callback(2))),
                    NativeResultRule.RangeEnumerate => Range(Structural(("index", NumberType), ("value", ItemType(first)))),
                    NativeResultRule.RangeZip => Range(Structural(("first", ItemType(first)), ("second", ItemType(arguments[1].Type)))),
                    _ => owner.Type(signature.Result)
                };
            }
        }
    }
}
