namespace Msync.Core;

public static class Optimizer
{
    public static Module Optimize(Module module, int level = 2)
    {
        if (level is < 0 or > 3) throw new MsyncException($"Optimization level must be 0, 1, 2, or 3; received {level}");
        if (level == 0) return FormBlocks(module with { OptimizationLevel = level });
        if (module.Functions.All(FunctionAnalysis.LiteralBody))
            return FormBlocks(ModuleOptimization.Compact(LocalSlotCompaction.Compact(module with { OptimizationLevel = level })));
        var preparation = module.Functions.Select(_ => new Dictionary<FunctionCode, FunctionCode>(ReferenceEqualityComparer.Instance)).ToList();
        var graphs = module.Functions.Select(_ => new Dictionary<FunctionCode, (int Epoch, FunctionCode Result)>(ReferenceEqualityComparer.Instance)).ToList();
        IReadOnlyList<InstructionEffect>? previousEffects = null;
        var effectEpoch = 0;
        var current = module with { OptimizationLevel = level, Blocks = null };
        while (true)
        {
            while (preparation.Count < current.Functions.Count)
            {
                preparation.Add(new(ReferenceEqualityComparer.Instance));
                graphs.Add(new(ReferenceEqualityComparer.Instance));
            }
            if (current.Functions.All(FunctionAnalysis.LiteralBody))
                return FormBlocks(ModuleOptimization.Compact(LocalSlotCompaction.Compact(current)));
            var constants = current.Constants.ToList();
            var constantInterner = new ConstantInterner(constants);
            var context = current with { Constants = constants };
            var prepared = context with
            {
                Functions = current.Functions.Select((function, index) =>
            {
                if (current.PreserveLocals && index == current.EntryPoint) return function;
                if (FunctionAnalysis.LiteralBody(function)) return function;
                if (preparation[index].TryGetValue(function, out var existing)) return existing;
                var features = FunctionAnalysis.Features(function);
                var predicated = (features & FunctionFeatures.Predicates) != 0 ? PredicateOptimization.OptimizeWithPool(context, function, constants, constantInterner) : function;
                var scalar = (features & FunctionFeatures.Scope) != 0 ? ScopeScalarization.Optimize(context, predicated, constants) : predicated;
                var tail = (features & FunctionFeatures.Call) != 0 ? TailCallOptimization.Optimize(context, scalar, index) : scalar;
                var result = (features & FunctionFeatures.Iteration) != 0 ? LoopOptimization.Optimize(context, tail, constants, level) : tail;
                result = Unchanged(function, result);
                preparation[index].Add(function, result);
                return result;
            }).ToArray()
            };
            var effects = EffectAnalysis.FunctionEffects(prepared);
            if (previousEffects is null || !effects.SequenceEqual(previousEffects)) { effectEpoch++; previousEffects = effects; }
            var functions = prepared.Functions.Select((function, index) => prepared.PreserveLocals && index == prepared.EntryPoint
                || FunctionAnalysis.LiteralBody(function) ? function : Evaluate(Graph(function, index))).ToArray();
            var reduced = LocalSlotCompaction.Compact(prepared with { Constants = constants, Functions = functions });
            for (var index = 0; index < reduced.Functions.Count; index++)
                graphs[index][reduced.Functions[index]] = (effectEpoch, reduced.Functions[index]);
            var specialized = level >= 2 ? InterproceduralSpecialization.Optimize(reduced, level) : reduced;
            var inlined = FunctionInlining.Inline(specialized, level);
            if (inlined.Functions.Count == current.Functions.Count
                && inlined.Functions.Select((function, index) => function.Instructions.SequenceEqual(current.Functions[index].Instructions)).All(equal => equal))
            {
                var numbered = inlined with
                {
                    Functions = inlined.Functions.Select((function, index) => inlined.PreserveLocals && index == inlined.EntryPoint
                    ? function : Number(function)).ToArray()
                };
                var numberedConstants = numbered.Constants.ToList();
                var numberedInterner = new ConstantInterner(numberedConstants);
                var numberedEffects = EffectAnalysis.FunctionEffects(numbered);
                var sameEffects = numberedEffects.SequenceEqual(effects);
                var cleaned = numbered with { Functions = numbered.Functions.Select(Clean).ToArray(), Constants = numberedConstants };
                return FormBlocks(ModuleOptimization.Compact(LocalSlotCompaction.Compact(cleaned)));

                FunctionCode Clean(FunctionCode function, int index)
                {
                    if (numbered.PreserveLocals && index == numbered.EntryPoint || FunctionAnalysis.LiteralBody(function)) return function;
                    if (sameEffects && graphs[index].TryGetValue(function, out var existing) && existing.Epoch == effectEpoch) return existing.Result;
                    return GraphOptimization.Optimize(numbered, function, numberedConstants, numberedEffects, numberedInterner);
                }

                FunctionCode Number(FunctionCode function)
                {
                    if (FunctionAnalysis.LiteralBody(function)) return function;
                    var features = FunctionAnalysis.Features(function);
                    var normalized = (features & FunctionFeatures.Directional) != 0 ? DirectionalNormalization.Optimize(inlined, function) : function;
                    var available = level >= 2 && (features & FunctionFeatures.Branch) != 0 ? GlobalRedundancy.OptimizeWithPool(inlined, normalized, constants, constantInterner) : normalized;
                    return ValueNumbering.Eliminate(available, constants);
                }
            }
            current = inlined;

            FunctionCode Graph(FunctionCode function, int index)
            {
                if (graphs[index].TryGetValue(function, out var existing) && existing.Epoch == effectEpoch) return existing.Result;
                var result = Unchanged(function, GraphOptimization.Optimize(prepared, function, constants, effects, constantInterner));
                graphs[index][function] = (effectEpoch, result);
                graphs[index][result] = (effectEpoch, result);
                return result;
            }

            FunctionCode Evaluate(FunctionCode function) => level >= 2
                && (FunctionAnalysis.Features(function) & FunctionFeatures.Iteration) != 0
                ? CompileTimeEvaluation.OptimizeWithPool(prepared, function, constants, constantInterner) : function;
        }
    }
    private static FunctionCode Unchanged(FunctionCode previous, FunctionCode next) => next.LocalCount == previous.LocalCount
        && next.Instructions.SequenceEqual(previous.Instructions) ? previous : next;
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
