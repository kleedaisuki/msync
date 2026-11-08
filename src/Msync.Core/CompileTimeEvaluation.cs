namespace Msync.Core;

internal static class CompileTimeEvaluation
{
    private sealed record FiniteRange(object[] Items);
    private sealed class Cursor(FiniteRange range)
    {
        internal readonly object[] Items = range.Items;
        internal int Position;
    }

    internal static FunctionCode Optimize(Module module, FunctionCode function, List<Value> constants)
    {
        if (module.PreserveLocals || function.ParameterCount != 0 || function.CaptureCount != 0
            || !function.Instructions.Any(instruction => instruction.Op == OpCode.IteratorNext)
            || !Certified(function)) return function;
        Value? result;
        try { result = Evaluate(module, function, constants); }
        catch (ArgumentException) { return function; }
        if (result is not (NullValue or BoolValue or NumberValue or StringValue or PathValue or ConnectionValue or TypeValue)) return function;
        Instruction literal;
        var span = function.Instructions.LastOrDefault(instruction => instruction.Op == OpCode.Return)?.Span;
        if (result is NullValue) literal = new(OpCode.Null, Span: span);
        else if (result is BoolValue boolean) literal = new(boolean.Data ? OpCode.True : OpCode.False, Span: span);
        else
        {
            var index = ValueRepresentation.IndexOf(constants, result);
            if (index < 0) { index = constants.Count; constants.Add(result); }
            literal = new(OpCode.Constant, index, Span: span);
        }
        return function with { Instructions = [literal, new(OpCode.Return, Span: span)] };
    }

