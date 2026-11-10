using Msync.Core;

namespace Msync.Runtime;

internal sealed class KnownLoop(Module module, IReadOnlyList<Instruction> code, IReadOnlyList<Value> constants,
    IReadOnlyList<KnownValue> inputs, RuntimeContext context, PlanFileSystem filesystem,
    Func<Value, Value?> resolve, Func<ScopeValue, string, Value?> member,
    int body, int header, int exit, int iterator)
{
    internal sealed record Copy(Value Source, PathValue Destination, SourceSpan? Span, int Instruction);
    internal sealed record Result(KnownValue[] Locals, IReadOnlyDictionary<int, bool> Stores, IReadOnlyList<Copy> Copies);
    private readonly KnownValue[] locals = inputs.ToArray();
    private readonly Dictionary<int, bool> stores = [];
    private readonly List<Copy> copies = [];
    private readonly List<Value?> stack = [];
    private bool stopped;
    private CancellationToken cancellation;

    internal Result? Run(IReadOnlyList<Value> values, CancellationToken cancellationToken)
    {
        cancellation = cancellationToken;
        locals[iterator] = new(new RangeValue(values, TypeRef.Path));
        foreach (Value value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stack.Clear(); stack.Add(value);
            int cursor = body;
            while (cursor < exit && !stopped)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor == exit - 1 && code[cursor] is { Op: OpCode.Jump, A: var target } && target == header) break;
                if (!Step(ref cursor)) return null;
                cursor++;
            }
            if (stack.Count != 0) return null;
            if (stopped) break;
        }
        return stores.Keys.Any(slot => locals[slot].Value is not { } value || !KnownEvaluation.Portable(value))
            ? null : new(locals, stores, copies);
    }

    private bool Step(ref int pc)
    {
        Instruction instruction = code[pc];
        switch (instruction.Op)
        {
            case OpCode.Nop: return true;
            case OpCode.Constant: stack.Add(constants[instruction.A]); return true;
            case OpCode.Null: stack.Add(Value.Null); return true;
            case OpCode.True: stack.Add(Value.True); return true;
            case OpCode.False: stack.Add(Value.False); return true;
            case OpCode.LoadLocal:
            case OpCode.LoadStorage: stack.Add(locals[instruction.A].Value); return true;
            case OpCode.InitializeLocal:
            case OpCode.StoreLocal:
                locals[instruction.A] = new(Pop());
                stores[instruction.A] = instruction.Op == OpCode.InitializeLocal;
                return locals[instruction.A].Known;
            case OpCode.Pop: Pop(); return true;
            case OpCode.Dup:
                if (stack.Count == 0) return false;
                stack.Add(stack[^1]); return true;
            case OpCode.Convert:
                return Push(Pop() is { } converted ? KnownEvaluation.Convert(converted, instruction.Text, context) : null);
            case OpCode.Qualify:
                {
                    Value? value = Pop(), connection = Pop();
                    return Push(connection is ConnectionValue remote && value is not null
                        ? KnownEvaluation.Qualify(remote, value, context) : null);
                }
            case OpCode.PathJoin:
                {
                    Value? right = Pop(), left = Pop();
                    return Push(left is PathValue or StringValue && right is PathValue or StringValue
                        ? PathSemantics.JoinValue(context.AsPath(left), right) : null);
                }
            case OpCode.GetMember:
                {
                    Value? value = Pop() is ScopeValue scope ? member(scope, instruction.Text!) : null;
                    return Push(value is CellValue cell ? cell.Data : value);
                }
            case OpCode.MakeRange:
                {
                    Value?[] values = PopMany(instruction.A);
                    return Push(values.All(value => value is not null)
                        ? new RangeValue(values.Select(value => value!).ToArray(),
                            values.Aggregate(new TypeRef(TypeKind.Never), (type, value) => TypeRef.Union(type, value!.Type))) : null);
                }
            case OpCode.NativeCall:
                {
                    Value?[] arguments = PopMany(instruction.A);
                    if (instruction.Text == "_core.iterator_close") return Push(Value.Null);
                    if (arguments.Any(value => value is null) || !NativeSignatures.TryGet(instruction.Text!, out var signature)
                        || signature.Effect != NativeEffect.Pure
                        || (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.Enumerates | NativeBehavior.LocalMemory)) != 0)
                        return false;
                    try { return Push(filesystem.Invoke(instruction.Text!, arguments.Select(value => value!).ToArray())); }
                    catch (Exception error) when (error is MsyncException or ArgumentException or InvalidOperationException
                        or FormatException or OverflowException)
                    { return false; }
                }
            case OpCode.Call:
                {
                    Value?[] arguments = PopMany(instruction.A);
                    if (Pop() is not FunctionValue function || arguments.Any(argument => argument is null)
                        || instruction.B < 0) return false;
                    int? target = KnownCalls.Target(function, instruction);
                    if (function.NativeName is null && instruction.Text is not null && target is null) return false;
                    var result = new KnownCalls(module, context, filesystem, resolve, member)
                        .Run(function, arguments.Select(argument => argument!).ToArray(), target, cancellation);
                    if (result is null || result.Value is KnownCopyResult
                        && (pc + 1 >= exit || code[pc + 1].Op != OpCode.Pop)) return false;
                    foreach (var copy in result.Copies)
                        copies.Add(new(copy.Source, copy.Destination, copy.Span, pc));
                    return Push(result.Value);
                }
            case OpCode.Transfer:
            case OpCode.LocalTransfer:
                {
                    Value? destination = Pop(), source = Pop();
                    if (source is not (PathValue or StringValue or RangeValue { SnapshotValues: not null })
                        || destination is not (PathValue or StringValue) || pc + 1 >= exit || code[pc + 1].Op != OpCode.Pop)
                        return false;
                    copies.Add(new(source, context.AsPath(destination), instruction.Span, pc));
                    stack.Add(Value.True);
                    return true;
                }
            case OpCode.Jump:
                if (instruction.A == exit) { stopped = true; return true; }
                if (instruction.A == header) { pc = exit - 1; return true; }
                if (instruction.A <= pc || instruction.A >= exit) return false;
                pc = instruction.A - 1; return true;
            case OpCode.JumpIfFalse:
            case OpCode.JumpIfTrue:
                {
                    if (Pop() is not BoolValue condition) return false;
                    if (condition.Data != (instruction.Op == OpCode.JumpIfTrue)) return true;
                    if (instruction.A == exit) { stopped = true; return true; }
                    if (instruction.A == header) { pc = exit - 1; return true; }
                    if (instruction.A <= pc || instruction.A >= exit) return false;
                    pc = instruction.A - 1; return true;
                }
            case OpCode.Not:
            case OpCode.IsNull:
            case OpCode.Negate:
                return Push(Pop() is { } unary ? KnownEvaluation.Unary(instruction.Op, unary) : null);
            case OpCode.Equal:
            case OpCode.NotEqual:
            case OpCode.Less:
            case OpCode.LessEqual:
            case OpCode.Greater:
            case OpCode.GreaterEqual:
            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:
            case OpCode.Remainder:
            case OpCode.IsType:
                {
                    Value? right = Pop(), left = Pop();
                    return Push(left is not null && right is not null ? KnownEvaluation.Binary(instruction.Op, left, right) : null);
                }
            case OpCode.RegexMatch:
                {
                    Value? pattern = instruction.Text is null ? Pop() : new StringValue(instruction.Text);
                    Value? value = Pop();
                    return Push(value is not null && pattern is StringValue expression
                        ? KnownEvaluation.Regex(value, expression.Data) : null);
                }
            case OpCode.Barrier when instruction.B == 0 && instruction.Text is null:
            case OpCode.Acquire when instruction.A == 0: return true;
            default: return false;
        }
    }

    private bool Push(Value? value)
    {
        if (value is null) return false;
        stack.Add(value); return true;
    }
    private Value? Pop()
    {
        if (stack.Count == 0) return null;
        Value? value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value;
    }
    private Value?[] PopMany(int count)
    {
        Value?[] values = new Value?[count];
        for (int index = count - 1; index >= 0; index--) values[index] = Pop();
        return values;
    }
}
