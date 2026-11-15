using Msync.Core;
using Msync.Core.Llvm;
using Msync.Hir;
using Msync.Optimization.Llvm;

namespace Msync.Compiler;

public static partial class Compiler
{
    internal static LlvmHirLoweringResult OptimizeLlvm(LlvmHirLoweringResult graph,
        int optimization, CompilationTrace? trace = null)
    {
        if (optimization is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(optimization));
        if (trace is null) return OptimizeLlvmCore(graph, optimization, null);
        return trace.MeasureDetailed("compiler.optimize", (graph, optimization, trace),
            static state => OptimizeLlvmCore(state.graph, state.optimization, state.trace),
            LlvmSize(graph), LlvmSize,
            attributes: new Dictionary<string, string>
            {
                ["ir.kind"] = "llvm",
                ["optimization.level"] = optimization.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["passes.available"] = "sroa,mem2reg,early-cse,loop-simplify,lcssa,licm,loop-rotate,sccp,dce,instcombine,adce,dse,simplifycfg"
            });
    }

    private static LlvmHirLoweringResult OptimizeLlvmCore(LlvmHirLoweringResult graph,
        int optimization, CompilationTrace? trace)
    {
        if (optimization == 0) return graph;
        for (int index = 0; index < graph.Module.Functions.Count; ++index)
        {
            LlvmFunction function = graph.Module.Functions[index];
            if (function.IsDeclaration) continue;
            if (trace?.Detail is not { } detail) OptimizeLlvmFunction(function, null);
            else detail.Run("optimizer.llvm.function", (function, detail),
                static state => OptimizeLlvmFunction(state.function, state.detail),
                attributes: FunctionAttributes(function));
        }
        return graph;
    }

    private static void OptimizeLlvmFunction(LlvmFunction function, CompilationTrace? trace)
    {
        var tree = new LlvmDominatorTree(function, trace);
        using var assumptions = new LlvmAssumptionCache(function, trace);
        LlvmSroaResult scalarized = LlvmSroa.Run(function, tree, assumptions,
            new() { PreserveCFG = false }, trace);
        bool commoned = LlvmEarlyCse.Run(function, true, trace, tree);
        var loops = new LlvmLoopInfo(function, tree, trace);
        bool canonicalized = LlvmLoopSimplify.Run(function, tree, loops, trace: trace);
        bool closed = LlvmLcssa.Run(function, tree, loops, trace);
        LlvmLoopChanges loopChanges = OptimizeLlvmLoops(function, tree, loops, trace);
        bool propagated = LlvmSccp.Run(function, trace);
        bool eliminated = LlvmDeadCodeElimination.Run(function, trace);
        var cleanupTree = new LlvmDominatorTree(function, trace);
        bool combined = OptimizeLlvmCombine(function, cleanupTree, trace);
        var postDominators = new LlvmDominatorTree(function, trace, isPostDominator: true);
        LlvmAdceChanged aggressivelyEliminated = LlvmAdce.Run(function, postDominators, cleanupTree, trace: trace);
        bool storesEliminated = OptimizeLlvmStores(function, cleanupTree, postDominators, trace);
        bool cfgSimplified = LlvmSimplifyCfgPass.Run(function, new(function.Parent!.DataLayout),
            options: new() { ConvertSwitchRangeToICmp = true, ConvertSwitchToArithmetic = true,
                HoistCommonInsts = true, SinkCommonInsts = true }, trace: trace);
        trace?.Metric("sroa.changed", scalarized.Changed ? 1 : 0);
        trace?.Metric("sroa.control-flow.changed", scalarized.CFGChanged ? 1 : 0);
        trace?.Metric("mem2reg.changed", scalarized.Promoted ? 1 : 0);
        trace?.Metric("early-cse.changed", commoned ? 1 : 0);
        trace?.Metric("loop-simplify.changed", canonicalized ? 1 : 0);
        trace?.Metric("lcssa.changed", closed ? 1 : 0);
        trace?.Metric("licm.changed", loopChanges.Motion ? 1 : 0);
        trace?.Metric("loop-rotate.changed", loopChanges.Rotation ? 1 : 0);
        trace?.Metric("sccp.changed", propagated ? 1 : 0);
        trace?.Metric("dce.changed", eliminated ? 1 : 0);
        trace?.Metric("instcombine.changed", combined ? 1 : 0);
        trace?.Metric("analysis.dominator.reused_after_sroa", 1);
        trace?.Metric("analysis.dominator.reused_for_memory_cleanup", 1);
        trace?.Metric("adce.changed", aggressivelyEliminated.ChangedAnything ? 1 : 0);
        trace?.Metric("adce.control-flow.changed", aggressivelyEliminated.ChangedControlFlow ? 1 : 0);
        trace?.Metric("dse.changed", storesEliminated ? 1 : 0);
        trace?.Metric("simplifycfg.changed", cfgSimplified ? 1 : 0);
    }

    private readonly record struct LlvmLoopChanges(bool Motion, bool Rotation);

