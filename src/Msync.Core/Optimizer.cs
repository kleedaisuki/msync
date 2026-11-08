namespace Msync.Core;

public static class Optimizer
{
    public static Module Optimize(Module module, int level = 1)
    {
        if (level is < 0 or > 3) throw new MsyncException($"Optimization level must be 0, 1, 2, or 3; received {level}");
        var constants = module.Constants.ToList();
        var functions = module.Functions.Select(f => level == 0 ? f : FoldBranches(FoldConstants(f, constants))).ToArray();
        return FormBlocks(module with { Constants = constants, Functions = functions, OptimizationLevel = level });
    }
    public static Module FormBlocks(Module module) => module with
    { Blocks = module.Functions.SelectMany((function, index) => Partition(function, index)).ToArray() };
    private static FunctionCode FoldConstants(FunctionCode function, List<Value> constants)
    {
        var code = function.Instructions.ToArray();
        var targets = code.SelectMany(i => i.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue ? [i.A] : i.Op == OpCode.IteratorNext ? new[] { i.B } : []).ToHashSet();
        Value? Constant(Instruction instruction) => instruction.Op switch
        {
            OpCode.Constant => constants[instruction.A],
            OpCode.True => Value.True,
            OpCode.False => Value.False,
            OpCode.Null => Value.Null,
            _ => null
        };
        Instruction Emit(Value value, SourceSpan? span)
        {
            if (value is BoolValue boolean) return new(boolean.Data ? OpCode.True : OpCode.False, Span: span);
            if (value is NullValue) return new(OpCode.Null, Span: span);
            var index = constants.IndexOf(value);
            if (index < 0) { index = constants.Count; constants.Add(value); }
            return new(OpCode.Constant, index, Span: span);
        }
        for (var i = 0; i + 1 < code.Length; i++)
        {
            var left = Constant(code[i]);
            if (left is null || targets.Contains(i + 1)) continue;
            Value? unary = (code[i + 1].Op, left) switch
            {
                (OpCode.Not, BoolValue b) => b.Data ? Value.False : Value.True,
                (OpCode.IsNull, _) => left is NullValue ? Value.True : Value.False,
                (OpCode.Negate, NumberValue n) => new NumberValue(-n.Data),
                _ => null
            };
            if (unary is not null)
            { code[i] = new(OpCode.Nop); code[i + 1] = Emit(unary, code[i + 1].Span); continue; }
            if (i + 2 >= code.Length || targets.Contains(i + 2)) continue;
            var right = Constant(code[i + 1]);
            if (right is null) continue;
            Value? folded = (code[i + 2].Op, left, right) switch
            {
                (OpCode.Add, NumberValue a, NumberValue b) => new NumberValue(a.Data + b.Data),
                (OpCode.Subtract, NumberValue a, NumberValue b) => new NumberValue(a.Data - b.Data),
                (OpCode.Multiply, NumberValue a, NumberValue b) => new NumberValue(a.Data * b.Data),
                (OpCode.Divide, NumberValue a, NumberValue b) => new NumberValue(a.Data / b.Data),
                (OpCode.Add, StringValue a, StringValue b) => new StringValue(a.Data + b.Data),
                (OpCode.Equal, BoolValue a, BoolValue b) => a.Data == b.Data ? Value.True : Value.False,
                (OpCode.NotEqual, BoolValue a, BoolValue b) => a.Data != b.Data ? Value.True : Value.False,
                (OpCode.Equal, StringValue a, StringValue b) => a.Data == b.Data ? Value.True : Value.False,
                (OpCode.NotEqual, StringValue a, StringValue b) => a.Data != b.Data ? Value.True : Value.False,
                (OpCode.IsType, TypeValue a, TypeValue b) => b.Data.Accepts(a.Data) ? Value.True : Value.False,
                _ => null
            };
            if (folded is null) continue;
            code[i] = new(OpCode.Nop); code[i + 1] = new(OpCode.Nop); code[i + 2] = Emit(folded, code[i + 2].Span);
        }
        return function with { Instructions = code };
    }
    private static FunctionCode FoldBranches(FunctionCode function)
    {
        var code = function.Instructions.ToArray();
        var targets = code.SelectMany(i => i.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue ? [i.A] : i.Op == OpCode.IteratorNext ? new[] { i.B } : []).ToHashSet();
        for (var index = 0; index + 1 < code.Length; index++)
        {
            var constant = code[index]; var branch = code[index + 1];
            if (targets.Contains(index + 1) || constant.Op is not (OpCode.True or OpCode.False) || branch.Op is not (OpCode.JumpIfFalse or OpCode.JumpIfTrue)) continue;
            var taken = constant.Op == OpCode.True == (branch.Op == OpCode.JumpIfTrue);
            code[index] = new(OpCode.Nop, Span: constant.Span);
            code[index + 1] = taken ? new(OpCode.Jump, branch.A, Span: branch.Span) : new(OpCode.Nop, Span: branch.Span);
        }
        return function with { Instructions = code };
    }
    private static IEnumerable<BasicBlock> Partition(FunctionCode function, int functionIndex)
    {
        var code = function.Instructions;
        if (code.Count == 0) yield break;
        var starts = new SortedSet<int> { 0, code.Count };
        for (var index = 0; index < code.Count; index++)
        {
            var instruction = code[index];
            if (instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue) starts.Add(instruction.A);
            if (instruction.Op == OpCode.IteratorNext) starts.Add(instruction.B);
            if (IsBoundary(instruction)) { starts.Add(index); starts.Add(index + 1); }
        }
        var boundaries = starts.ToArray();
        for (var i = 0; i + 1 < boundaries.Length; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1];
            if (start == end) continue;
            var last = code[end - 1];
            IReadOnlyList<int> next = last.Op switch
            {
                OpCode.Return => [],
                OpCode.Jump => [last.A],
                OpCode.JumpIfTrue or OpCode.JumpIfFalse => [last.A, end],
                OpCode.IteratorNext => [last.B, end],
                _ => end < code.Count ? [end] : []
            };
            yield return new(functionIndex, start, end, next, code.Skip(start).Take(end - start).Any(IsEffect));
        }
    }
    private static bool IsBoundary(Instruction i) => IsEffect(i) || i.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue or OpCode.IteratorNext or OpCode.Return;
    private static bool IsEffect(Instruction i) => i.Op is OpCode.Transfer or OpCode.LocalTransfer or OpCode.Call or OpCode.NativeCall or OpCode.ProcessCall or OpCode.GetIterator or OpCode.IteratorNext;
}

public static class Binding
{
    public static Module Bind(Module module, BindingContext context)
    {
        if (module.Stage == ArtifactStage.Executable) return module;
        var full = context with { WorkingDirectory = Path.GetFullPath(context.WorkingDirectory), HomeDirectory = context.HomeDirectory.Length == 0 ? HostContext.HomeDirectory : Path.GetFullPath(context.HomeDirectory) };
        Value BindValue(Value value) => value switch
        {
            PathValue path => path with { WorkingDirectory = path.WorkingDirectory ?? full.WorkingDirectory, HomeDirectory = path.HomeDirectory ?? full.HomeDirectory },
            FunctionValue function => function with { Captures = function.Captures?.Select(BindValue).ToArray() },
            ScopeValue scope => new ScopeValue(scope.Members.ToDictionary(p => p.Key, p => BindValue(p.Value))),
            _ => value
        };
        var bound = module with { Constants = module.Constants.Select(BindValue).ToArray(), Context = full, Stage = ArtifactStage.Executable };
        return ExecutionLowering.Lower(bound);
    }
}
