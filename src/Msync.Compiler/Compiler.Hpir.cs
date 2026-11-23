using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Hir;
using Msync.Optimization.Llvm;

namespace Msync.Compiler;

public static partial class Compiler
{
    internal static HpirProgram LowerHpir(HirModule module, int optimization, CompilationTrace? trace = null) =>
        CodegenHpir(OptimizeLlvm(LowerLlvm(module, trace), optimization, trace), optimization, trace);

    internal static HpirProgram CodegenHpir(LlvmHirLoweringResult graph, int optimization, CompilationTrace? trace)
    {
        PrepareOptimizedHpirGraph(graph, optimization, trace);
        return LlvmHpirLowerer.Lower(graph, trace) with { OptimizationLevel = optimization };
    }

    internal static void PrepareOptimizedHpirGraph(LlvmHirLoweringResult graph, int optimization, CompilationTrace? trace)
    {
        if (optimization == 0)
        {
            // HPIR consumes live SSA values, not bookkeeping for discarded results.
            // The optimizing pipeline already runs SROA and dead-code elimination.
            if (trace?.Detail is not { } detail) CanonicalizeValueLocals(graph.Module, null);
            else detail.Run("compiler.hpir.canonicalize.values", (graph.Module, detail),
                static state => CanonicalizeValueLocals(state.Module, state.detail));
        }
    }

    private static void CanonicalizeValueLocals(LlvmModule module, CompilationTrace? trace)
    {
        int visited = 0, promoted = 0, pruned = 0;
        foreach (var function in module.Functions)
        {
            if (function.IsDeclaration) continue;
            if (function.BasicBlocks[0].Instructions.Any(static instruction => instruction is LlvmAllocaInstruction))
            {
                ++visited;
                if (LlvmPromoteMemoryToRegister.Run(function, new(function, trace), trace)) ++promoted;
            }
            if (LlvmDeadCodeElimination.Run(function, trace)) ++pruned;
        }
        trace?.Metric("functions.with_local_storage", visited);
        trace?.Metric("functions.promoted", promoted);
        trace?.Metric("functions.pruned", pruned);
    }

    internal static LlvmHirLoweringResult LowerLlvm(HirModule module, CompilationTrace? trace) =>
        trace is null ? LlvmHirLowerer.Lower(module)
            : trace.MeasureDetailed("compiler.lower", (module, trace),
                static state => LlvmHirLowerer.Lower(state.module, state.trace),
                size: static result => LlvmSize(result));
}
