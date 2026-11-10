using Msync.Core;

namespace Msync.Runtime;

// The result is a dependency on a copy, not an assumed successful Bool.
internal sealed record KnownCopyResult(int Copy) : Value
{
    public override TypeRef Type => TypeRef.Bool;
}

internal sealed class KnownCalls(Module module, RuntimeContext context, PlanFileSystem filesystem,
    Func<Value, Value?> resolve, Func<ScopeValue, string, Value?> member,
    Func<string?, Value?>? builtin = null)
{
    private readonly Module image = module;
    private readonly RuntimeContext runtime = context;
    private readonly PlanFileSystem files = filesystem;
    private readonly Func<Value, Value?> resolver = resolve;
    private readonly Func<ScopeValue, string, Value?> members = member;
    private readonly Func<string?, Value?>? builtins = builtin;
    internal sealed record Copy(Value Source, PathValue Destination, SourceSpan? Span);
    internal sealed record Result(Value Value, IReadOnlyList<Copy> Copies);
    private sealed record Iterator(IReadOnlyList<Value> Values) : Value
    {
        internal int Position;
        public override TypeRef Type => new(TypeKind.Range);
    }

    private readonly HashSet<int> active = [];
    private readonly List<Copy> copies = [];

    internal Result? Run(FunctionValue function, IReadOnlyList<Value> arguments, int? target,
        CancellationToken cancellationToken)
    {
        Value? value = Invoke(function, arguments, target, cancellationToken);
        return value is null ? null : new(value, copies);
    }

    private Value? Invoke(FunctionValue closure, IReadOnlyList<Value> arguments, int? selected,
        CancellationToken cancellationToken)
    {
        if (closure.NativeName is { } native) return Native(native, arguments);
        int target = selected ?? closure.Function;
        if (!active.Add(target)) return null;
        try
        {
            var function = image.Functions[target];
            if (arguments.Count != function.ParameterCount) return null;
            Value?[] locals = new Value?[function.LocalCount];
            if (closure.Captures is { } captures)
                for (int index = 0; index < captures.Count; index++) locals[index] = resolver(captures[index]);
            for (int index = 0; index < arguments.Count; index++)
                locals[function.CaptureCount + index] = resolver(arguments[index]);
            return new Frame(this, function, locals, cancellationToken).Run();
        }
        finally { active.Remove(target); }
    }

    private Value? Native(string name, IReadOnlyList<Value> arguments)
    {
        if (arguments.Any(value => value is KnownCopyResult)
            || !NativeSignatures.TryGet(name, out var signature) || signature.Effect != NativeEffect.Pure
            || (signature.Behavior.GetValueOrDefault() & (NativeBehavior.Invokes | NativeBehavior.LocalMemory)) != 0)
            return null;
        if (!NativeEvaluation.CanEnumerate(signature, arguments)) return null;
        try { return files.Invoke(name, arguments); }
        catch (Exception error) when (error is MsyncException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException)
        { return null; }
    }

    internal static int? Target(FunctionValue function, Instruction instruction)
    {
        if (function.NativeName is not null) return null;
        if (instruction.B < 0) return null;
        if (instruction.Text is not null)
            return FunctionDispatch.Parse(instruction.Text).TryGetValue(function.Function, out int target) ? target : null;
        return instruction.B > 0 ? instruction.B - 1 : function.Function;
    }

    private sealed class Frame(KnownCalls owner, FunctionCode function, Value?[] locals,
        CancellationToken cancellationToken)
    {
        private readonly List<Value?> stack = [];
        private readonly IReadOnlyList<Instruction> code = function.Instructions;
        private readonly int captureCount = function.CaptureCount;
        private int pc;
        private bool returned;
        private Value? result;

        internal Value? Run()
        {
            for (pc = 0; pc < code.Count && !returned; pc++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Step(code[pc])) return null;
            }
            return returned ? result : stack.Count == 0 ? Value.Null : stack[^1];
        }

        private bool Step(Instruction instruction)
        {
            switch (instruction.Op)
            {
                case OpCode.Nop: return true;
                case OpCode.Constant: return Push(owner.image.Constants[instruction.A]);
                case OpCode.LoadBuiltin: return Push(owner.builtins?.Invoke(instruction.Text));
                case OpCode.Null: return Push(Value.Null);
                case OpCode.True: return Push(Value.True);
                case OpCode.False: return Push(Value.False);
                case OpCode.LoadLocal:
                case OpCode.LoadStorage: return Push(locals[instruction.A]);
                case OpCode.InitializeLocal:
                    locals[instruction.A] = Pop(); return locals[instruction.A] is not null;
                case OpCode.StoreLocal:
                    if (instruction.A < captureCount) return false;
                    locals[instruction.A] = Pop(); return locals[instruction.A] is not null;
                case OpCode.Pop: return Pop() is not null;
                case OpCode.Dup: return stack.Count != 0 && Push(stack[^1]);
                case OpCode.Return: result = stack.Count == 0 ? Value.Null : Pop(); returned = true; return result is not null;
                case OpCode.Convert:
                    return Pop() is { } converted && Push(KnownEvaluation.Convert(converted, instruction.Text, owner.runtime));
                case OpCode.Qualify:
                    {
                        Value? value = Pop(), receiver = Pop();
                        return receiver is ConnectionValue connection && value is not null
                            && Push(KnownEvaluation.Qualify(connection, value, owner.runtime));
                    }
                case OpCode.PathJoin:
                    {
                        Value? right = Pop(), left = Pop();
                        return left is PathValue or StringValue && right is PathValue or StringValue
                            && Push(PathSemantics.JoinValue(owner.runtime.AsPath(left), right));
                    }
                case OpCode.GetMember:
                    return Pop() is ScopeValue scope && Push(owner.members(scope, instruction.Text!));
                case OpCode.GetCapture:
                    return Pop() is FunctionValue { Captures: { } captures }
                        && instruction.A < captures.Count && Push(owner.resolver(captures[instruction.A]));
                case OpCode.MakeRange:
                    {
                        Value?[] values = PopMany(instruction.A);
                        return values.All(value => value is not null && value is not KnownCopyResult)
                            && Push(new RangeValue(values.Select(value => value!).ToArray(),
                                values.Aggregate(new TypeRef(TypeKind.Never), (type, value) => TypeRef.Union(type, value!.Type))));
                    }
                case OpCode.MakeScope:
                    {
                        Value?[] values = PopMany(instruction.A);
                        if (values.Any(value => value is null or KnownCopyResult)) return false;
                        string[] names = (instruction.Text ?? "").Split('\0');
                        Dictionary<string, Value> members = new(StringComparer.Ordinal);
                        for (int index = 0; index < names.Length && index < values.Length; index++)
                            if (!members.TryAdd(names[index], values[index]!)) return false;
                        return Push(new ScopeValue(members));
                    }
                case OpCode.MakeClosure:
                    {
                        Value?[] values = PopMany(instruction.B);
                        return values.All(value => value is not null && value is not KnownCopyResult)
                            && Push(new FunctionValue(instruction.A, values.Select(value => value!).ToArray()));
                    }
                case OpCode.SpecializeClosure:
                    {
                        if (Pop() is not FunctionValue closure || instruction.A < 0) return false;
                        int target = instruction.Text is null ? instruction.A
                            : FunctionDispatch.Parse(instruction.Text).GetValueOrDefault(closure.Function, -1);
                        return target >= 0 && Push(closure with { Function = target });
                    }
                case OpCode.Call: return Call(instruction);
                case OpCode.NativeCall:
                    {
                        Value?[] values = PopMany(instruction.A);
                        if (instruction.Text == "_core.iterator_close") return Push(Value.Null);
                        return values.All(value => value is not null)
                            && Push(owner.Native(instruction.Text!, values.Select(value => value!).ToArray()));
                    }
                case OpCode.Transfer:
                case OpCode.LocalTransfer:
                    {
                        Value? destination = Pop(), source = Pop();
                        if (destination is not (PathValue or StringValue)
                            || source is not (PathValue or StringValue or RangeValue { Selection: null, Resolve: null, SnapshotValues: not null }))
                            return false;
                        int index = owner.copies.Count;
                        owner.copies.Add(new(source, owner.runtime.AsPath(destination), instruction.Span));
                        return Push(new KnownCopyResult(index));
                    }
                case OpCode.GetIterator:
                    return Pop() is RangeValue { Selection: null, Resolve: null, SnapshotValues: { } snapshot }
                        && Push(new Iterator(snapshot));
                case OpCode.IteratorNext:
                    {
                        if (locals[instruction.A] is not Iterator iterator) return false;
                        if (iterator.Position < iterator.Values.Count) return Push(iterator.Values[iterator.Position++]);
                        pc = instruction.B - 1; return true;
                    }
                case OpCode.Jump:
                    if (instruction.A <= pc && code[instruction.A].Op != OpCode.IteratorNext) return false;
                    pc = instruction.A - 1; return true;
                case OpCode.JumpIfFalse:
                case OpCode.JumpIfTrue:
                    {
                        if (Pop() is not BoolValue condition) return false;
                        if (condition.Data != (instruction.Op == OpCode.JumpIfTrue)) return true;
                        if (instruction.A <= pc && code[instruction.A].Op != OpCode.IteratorNext) return false;
                        pc = instruction.A - 1; return true;
                    }
                case OpCode.Barrier when instruction.B == 0 && instruction.Text is null:
                case OpCode.Acquire when instruction.A == 0: return true;
                case OpCode.Not:
                case OpCode.IsNull:
                case OpCode.Negate:
                    return Pop() is { } unary && Push(KnownEvaluation.Unary(instruction.Op, unary));
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
                        return left is not null && right is not null && Push(KnownEvaluation.Binary(instruction.Op, left, right));
                    }
                case OpCode.RegexMatch:
                    {
                        Value? pattern = instruction.Text is null ? Pop() : new StringValue(instruction.Text);
                        Value? value = Pop();
                        return value is not null and not KnownCopyResult && pattern is StringValue expression
                            && Push(KnownEvaluation.Regex(value, expression.Data));
                    }
                default: return false;
            }
        }

        private bool Call(Instruction instruction)
        {
            Value?[] arguments = PopMany(instruction.A);
            if (Pop() is not FunctionValue function || instruction.B < 0 || arguments.Any(value => value is null)) return false;
            int? target = Target(function, instruction);
            if (function.NativeName is null && instruction.Text is not null && target is null) return false;
            return Push(owner.Invoke(function, arguments.Select(value => value!).ToArray(), target, cancellationToken));
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
}
