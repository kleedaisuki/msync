namespace Msync.Optimization;

public static class Optimizer
{
    public static Module Optimize(Module module, int level = 2)
        => BlockBuilder.Form(OptimizeInstructions(module, level));

    public static Module OptimizeInstructions(Module module, int level = 2) =>
        OptimizeCore(module, level, null);

    public static Module OptimizeInstructions(Module module, int level, CompilationTrace? trace)
    {
        if (trace?.IncludeOptimizerPasses != true) return OptimizeInstructions(module, level);
        var profile = new OptimizationProfile(trace);
        try { return OptimizeCore(module, level, profile); }
        finally { profile.Publish(); }
    }

    private static Module OptimizeCore(Module module, int level, OptimizationProfile? profile)
    {
        if (level is < 0 or > 3) throw new MsyncException($"Optimization level must be 0, 1, 2, or 3; received {level}");
        module = module with { OptimizationLevel = level, Blocks = null };
        if (level == 0) return module;
        var started = profile?.Start() ?? default;
        bool isLinear = LinearEffectOptimization.TryOptimize(module, level, out var linear);
        profile?.Add(OptimizationProfile.Pass.Linear, started, module, isLinear ? linear! : module);
        if (isLinear) return linear!;
        if (module.Functions.All(FunctionAnalysis.LiteralBody))
            return Compact(module, profile);
        var preparation = module.Functions.Select(_ => new Dictionary<FunctionCode, FunctionCode>(ReferenceEqualityComparer.Instance)).ToList();
        var graphs = module.Functions.Select(_ => new Dictionary<FunctionCode, (OptimizationDependencies Dependencies, FunctionCode Result)>(ReferenceEqualityComparer.Instance)).ToList();
        var current = module;
        while (true)
        {
            while (preparation.Count < current.Functions.Count)
            {
                preparation.Add(new(ReferenceEqualityComparer.Instance));
                graphs.Add(new(ReferenceEqualityComparer.Instance));
            }
            if (current.Functions.All(FunctionAnalysis.LiteralBody))
                return Compact(current, profile);
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
                var start = profile?.Start() ?? default;
                var predicated = (features & FunctionFeatures.Predicates) != 0 ? PredicateOptimization.OptimizeWithPool(context, function, constants, constantInterner) : function;
                if ((features & FunctionFeatures.Predicates) != 0) profile?.Add(OptimizationProfile.Pass.Predicates, start, function, predicated);
                // Compress proved scan CFGs before per-block memory analyses.
                start = profile?.Start() ?? default;
                var scheduled = (features & FunctionFeatures.Iteration) != 0
                    ? AffineLoopOptimization.Optimize(context, predicated, level) : predicated;
                if ((features & FunctionFeatures.Iteration) != 0) profile?.Add(OptimizationProfile.Pass.Affine, start, predicated, scheduled);
                start = profile?.Start() ?? default;
                var memory = level >= 2 && (features & (FunctionFeatures.Scope | FunctionFeatures.Fields))
                    == (FunctionFeatures.Scope | FunctionFeatures.Fields)
                    ? CrossBlockMemoryOptimization.Optimize(context, scheduled, constants) : scheduled;
                if (level >= 2 && (features & (FunctionFeatures.Scope | FunctionFeatures.Fields)) == (FunctionFeatures.Scope | FunctionFeatures.Fields))
                    profile?.Add(OptimizationProfile.Pass.Memory, start, scheduled, memory);
                start = profile?.Start() ?? default;
                var scalar = (features & FunctionFeatures.Scope) != 0 ? ScopeScalarization.Optimize(memory) : memory;
                if ((features & FunctionFeatures.Scope) != 0) profile?.Add(OptimizationProfile.Pass.ScopeScalarization, start, memory, scalar);
                start = profile?.Start() ?? default;
                var tail = (features & FunctionFeatures.Call) != 0 ? TailCallOptimization.Optimize(context, scalar, index) : scalar;
                if ((features & FunctionFeatures.Call) != 0) profile?.Add(OptimizationProfile.Pass.TailCalls, start, scalar, tail);
                start = profile?.Start() ?? default;
                bool reschedule = (features & FunctionFeatures.Iteration) != 0 && !ReferenceEquals(scheduled, Unchanged(scheduled, tail));
                var affine = reschedule ? AffineLoopOptimization.Optimize(context, tail, level) : tail;
                if (reschedule) profile?.Add(OptimizationProfile.Pass.Affine, start, tail, affine);
                start = profile?.Start() ?? default;
                var result = (features & FunctionFeatures.Iteration) != 0 ? LoopOptimization.Optimize(context, affine, constants, level) : affine;
                if ((features & FunctionFeatures.Iteration) != 0) profile?.Add(OptimizationProfile.Pass.Iteration, start, affine, result);
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
                    var start = profile?.Start() ?? default;
                    var memoryEffects = MemoryEffects.Analyze(memoryContext);
                    profile?.AddAnalysis(OptimizationProfile.Pass.MemoryEffects, start, memoryContext);
                    prepared = prepared with
                    {
                        Functions = prepared.Functions.Select((function, index) =>
                        {
                            if (!clients[index]) return function;
                            var startedMemory = profile?.Start() ?? default;
                            var result = CrossBlockMemoryOptimization.Optimize(memoryContext, function, constants, memoryEffects);
                            profile?.Add(OptimizationProfile.Pass.Memory, startedMemory, function, result);
                            return result;
                        }).ToArray()
                    };
                }
            }
            var effects = MeasureEffects(prepared, profile);
            var functions = prepared.Functions.Select((function, index) => prepared.PreserveLocals && index == prepared.EntryPoint
                || FunctionAnalysis.LiteralBody(function) ? function : Evaluate(Graph(function, index))).ToArray();
            var reduced = CompactLocals(prepared with { Constants = constants, Functions = functions }, profile);
            for (var index = 0; index < reduced.Functions.Count; index++)
                graphs[index][reduced.Functions[index]] =
                    (OptimizationDependencies.Capture(reduced.Functions[index], reduced, effects), reduced.Functions[index]);
            var specialized = reduced;
            if (level >= 2)
            {
                var start = profile?.Start() ?? default;
                specialized = InterproceduralSpecialization.Optimize(reduced, level);
                profile?.Add(OptimizationProfile.Pass.Interprocedural, start, reduced, specialized);
                var direct = specialized;
                start = profile?.Start() ?? default;
                specialized = AggregateArgumentSpecialization.Optimize(direct, level);
                profile?.Add(OptimizationProfile.Pass.AggregateArguments, start, direct, specialized);
            }
            var inlineStart = profile?.Start() ?? default;
            var inlined = FunctionInlining.Inline(specialized, level);
            profile?.Add(OptimizationProfile.Pass.Inlining, inlineStart, specialized, inlined);
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
                var numberedEffects = MeasureEffects(numbered, profile);
                var cleaned = numbered with { Functions = numbered.Functions.Select(Clean).ToArray(), Constants = numberedConstants };
                return Compact(cleaned, profile);

