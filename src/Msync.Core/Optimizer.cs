namespace Msync.Core;

public static class Optimizer
{
    public static Module Optimize(Module module, int level = 1)
    {
        if (level is < 0 or > 3) throw new MsyncException($"Optimization level must be 0, 1, 2, or 3; received {level}");
        if (level == 0) return FormBlocks(module with { OptimizationLevel = level });
        var current = module with { OptimizationLevel = level, Blocks = null };
        while (true)
        {
            var constants = current.Constants.ToList();
            var prepared = current with { Constants = constants, Functions = current.Functions.Select((function, index) =>
            {
                if (current.PreserveLocals && index == current.EntryPoint) return function;
                var context = current with { Constants = constants };
                var predicated = PredicateOptimization.Optimize(context, function, constants);
                var scalar = ScopeScalarization.Optimize(context, predicated, constants);
                var tail = TailCallOptimization.Optimize(context, scalar, index);
                return LoopOptimization.Optimize(context, tail, constants, level);
            }).ToArray() };
            var effects = EffectAnalysis.FunctionEffects(prepared);
            var functions = prepared.Functions.Select((function, index) => prepared.PreserveLocals && index == prepared.EntryPoint
                ? function : GraphOptimization.Optimize(prepared, function, constants, effects)).ToArray();
            var reduced = LocalSlotCompaction.Compact(prepared with { Constants = constants, Functions = functions });
            var inlined = FunctionInlining.Inline(reduced, level);
            if (inlined.Functions.Select((function, index) => function.Instructions.SequenceEqual(current.Functions[index].Instructions)).All(equal => equal))
            {
                var numbered = inlined with { Functions = inlined.Functions.Select((function, index) => inlined.PreserveLocals && index == inlined.EntryPoint
                    ? function : Number(function)).ToArray() };
                var numberedConstants = numbered.Constants.ToList();
                var numberedEffects = EffectAnalysis.FunctionEffects(numbered);
                var cleaned = numbered with { Functions = numbered.Functions.Select((function, index) => numbered.PreserveLocals && index == numbered.EntryPoint
                    ? function : GraphOptimization.Optimize(numbered, function, numberedConstants, numberedEffects)).ToArray(), Constants = numberedConstants };
                return FormBlocks(ModuleOptimization.Compact(LocalSlotCompaction.Compact(cleaned)));

                FunctionCode Number(FunctionCode function)
                {
                    var normalized = DirectionalNormalization.Optimize(inlined, function);
                    var available = level >= 2 ? GlobalRedundancy.Optimize(inlined, normalized, constants) : normalized;
                    return ValueNumbering.Eliminate(available, constants);
                }
            }
            current = inlined;
        }
    }
    public static Module FormBlocks(Module module) => module with
    { Blocks = module.Functions.SelectMany((function, index) => Partition(function, index)).ToArray() };
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
    private static bool IsEffect(Instruction i) => EffectAnalysis.Effects(i) != InstructionEffect.None;
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
            ScopeValue scope => new ScopeValue(scope.Members.ToDictionary(p => p.Key, p => BindValue(p.Value))) { AllocationSite = scope.AllocationSite },
            _ => value
        };
        var bound = module with { Constants = module.Constants.Select(BindValue).ToArray(), Context = full, Stage = ArtifactStage.Executable };
        var optimized = Optimizer.Optimize(ExecutionLowering.Lower(bound), module.OptimizationLevel);
        return Optimizer.Optimize(ExecutionLowering.Lower(optimized), module.OptimizationLevel);
    }
}
