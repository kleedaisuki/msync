// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp
// EarlyCSEPass::run, isSameMemGeneration and removeMSSA; ScalarOptions.td.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    public static bool Run(LlvmFunction function, bool useMemorySsa, CompilationTrace? trace = null,
        LlvmDominatorTree? tree = null, LlvmMemorySsa? memorySsa = null, LlvmAAResults? aa = null,
        uint memorySsaOptimizationCap = 500)
    {
        trace = trace?.Detail;
        tree ??= useMemorySsa ? memorySsa?.DominatorTree ?? new LlvmDominatorTree(function, trace)
            : new LlvmDominatorTree(function, trace);
        var work = new Work(function, tree, trace, useMemorySsa, memorySsa, aa, memorySsaOptimizationCap);
        try
        {
            if (trace is null) return work.Run();
            return trace.MeasureDetailed("optimizer.pass.llvm-early-cse", work, static state => state.Run(),
                input: new CompilationSize(1, work.InstructionCount, function.BasicBlocks.Count),
                size: _ => new CompilationSize(1, work.InstructionCount, function.BasicBlocks.Count),
                attributes: new Dictionary<string, string>
                {
                    ["function.name"] = function.Name,
                    ["memory-ssa"] = useMemorySsa ? "enabled" : "disabled",
                    ["memory-ssa.optimization-cap"] = memorySsaOptimizationCap.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, metrics: _ => work.Metrics!);
        }
        finally { work.DisposeOwnedMemorySsa(); }
    }

    private sealed partial class Work
    {
        private readonly LlvmMemorySsa? memorySsa;
        private readonly LlvmMemorySsaUpdater? memorySsaUpdater;
        private readonly bool ownsMemorySsa;
        private readonly uint memorySsaOptimizationCap = 500;
        private uint clobberCounter;

        internal Work(LlvmFunction function, LlvmDominatorTree tree, CompilationTrace? trace, bool useMemorySsa,
            LlvmMemorySsa? memorySsa, LlvmAAResults? aa, uint memorySsaOptimizationCap) : this(function, tree, trace)
        {
            this.memorySsaOptimizationCap = memorySsaOptimizationCap;
            if (!useMemorySsa) return;
            if (memorySsa is null)
            {
                aa ??= new LlvmAAResults(function, Layout, tree, Metrics);
                memorySsa = new LlvmMemorySsa(function, aa, tree, trace, Metrics);
                ownsMemorySsa = true;
            }
            this.memorySsa = memorySsa;
            memorySsaUpdater = new(memorySsa);
        }

        internal void DisposeOwnedMemorySsa()
        { if (ownsMemorySsa) memorySsa!.Dispose(); }

        private bool IsSameMemoryGeneration(uint earlierGeneration, uint laterGeneration,
            LlvmInstruction earlier, LlvmInstruction later)
        {
            Count("memory_generation.queries");
            if (earlierGeneration == laterGeneration)
            { Count("memory_generation.same_generation"); return true; }
            if (memorySsa is null)
            { Count("memory_generation.no_memoryssa"); return false; }
            if (trace is null) return IsSameMemoryGenerationWithSsa(earlier, later);
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["block.name"] = later.Parent!.Name,
                ["earlier.instruction.name"] = earlier.Name,
                ["later.instruction.name"] = later.Name,
                ["earlier.generation"] = earlierGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["later.generation"] = laterGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (later.Source is { } source)
            {
                attributes["source.name"] = source.Source;
                attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return trace.MeasureDetailed("optimizer.llvm-early-cse.memory-generation", (Self: this, Earlier: earlier, Later: later),
                static state => state.Self.IsSameMemoryGenerationWithSsa(state.Earlier, state.Later), attributes: attributes);
        }

        private bool IsSameMemoryGenerationWithSsa(LlvmInstruction earlier, LlvmInstruction later)
        {
            var earlierAccess = memorySsa!.GetMemoryAccess(earlier);
            if (earlierAccess is null) { Count("memory_generation.earlier_without_access"); return true; }
            var laterAccess = memorySsa.GetMemoryAccess(later);
            if (laterAccess is null) { Count("memory_generation.later_without_access"); return true; }
            LlvmMemoryAccess definition;
            if (clobberCounter < memorySsaOptimizationCap)
            {
                Count("memory_generation.walker_query");
                definition = memorySsa.GetWalker().GetClobberingMemoryAccess(later, Metrics);
                ++clobberCounter;
            }
            else
            {
                Count("memory_generation.optimization_cap");
                definition = laterAccess.DefiningAccess!;
            }
            bool dominates = memorySsa.Dominates(definition, earlierAccess);
            Count(dominates ? "memory_generation.clobber_dominates_earlier" : "memory_generation.intervening_clobber");
            return dominates;
        }

        private void RemoveMemorySsa(LlvmInstruction instruction)
        {
            if (memorySsaUpdater is null) return;
            if (Metrics is not null)
                Count(memorySsa!.GetMemoryAccess(instruction) is null ? "memoryssa.remove.no_access" : "memoryssa.remove.access");
            memorySsaUpdater.RemoveMemoryAccess(instruction, optimizePhis: true);
        }
    }
}