    private static LlvmLoopChanges OptimizeLlvmLoops(LlvmFunction function, LlvmDominatorTree tree,
        LlvmLoopInfo loops, CompilationTrace? trace)
    {
        if (loops.TopLevelLoops.Count == 0) return default;
        if (trace?.Detail is not { } detail) return OptimizeLlvmLoopsCore(function, tree, loops, null, null);
        var metrics = new Dictionary<string, ulong>();
        return detail.MeasureDetailed("optimizer.llvm.loop-motion", (function, tree, loops, detail, metrics),
            static state => OptimizeLlvmLoopsCore(state.function, state.tree, state.loops, state.detail, state.metrics),
            attributes: FunctionAttributes(function), metrics: _ => metrics);
    }

    private static LlvmLoopChanges OptimizeLlvmLoopsCore(LlvmFunction function, LlvmDominatorTree tree,
        LlvmLoopInfo loops, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        var alias = new LlvmAAResults(function, tree: tree, metrics: metrics);
        var memory = new LlvmMemorySsa(function, alias, tree, trace, metrics);
        var updater = new LlvmMemorySsaUpdater(memory);
        var library = new LlvmTargetLibraryInfo(function.Parent!);
        var target = new LlvmTargetTransformInfo(function.Parent!.DataLayout, metrics);
        var rotation = new LlvmLoopRotatePass();
        using var assumptions = new LlvmAssumptionCache(function, trace);
        bool moved = false, rotated = false;
        foreach (LlvmLoop loop in loops.GetLoopsInPreorder().Reverse())
        {
            moved |= LlvmLicm.Run(loop, loops, tree, alias, updater, library, target,
                options: new(AllowSpeculation: false), trace: trace);
            rotated |= rotation.Run(loop, loops, target, tree, updater, trace: trace, assumptions: assumptions);
            moved |= LlvmLicm.Run(loop, loops, tree, alias, updater, library, target, trace: trace);
        }
        return new(moved, rotated);
    }

    private static bool OptimizeLlvmCombine(LlvmFunction function, LlvmDominatorTree tree,
        CompilationTrace? trace)
    {
        if (trace?.Detail is not { } detail) return OptimizeLlvmCombineCore(function, tree, null, null);
        var metrics = new Dictionary<string, ulong>();
        return detail.MeasureDetailed("optimizer.llvm.scalar-combine", (function, tree, detail, metrics),
            static state => OptimizeLlvmCombineCore(state.function, state.tree, state.detail, state.metrics),
            attributes: FunctionAttributes(function), metrics: _ => metrics);
    }

    private static bool OptimizeLlvmCombineCore(LlvmFunction function, LlvmDominatorTree tree,
        CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        var alias = new LlvmAAResults(function, tree: tree, metrics: metrics);
        using var assumptions = new LlvmAssumptionCache(function, trace);
        var library = new LlvmTargetLibraryInfo(function.Parent!);
        var target = new LlvmTargetTransformInfo(function.Parent!.DataLayout, metrics);
        return LlvmInstCombine.Run(function, alias, assumptions, library, target, tree, trace: trace);
    }

    private static bool OptimizeLlvmStores(LlvmFunction function, LlvmDominatorTree tree,
        LlvmDominatorTree postDominators, CompilationTrace? trace)
    {
        if (trace?.Detail is not { } detail) return OptimizeLlvmStoresCore(function, tree, postDominators, null, null);
        var metrics = new Dictionary<string, ulong>();
        return detail.MeasureDetailed("optimizer.llvm.memory-cleanup", (function, tree, postDominators, detail, metrics),
            static state => OptimizeLlvmStoresCore(state.function, state.tree, state.postDominators, state.detail, state.metrics),
            attributes: FunctionAttributes(function), metrics: _ => metrics);
    }

    private static bool OptimizeLlvmStoresCore(LlvmFunction function, LlvmDominatorTree tree,
        LlvmDominatorTree postDominators, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        var cycles = new LlvmCycleInfo(function, trace, metrics);
        var alias = new LlvmAAResults(function, tree: tree, metrics: metrics);
        var memory = new LlvmMemorySsa(function, alias, tree, trace, metrics);
        var library = new LlvmTargetLibraryInfo(function.Parent!);
        return LlvmDeadStoreElimination.Run(function, alias, memory, tree, postDominators, library, cycles, trace: trace);
    }

    internal static CompilationSize LlvmSize(LlvmHirLoweringResult graph)
    {
        long functions = 0, instructions = 0, allocations = 0;
        foreach (LlvmFunction function in graph.Module.Functions)
        {
            if (function.IsDeclaration) continue;
            ++functions;
            foreach (LlvmBasicBlock block in function.BasicBlocks)
            foreach (LlvmInstruction instruction in block.Instructions)
            {
                ++instructions;
                if (instruction is LlvmAllocaInstruction) ++allocations;
            }
        }
        return new(functions, instructions, allocations);
    }

    private static Dictionary<string, string> FunctionAttributes(LlvmFunction function)
    {
        var attributes = new Dictionary<string, string> { ["function.name"] = function.Name };
        if (function.BasicBlocks.FirstOrDefault()?.FirstInstruction?.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }
}