    private static bool Certified(FunctionCode function)
    {
        var code = function.Instructions;
        var headers = new Dictionary<int, (int Slot, int Latch)>();
        var uses = new Dictionary<int, List<int>>();
        var incoming = new List<int>?[code.Count];
        for (var pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            if (!Supported(instruction.Op)) return false;
            if (instruction.Op is OpCode.LoadLocal or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.IteratorNext)
            {
                if (!uses.TryGetValue(instruction.A, out var sites)) uses[instruction.A] = sites = [];
                sites.Add(pc);
            }
            if (instruction.Op == OpCode.IteratorNext && instruction.B <= pc) return false;
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue && instruction.A < code.Count)
                (incoming[instruction.A] ??= []).Add(pc);
            if (instruction.Op is not (OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue) || instruction.A > pc) continue;
            var header = instruction.A;
            if (header < 2 || code[header].Op != OpCode.IteratorNext || code[header - 2].Op != OpCode.GetIterator
                || code[header - 1] is not { Op: OpCode.StoreLocal } store || store.A != code[header].A) return false;
            if (!headers.TryGetValue(header, out var previous) || pc > previous.Latch) headers[header] = (store.A, pc);
        }
        foreach (var (header, loop) in headers)
        {
            // Every cycle advances its own finite cursor. Its sole reset is in
            // the preheader; external body entry would invalidate that proof.
            if (code[header].B != loop.Latch + 1) return false;
            if (uses[loop.Slot].Any(pc => pc != header - 1 && pc != header)) return false;
            for (var target = header + 1; target <= loop.Latch; target++)
                if (incoming[target] is { } sources && sources.Any(pc => pc < header - 1 || pc > loop.Latch)) return false;
        }
        return true;
    }

    private static bool Supported(OpCode op) => op is OpCode.Constant or OpCode.Null or OpCode.True or OpCode.False
        or OpCode.LoadLocal or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal or OpCode.Pop or OpCode.Dup
        or OpCode.Jump or OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.Return or OpCode.MakeRange
        or OpCode.GetIterator or OpCode.IteratorNext or OpCode.Not or OpCode.IsNull or OpCode.IsType
        or OpCode.Equal or OpCode.NotEqual or OpCode.Less or OpCode.LessEqual or OpCode.Greater or OpCode.GreaterEqual
        or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Negate or OpCode.Convert
        or OpCode.PathJoin or OpCode.Qualify or OpCode.NativeCall or OpCode.Nop;

    private static Value? Evaluate(Module module, FunctionCode function, IReadOnlyList<Value> constants)
    {
        var stack = new List<object>();
        var locals = new object?[function.LocalCount];
        object? Pop()
        {
            if (stack.Count == 0) return null;
            var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value;
        }
        object[]? PopMany(int count)
        {
            if (stack.Count < count) return null;
            var values = stack.GetRange(stack.Count - count, count).ToArray();
            stack.RemoveRange(stack.Count - count, count); return values;
        }
        Value Bool(bool value) => value ? Value.True : Value.False;
        var code = function.Instructions;
        for (var pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.Nop: break;
                case OpCode.Constant:
                    if (constants[instruction.A] is not (NullValue or BoolValue or NumberValue or StringValue or PathValue or ConnectionValue or TypeValue)) return null;
                    stack.Add(constants[instruction.A]); break;
                case OpCode.Null: stack.Add(Value.Null); break;
                case OpCode.True: stack.Add(Value.True); break;
                case OpCode.False: stack.Add(Value.False); break;
                case OpCode.LoadLocal:
                case OpCode.LoadStorage:
                    if (locals[instruction.A] is not { } local) return null;
                    stack.Add(local); break;
                case OpCode.StoreLocal:
                case OpCode.InitializeLocal:
                    if (Pop() is not { } assigned) return null;
                    locals[instruction.A] = assigned; break;
                case OpCode.Pop: if (Pop() is null) return null; break;
                case OpCode.Dup:
                    if (stack.Count == 0) return null;
                    stack.Add(stack[^1]); break;
                case OpCode.Jump: pc = instruction.A - 1; break;
                case OpCode.JumpIfTrue:
                case OpCode.JumpIfFalse:
                    if (Pop() is not BoolValue condition) return null;
                    if (condition.Data == (instruction.Op == OpCode.JumpIfTrue)) pc = instruction.A - 1;
                    break;
                case OpCode.Return: return stack.Count == 0 ? Value.Null : Pop() as Value;
                case OpCode.MakeRange:
                    if (PopMany(instruction.A) is not { } items) return null;
                    stack.Add(new FiniteRange(items)); break;
                case OpCode.GetIterator:
                    if (Pop() is not FiniteRange range) return null;
                    stack.Add(new Cursor(range)); break;
                case OpCode.IteratorNext:
                    if (locals[instruction.A] is not Cursor iterator) return null;
                    if (iterator.Position == iterator.Items.Length) pc = instruction.B - 1;
                    else stack.Add(iterator.Items[iterator.Position++]);
                    break;
                case OpCode.Not:
                    if (Pop() is not BoolValue boolean) return null;
                    stack.Add(Bool(!boolean.Data)); break;
                case OpCode.IsNull: stack.Add(Bool(Pop() is NullValue)); break;
                case OpCode.IsType:
                    if (Pop() is not TypeValue expected || Pop() is not Value tested) return null;
                    stack.Add(Bool(expected.Data.Accepts(tested is TypeValue actual ? actual.Data : tested.Type))); break;
                case OpCode.Negate:
                    if (Pop() is not NumberValue number) return null;
                    stack.Add(new NumberValue(-number.Data)); break;
                case OpCode.NativeCall:
                    if (PopMany(instruction.A) is not { } inputs || inputs.Any(input => input is not Value)) return null;
                    if (PureEvaluation.Native(instruction.Text, inputs.Cast<Value>().ToArray(), module) is not { } native) return null;
                    stack.Add(native); break;
                case OpCode.Convert:
                    if (Pop() is not Value converted || Convert(module, instruction.Text, converted) is not { } conversion) return null;
                    stack.Add(conversion); break;
                default:
                    if (Pop() is not Value right || Pop() is not Value left || Binary(module, instruction.Op, left, right) is not { } result) return null;
                    stack.Add(result); break;
            }
        }
        return stack.Count == 0 ? Value.Null : Pop() as Value;
    }

    private static Value? Convert(Module module, string? type, Value value) => (type, value) switch
    {
        ("Path", PathValue) or ("String", StringValue) => value,
        ("Path", StringValue text) when module.Context is { } context => new PathValue(text.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory),
        ("String", PathValue path) when module.Stage == ArtifactStage.Executable => new StringValue(path.NativeText),
        _ => null
    };

    private static Value? Binary(Module module, OpCode op, Value left, Value right)
    {
        Value Bool(bool value) => value ? Value.True : Value.False;
        if (left is NumberValue a && right is NumberValue b)
        {
            var comparison = a.Data.CompareTo(b.Data);
            return op switch
            {
                OpCode.Add => new NumberValue(a.Data + b.Data),
                OpCode.Subtract => new NumberValue(a.Data - b.Data),
                OpCode.Multiply => new NumberValue(a.Data * b.Data),
                OpCode.Divide => new NumberValue(a.Data / b.Data),
                _ => Compare(op, comparison)
            };
        }
        if (op == OpCode.Add && left is StringValue x && right is StringValue y) return new StringValue(x.Data + y.Data);
        if (left is BoolValue c && right is BoolValue d) return Compare(op, c.Data.CompareTo(d.Data));
        if (left is TypeValue e && right is TypeValue f) return Compare(op, e.Data == f.Data ? 0 : 1);
        if (op == OpCode.PathJoin && left is PathValue path && right is PathValue or StringValue
            && (module.Stage == ArtifactStage.Executable || path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl))
            return PathSemantics.JoinValue(path, right);
        if (op == OpCode.Qualify && left is ConnectionValue connection && right is PathValue qualified)
            return PureEvaluation.Qualify(connection, qualified, module.Context);
        if (left is StringValue or PathValue && right is StringValue or PathValue
            && (module.Stage == ArtifactStage.Executable || left is StringValue && right is StringValue))
        {
            var first = left is StringValue text ? text.Data : ((PathValue)left).NativeText;
            var second = right is StringValue text2 ? text2.Data : ((PathValue)right).NativeText;
            return Compare(op, TextSemantics.CompareOrdinal(first, second));
        }
        return null;

        Value? Compare(OpCode comparison, int result) => comparison switch
        {
            OpCode.Equal => Bool(result == 0),
            OpCode.NotEqual => Bool(result != 0),
            OpCode.Less => Bool(result < 0),
            OpCode.LessEqual => Bool(result <= 0),
            OpCode.Greater => Bool(result > 0),
            OpCode.GreaterEqual => Bool(result >= 0),
            _ => null
        };
    }
}
