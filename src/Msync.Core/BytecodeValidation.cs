namespace Msync.Core;

internal static class BytecodeValidation
{
    private readonly record struct Item(TypeRef Type, int? Function = null, bool Iterator = false);
    private sealed record State(Item[] Stack, Item?[] Locals);
    internal static void Validate(Module module)
    {
        foreach (var function in module.Functions) ValidateFunction(module, function);
    }
    private static void ValidateFunction(Module module, FunctionCode function)
    {
        var code = function.Instructions;
        if (code.Count == 0) throw new MsyncException($"Function '{function.Name}' has no bytecode body");
        var initial = new Item?[function.LocalCount];
        for (var i = 0; i < function.CaptureCount; i++) initial[i] = new(TypeRef.Any);
        for (var i = 0; i < function.ParameterCount; i++) initial[function.CaptureCount + i] = new(function.ParameterTypes is { } types && i < types.Count ? types[i] : TypeRef.Any);
        var states = new State?[code.Count + 1];
        var work = new Queue<int>();
        states[0] = new([], initial); work.Enqueue(0);
        while (work.TryDequeue(out var pc))
        {
            if (pc == code.Count) continue;
            var instruction = code[pc]; var state = states[pc]!;
            var stack = state.Stack.ToList(); var locals = state.Locals.ToArray();
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
            void Push(TypeRef type) => stack.Add(new(type));
            bool terminal = false;
            List<int> next = [];
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Constant:
                    var constant = module.Constants[instruction.A]; stack.Add(new(constant.Type, constant is FunctionValue { NativeName: null } f ? f.Function : null)); break;
                case OpCode.Null: Push(TypeRef.Null); break;
                case OpCode.True: case OpCode.False: Push(TypeRef.Bool); break;
                case OpCode.LoadLocal:
                    stack.Add(locals[instruction.A] ?? throw Error($"local {instruction.A} is not defined on every incoming path")); break;
                case OpCode.StoreLocal: locals[instruction.A] = Pop(); break;
                case OpCode.Pop: Pop(); break;
                case OpCode.Dup:
                    var duplicate = Pop(); stack.Add(duplicate); stack.Add(duplicate); break;
                case OpCode.Jump: next.Add(instruction.A); terminal = true; break;
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                    Require(Pop(), TypeKind.Bool); next.Add(instruction.A); break;
                case OpCode.Return:
                    if (stack.Count > 1) throw Error($"return leaves {stack.Count} values on the operand stack");
                    terminal = true; break;
                case OpCode.MakeClosure:
                    if (instruction.B != module.Functions[instruction.A].CaptureCount) throw Error("closure capture layout disagrees with function");
                    PopMany(instruction.B); stack.Add(new(TypeRef.Function, instruction.A)); break;
                case OpCode.SpecializeClosure:
                    Require(Pop(), TypeKind.Function); stack.Add(new(TypeRef.Function, instruction.A)); break;
                case OpCode.Call:
                case OpCode.ProcessCall:
                    {
                        PopMany(instruction.A); var callee = Pop(); Require(callee, TypeKind.Function | TypeKind.Path | TypeKind.String);
                        var target = instruction.B == 0 ? callee.Function : instruction.B - 1;
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
                    if (instruction.Text is null || !NativeSignatures.TryGet(instruction.Text, out var signature)) throw Error($"native '{instruction.Text}' is not registered");
                    var arguments = PopMany(instruction.A);
                    if (!signature.Variadic && arguments.Length != signature.Parameters.Count || signature.Variadic && arguments.Length < signature.Parameters.Count)
                        throw Error("native argument count disagrees with registry signature");
                    for (var i = 0; i < arguments.Length; i++) Require(arguments[i], signature.Parameters[Math.Min(i, signature.Parameters.Count - 1)].Kind);
                    Push(signature.Result); break;
                case OpCode.LoadBuiltin: Push(instruction.Text?.TrimStart('$') is "std" or "view" or "views" or "ranges" ? TypeRef.Scope : TypeRef.Connection); break;
                case OpCode.MakeRange: PopMany(instruction.A); Push(new(TypeKind.Range)); break;
                case OpCode.MakeLazyRange:
                    var items = PopMany(instruction.A);
                    if (instruction.Text is { } mask && (mask.Length != items.Length || mask.Any(c => c is not ('0' or '1')))) throw Error("lazy range mask disagrees with item table");
                    for (var i = 0; i < items.Length; i++) if (instruction.Text is null || instruction.Text[i] == '1') Require(items[i], TypeKind.Function);
                    if (instruction.B != 0 && (instruction.B < 0 || instruction.B > module.Constants.Count || module.Constants[instruction.B - 1] is not TypeValue)) throw Error("lazy range item type does not identify a Type constant");
                    Push(new(TypeKind.Range)); break;
                case OpCode.DeferRange: Require(Pop(), TypeKind.Function); Push(new(TypeKind.Range)); break;
                case OpCode.MakeScope:
                    if ((instruction.Text is null ? 0 : instruction.Text.Split('\0').Length) != instruction.A) throw Error("scope member names disagree with value count");
                    PopMany(instruction.A); Push(TypeRef.Scope); break;
                case OpCode.GetMember: Require(Pop(), TypeKind.Scope | TypeKind.Connection | TypeKind.Process); Push(TypeRef.Any); break;
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
                case OpCode.Not: Require(Pop(), TypeKind.Bool); Push(TypeRef.Bool); break;
                case OpCode.IsNull: Pop(); Push(TypeRef.Bool); break;
                case OpCode.IsType: Require(Pop(), TypeKind.Type); Pop(); Push(TypeRef.Bool); break;
                case OpCode.RegexMatch:
                    if (instruction.Text is null) Require(Pop(), TypeKind.String | TypeKind.Path);
                    Require(Pop(), TypeKind.String | TypeKind.Path); Push(TypeRef.Bool); break;
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
                    Require(Pop(), TypeKind.Number); Require(Pop(), TypeKind.Number); Push(TypeRef.Number); break;
                case OpCode.Negate: Require(Pop(), TypeKind.Number); Push(TypeRef.Number); break;
                case OpCode.GetIterator: Require(Pop(), TypeKind.Range); stack.Add(new(new(TypeKind.Range), Iterator: true)); break;
                case OpCode.IteratorNext:
                    if (locals[instruction.A] is not { Iterator: true }) throw Error("iteration local does not contain an iterator");
                    Merge(instruction.B, stack, locals); Push(TypeRef.Any); break;
                default: throw Error("operation has no executable definition");
            }
            if (!terminal) next.Add(pc + 1);
            foreach (var successor in next) Merge(successor, stack, locals);
        }
        void Merge(int target, List<Item> stack, Item?[] locals)
        {
            var previous = states[target];
            if (previous is null) { states[target] = new(stack.ToArray(), locals.ToArray()); work.Enqueue(target); return; }
            if (previous.Stack.Length != stack.Count) throw new MsyncException($"Invalid bytecode in '{function.Name}': control-flow stack heights disagree at instruction {target}");
            var mergedStack = new Item[stack.Count]; var mergedLocals = new Item?[locals.Length]; var changed = false;
            Item Join(Item left, Item right)
            {
                if (left.Iterator != right.Iterator) throw new MsyncException($"Invalid iterator ownership at bytecode join in '{function.Name}'");
                return new(TypeRef.Union(left.Type, right.Type), left.Function == right.Function ? left.Function : null, left.Iterator);
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