                FunctionCode Clean(FunctionCode function, int index)
                {
                    if (numbered.PreserveLocals && index == numbered.EntryPoint || FunctionAnalysis.LiteralBody(function)) return function;
                    if (graphs[index].TryGetValue(function, out var existing) && existing.Dependencies.Matches(numbered, numberedEffects)) return existing.Result;
                    var start = profile?.Start() ?? default;
                    var result = GraphOptimization.Optimize(numbered, function, numberedConstants, numberedEffects, numberedInterner);
                    profile?.Add(OptimizationProfile.Pass.Graph, start, function, result);
                    return result;
                }

                FunctionCode Number(FunctionCode function)
                {
                    if (FunctionAnalysis.LiteralBody(function)) return function;
                    var features = FunctionAnalysis.Features(function);
                    var start = profile?.Start() ?? default;
                    var normalized = (features & FunctionFeatures.Directional) != 0 ? DirectionalNormalization.Optimize(inlined, function) : function;
                    if ((features & FunctionFeatures.Directional) != 0) profile?.Add(OptimizationProfile.Pass.Directional, start, function, normalized);
                    start = profile?.Start() ?? default;
                    var available = level >= 2 && (features & FunctionFeatures.Branch) != 0 ? GlobalRedundancy.OptimizeWithPool(inlined, normalized, constants, constantInterner) : normalized;
                    if (level >= 2 && (features & FunctionFeatures.Branch) != 0) profile?.Add(OptimizationProfile.Pass.GlobalRedundancy, start, normalized, available);
                    start = profile?.Start() ?? default;
                    var indexed = level >= 2 && (features & FunctionFeatures.IndexedReads) != 0
                        ? IndexedMemoryOptimization.Optimize(available, constants) : available;
                    if (level >= 2 && (features & FunctionFeatures.IndexedReads) != 0) profile?.Add(OptimizationProfile.Pass.IndexedMemory, start, available, indexed);
                    start = profile?.Start() ?? default;
                    var result = ValueNumbering.Eliminate(indexed, constants);
                    profile?.Add(OptimizationProfile.Pass.ValueNumbering, start, indexed, result);
                    return result;
                }
            }
            current = inlined;

            FunctionCode Graph(FunctionCode function, int index)
            {
                if (graphs[index].TryGetValue(function, out var existing) && existing.Dependencies.Matches(prepared, effects)) return existing.Result;
                var start = profile?.Start() ?? default;
                var result = Unchanged(function, GraphOptimization.Optimize(prepared, function, constants, effects, constantInterner));
                profile?.Add(OptimizationProfile.Pass.Graph, start, function, result);
                var dependencies = OptimizationDependencies.Capture(function, prepared, effects);
                graphs[index][function] = (dependencies, result);
                graphs[index][result] = (dependencies, result);
                return result;
            }

            FunctionCode Evaluate(FunctionCode function)
            {
                if (level < 2 || (FunctionAnalysis.Features(function) & FunctionFeatures.Iteration) == 0) return function;
                var start = profile?.Start() ?? default;
                var result = CompileTimeEvaluation.OptimizeWithPool(prepared, function, constants, constantInterner);
                profile?.Add(OptimizationProfile.Pass.Evaluation, start, function, result);
                return result;
            }
        }
    }

    private static Module CompactLocals(Module module, OptimizationProfile? profile)
    {
        var start = profile?.Start() ?? default;
        var result = LocalSlotCompaction.Compact(module);
        profile?.Add(OptimizationProfile.Pass.Locals, start, module, result);
        return result;
    }

    private static Module Compact(Module module, OptimizationProfile? profile)
    {
        var locals = CompactLocals(module, profile);
        var start = profile?.Start() ?? default;
        var result = ModuleOptimization.Compact(locals);
        profile?.Add(OptimizationProfile.Pass.Compact, start, locals, result);
        return result;
    }

    private static IReadOnlyList<InstructionEffect> MeasureEffects(Module module, OptimizationProfile? profile)
    {
        var start = profile?.Start() ?? default;
        var result = EffectAnalysis.FunctionEffects(module);
        profile?.AddAnalysis(OptimizationProfile.Pass.Effects, start, module);
        return result;
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
