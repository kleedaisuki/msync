namespace Msync.Optimization;

public static class Optimizer
{
    public static Module Optimize(Module module, int level = 2)
    {
        if (level is < 0 or > 3) throw new MsyncException($"Optimization level must be 0, 1, 2, or 3; received {level}");
        if (level == 0) return BlockBuilder.Form(module with { OptimizationLevel = level });
        if (LinearEffectOptimization.TryOptimize(module, level, out var linear)) return linear;
        if (module.Functions.All(FunctionAnalysis.LiteralBody))
            return BlockBuilder.Form(ModuleOptimization.Compact(LocalSlotCompaction.Compact(module with { OptimizationLevel = level })));
        var preparation = module.Functions.Select(_ => new Dictionary<FunctionCode, FunctionCode>(ReferenceEqualityComparer.Instance)).ToList();
        var graphs = module.Functions.Select(_ => new Dictionary<FunctionCode, (OptimizationDependencies Dependencies, FunctionCode Result)>(ReferenceEqualityComparer.Instance)).ToList();
        var current = module with { OptimizationLevel = level, Blocks = null };
        while (true)
        {
            while (preparation.Count < current.Functions.Count)
            {
                preparation.Add(new(ReferenceEqualityComparer.Instance));
                graphs.Add(new(ReferenceEqualityComparer.Instance));
            }
            if (current.Functions.All(FunctionAnalysis.LiteralBody))
                return BlockBuilder.Form(ModuleOptimization.Compact(LocalSlotCompaction.Compact(current)));
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
                var memory = level >= 2 && (features & (FunctionFeatures.Scope | FunctionFeatures.Fields))
                    == (FunctionFeatures.Scope | FunctionFeatures.Fields)
                    ? CrossBlockMemoryOptimization.Optimize(context, predicated, constants) : predicated;
                var scalar = (features & FunctionFeatures.Scope) != 0 ? ScopeScalarization.Optimize(context, memory, constants) : memory;
                var tail = (features & FunctionFeatures.Call) != 0 ? TailCallOptimization.Optimize(context, scalar, index) : scalar;
                var result = (features & FunctionFeatures.Iteration) != 0 ? LoopOptimization.Optimize(context, tail, constants, level) : tail;
                result = Unchanged(function, result);
                preparation[index].Add(function, result);
                return result;
            }).ToArray()
            };
            if (level >= 2 && !prepared.PreserveLocals)
            {
                var clients = prepared.Functions.Select(function => NeedsMemoryAnalysis(function, constants)).ToArray();
                if (clients.Contains(true))
                {
                    var memoryContext = prepared;
                    var memoryEffects = MemoryEffects.Analyze(memoryContext);
                    prepared = prepared with
                    {
                        Functions = prepared.Functions.Select((function, index) => clients[index]
                            ? CrossBlockMemoryOptimization.Optimize(memoryContext, function, constants, memoryEffects) : function).ToArray()
                    };
                }
            }
            var effects = EffectAnalysis.FunctionEffects(prepared);
            var functions = prepared.Functions.Select((function, index) => prepared.PreserveLocals && index == prepared.EntryPoint
                || FunctionAnalysis.LiteralBody(function) ? function : Evaluate(Graph(function, index))).ToArray();
            var reduced = LocalSlotCompaction.Compact(prepared with { Constants = constants, Functions = functions });
            for (var index = 0; index < reduced.Functions.Count; index++)
                graphs[index][reduced.Functions[index]] =
                    (OptimizationDependencies.Capture(reduced.Functions[index], reduced, effects), reduced.Functions[index]);
            var specialized = level >= 2 ? InterproceduralSpecialization.Optimize(reduced, level) : reduced;
            if (level >= 2) specialized = AggregateArgumentSpecialization.Optimize(specialized, level);
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
                var cleaned = numbered with { Functions = numbered.Functions.Select(Clean).ToArray(), Constants = numberedConstants };
                return BlockBuilder.Form(ModuleOptimization.Compact(LocalSlotCompaction.Compact(cleaned)));

                FunctionCode Clean(FunctionCode function, int index)
                {
                    if (numbered.PreserveLocals && index == numbered.EntryPoint || FunctionAnalysis.LiteralBody(function)) return function;
                    if (graphs[index].TryGetValue(function, out var existing) && existing.Dependencies.Matches(numbered, numberedEffects)) return existing.Result;
                    return GraphOptimization.Optimize(numbered, function, numberedConstants, numberedEffects, numberedInterner);
                }

                FunctionCode Number(FunctionCode function)
                {
                    if (FunctionAnalysis.LiteralBody(function)) return function;
                    var features = FunctionAnalysis.Features(function);
                    var normalized = (features & FunctionFeatures.Directional) != 0 ? DirectionalNormalization.Optimize(inlined, function) : function;
                    var available = level >= 2 && (features & FunctionFeatures.Branch) != 0 ? GlobalRedundancy.OptimizeWithPool(inlined, normalized, constants, constantInterner) : normalized;
                    var indexed = level >= 2 && (features & FunctionFeatures.IndexedReads) != 0
                        ? IndexedMemoryOptimization.Optimize(available, constants) : available;
                    return ValueNumbering.Eliminate(indexed, constants);
                }
            }
            current = inlined;

            FunctionCode Graph(FunctionCode function, int index)
            {
                if (graphs[index].TryGetValue(function, out var existing) && existing.Dependencies.Matches(prepared, effects)) return existing.Result;
                var result = Unchanged(function, GraphOptimization.Optimize(prepared, function, constants, effects, constantInterner));
                var dependencies = OptimizationDependencies.Capture(function, prepared, effects);
                graphs[index][function] = (dependencies, result);
                graphs[index][result] = (dependencies, result);
                return result;
            }

            FunctionCode Evaluate(FunctionCode function) => level >= 2
                && (FunctionAnalysis.Features(function) & FunctionFeatures.Iteration) != 0
                ? CompileTimeEvaluation.OptimizeWithPool(prepared, function, constants, constantInterner) : function;
        }
    }
    private static FunctionCode Unchanged(FunctionCode previous, FunctionCode next) => next.LocalCount == previous.LocalCount
        && next.Instructions.SequenceEqual(previous.Instructions) ? previous : next;
    private static bool NeedsMemoryAnalysis(FunctionCode function, IReadOnlyList<Value> constants)
    {
        bool fields = false;
        bool scope = function.CaptureCount > 0 || function.ParameterCount > 0 &&
            (function.ParameterTypes is null || function.ParameterTypes.Any(type => (type.Kind & TypeKind.Scope) != 0));
        foreach (var instruction in function.Instructions)
        {
            fields |= instruction.Op is OpCode.GetMember or OpCode.SetMember;
            scope |= instruction.Op is OpCode.MakeScope or OpCode.SetMember
                || instruction.Op == OpCode.GetMember && instruction.A > 0
                || instruction.Op == OpCode.Constant && constants[instruction.A] is ScopeValue;
        }
        return fields && scope;
    }
}
