namespace Msync.Core;

internal static class BytecodeValidation
{
    private readonly record struct Item(TypeRef Type, int? Function = null, bool Iterator = false, Value? Constant = null);
    private sealed record State(Item[] Stack, Item?[] Locals);

    internal static void Validate(Module module)
    {
        foreach (var function in module.Functions) new FunctionValidator(module, function).Validate();
    }

    private sealed class FunctionValidator(Module module, FunctionCode function)
    {
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly State?[] states = new State?[function.Instructions.Count + 1];
        private readonly Queue<int> work = new();
        private bool[] starts = [];
        private readonly HashSet<int> cells = function.Instructions.Where(instruction => instruction.Op == OpCode.LoadCell)
            .Select(instruction => instruction.A).ToHashSet();

        internal void Validate()
        {
            if (code.Count == 0) throw new MsyncException($"Function '{function.Name}' has no bytecode body");
            starts = BlockStarts(code);
            var initial = new Item?[function.LocalCount];
            for (var i = 0; i < function.CaptureCount; i++) initial[i] = new(TypeRef.Any);
            for (var i = 0; i < function.ParameterCount; i++) initial[function.CaptureCount + i] = new(function.ParameterTypes is { } types && i < types.Count ? types[i] : TypeRef.Any);
            states[0] = new([], initial);
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
            for (var pc = start; pc < code.Count; pc++)
            {
                if (!Step(pc, stack, locals)) return;
                if (!starts[pc + 1]) continue;
                Merge(pc + 1, stack, locals);
                return;
            }
        }

        private static bool[] BlockStarts(IReadOnlyList<Instruction> instructions)
        {
            var result = new bool[instructions.Count + 1];
            result[0] = result[^1] = true;
            for (var pc = 0; pc < instructions.Count; pc++)
            {
                var instruction = instructions[pc];
                if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue)
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

        private bool Step(int pc, List<Item> stack, Item?[] locals)
        {
            var instruction = code[pc];
            MsyncException Error(string message) => new($"Invalid bytecode in '{function.Name}' at instruction {pc} ({instruction.Op}): {message}", instruction.Span);
            Item Pop()
            {
                if (stack.Count == 0) throw Error("operand stack underflow");
                var item = stack[^1]; stack.RemoveAt(stack.Count - 1); return item;
            }
            Item[] PopMany(int count)
            {
                if (count < 0 || count > stack.Count) throw Error($"operation requires {count} stack operands, has {stack.Count}");
                var values = new Item[count]; for (var i = count - 1; i >= 0; i--) values[i] = Pop(); return values;
            }
            void Require(Item item, TypeKind kinds)
            {
                if (item.Iterator || (item.Type.Kind & kinds) == TypeKind.Never)
                    throw Error($"expected {new TypeRef(kinds)}, found {(item.Iterator ? "iterator" : item.Type)}");
            }
            void Push(TypeRef type, Value? constant = null) => stack.Add(new(type, Constant: constant));
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Barrier: break;
                case OpCode.Acquire:
                    if (instruction.A != 0) { var process = Pop(); Require(process, TypeKind.Process); stack.Add(process); }
                    break;
                case OpCode.Constant:
                    var constant = module.Constants[instruction.A]; stack.Add(new(constant.Type, constant is FunctionValue { NativeName: null } f ? f.Function : null, Constant: constant)); break;
                case OpCode.Null: Push(TypeRef.Null, Value.Null); break;
                case OpCode.True: Push(TypeRef.Bool, Value.True); break;
                case OpCode.False: Push(TypeRef.Bool, Value.False); break;
                case OpCode.LoadLocal:
                case OpCode.LoadCell:
                case OpCode.LoadStorage:
                    var local = locals[instruction.A] ?? throw Error($"local {instruction.A} is not defined on every incoming path");
                    // An escaped location can be written through closure aliases,
                    // including aliases introduced when a callee is inlined.
                    stack.Add(cells.Contains(instruction.A) ? local with { Type = TypeRef.Any, Constant = null, Function = null } : local); break;
                case OpCode.StoreLocal:
                case OpCode.InitializeLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup:
                    var duplicate = Pop(); stack.Add(duplicate); stack.Add(duplicate); break;
                case OpCode.Jump: Merge(instruction.A, stack, locals); return false;
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                    var condition = Pop(); Require(condition, TypeKind.Bool);
                    if (condition.Constant is BoolValue known)
                    {
                        if (known.Data == (instruction.Op == OpCode.JumpIfTrue)) { Merge(instruction.A, stack, locals); return false; }
                    }
                    else Merge(instruction.A, stack, locals);
                    break;
                case OpCode.Return:
                    if (stack.Count > 1) throw Error($"return leaves {stack.Count} values on the operand stack");
                    return false;
                case OpCode.MakeClosure:
                    if (instruction.B != module.Functions[instruction.A].CaptureCount) throw Error("closure capture layout disagrees with function");
                    PopMany(instruction.B); stack.Add(new(TypeRef.Function, instruction.A)); break;
                case OpCode.SpecializeClosure:
                    Require(Pop(), TypeKind.Function); stack.Add(new(TypeRef.Function, instruction.Text is null ? instruction.A : null)); break;
                case OpCode.Call:
                case OpCode.ProcessCall:
                    {
                        PopMany(instruction.A); var callee = Pop(); Require(callee, TypeKind.Function | TypeKind.Path | TypeKind.String);
                        var target = instruction.Op == OpCode.ProcessCall ? null : instruction.B == 0 ? callee.Function : instruction.B - 1;
                        if (instruction.Op == OpCode.Call && instruction.Text is { } dispatch)
                            target = callee.Function is { } identity && FunctionDispatch.Parse(dispatch).TryGetValue(identity, out int selected) ? selected : null;
                        if (target is { } index)
                        {
                            if (index < 0 || index >= module.Functions.Count) throw Error($"callee {index} does not identify a function");
                            if (module.Functions[index].ParameterCount != instruction.A) throw Error("call argument count disagrees with function");
                            Push(module.Functions[index].ReturnType);
                        }
                        else Push(instruction.Op == OpCode.ProcessCall ? TypeRef.Process : TypeRef.Any);
                        break;
                    }
                case OpCode.NativeCall:
                    if (instruction.Text == "_core.iterator_close")
                    {
                        if (instruction.A != 1 || !Pop().Iterator) throw Error("iterator cleanup requires one loop iterator");
                        Push(TypeRef.Null); break;
                    }
                    if (instruction.Text is null || !NativeSignatures.TryGet(instruction.Text, out var signature)) throw Error($"native '{instruction.Text}' is not registered");
                    var arguments = PopMany(instruction.A);
                    if (!signature.Variadic && arguments.Length != signature.Parameters.Count || signature.Variadic && arguments.Length < signature.Parameters.Count)
                        throw Error("native argument count disagrees with registry signature");
                    for (var i = 0; i < arguments.Length; i++) Require(arguments[i], signature.Parameters[Math.Min(i, signature.Parameters.Count - 1)].Kind);
                    Push(signature.Result); break;
                case OpCode.LoadBuiltin: Push(instruction.Text?.TrimStart('$') is "std" ? TypeRef.Scope : TypeRef.Connection); break;
                case OpCode.MakeRange: PopMany(instruction.A); Push(new(TypeKind.Range)); break;
                case OpCode.MakeLazyRange:
                    var items = PopMany(instruction.A);
                    if (instruction.Text is { } mask && (mask.Length != items.Length || mask.Any(c => c is not ('0' or '1')))) throw Error("lazy range mask disagrees with item table");
                    for (var i = 0; i < items.Length; i++) if (instruction.Text is null || instruction.Text[i] == '1') Require(items[i], TypeKind.Function);
                    if (instruction.B != 0 && (instruction.B < 0 || instruction.B > module.Constants.Count || module.Constants[instruction.B - 1] is not TypeValue)) throw Error("lazy range item type does not identify a Type constant");
                    Push(new(TypeKind.Range)); break;
                case OpCode.DeferRange: Require(Pop(), TypeKind.Function); Push(new(TypeKind.Range)); break;
                case OpCode.MakeScope:
                    if ((instruction.Text is null ? 0 : instruction.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length) != instruction.A) throw Error("scope member names disagree with value count");
                    PopMany(instruction.A); Push(TypeRef.Scope); break;
                case OpCode.GetSingleton: Push(new(TypeKind.Scope | TypeKind.Null)); break;
                case OpCode.SetSingleton: Require(Pop(), TypeKind.Scope); Push(TypeRef.Scope); break;
                case OpCode.GetMember: Require(Pop(), TypeKind.Scope | TypeKind.Connection | TypeKind.Process); Push(TypeRef.Any); break;
                case OpCode.SetMember: Pop(); Require(Pop(), TypeKind.Scope); Push(TypeRef.Null); break;
                case OpCode.GetCapture: Require(Pop(), TypeKind.Function); Push(TypeRef.Any); break;
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
                case OpCode.GetIterator: Require(Pop(), TypeKind.Range); stack.Add(new(new(TypeKind.Range), Iterator: true)); break;
                case OpCode.IteratorNext:
                    if (locals[instruction.A] is not { Iterator: true }) throw Error("iteration local does not contain an iterator");
                    Merge(instruction.B, stack, locals); Push(TypeRef.Any); break;
                default: throw Error("operation has no executable definition");
            }
            return true;
        }
        private void Merge(int target, List<Item> stack, Item?[] locals)
        {
            var previous = states[target];
            if (previous is null) { states[target] = new(stack.ToArray(), locals.ToArray()); work.Enqueue(target); return; }
            if (previous.Stack.Length != stack.Count) throw new MsyncException($"Invalid bytecode in '{function.Name}': control-flow stack heights disagree at instruction {target}");
            var mergedStack = new Item[stack.Count]; var mergedLocals = new Item?[locals.Length]; var changed = false;
            Item Join(Item left, Item right)
            {
                if (left.Iterator != right.Iterator) throw new MsyncException($"Invalid iterator ownership at bytecode join in '{function.Name}'");
                return new(TypeRef.Union(left.Type, right.Type), left.Function == right.Function ? left.Function : null, left.Iterator, ValueRepresentation.Same(left.Constant, right.Constant) ? left.Constant : null);
            }
            for (var i = 0; i < stack.Count; i++) { mergedStack[i] = Join(previous.Stack[i], stack[i]); changed |= mergedStack[i] != previous.Stack[i]; }
            for (var i = 0; i < locals.Length; i++)
            {
                mergedLocals[i] = previous.Locals[i] is { } left && locals[i] is { } right ? Join(left, right) : null;
                changed |= mergedLocals[i] != previous.Locals[i];
            }
            if (changed) { states[target] = new(mergedStack, mergedLocals); work.Enqueue(target); }
        }
    }
}
