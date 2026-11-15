// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/DeadStoreElimination.cpp and ScalarOptions.td.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed record LlvmDseOptions(uint MaxDomConditionDepth = 1024, uint DefinitionsLimit = 5000,
    uint OtherBlockCost = 5, uint PartialStoreLimit = 5, uint PathCheckLimit = 50,
    uint SameBlockCost = 1, uint ScanLimit = 150, uint WalkLimit = 90,
    bool OptimizeMemorySsa = true, bool InitializesAttributeImprovement = true,
    bool PartialOverwriteTracking = true, bool PartialStoreMerging = true);

public static partial class LlvmDeadStoreElimination
{
    public static bool Run(LlvmFunction function, LlvmAAResults alias, LlvmMemorySsa memorySsa,
        LlvmDominatorTree dominators, LlvmDominatorTree postDominators, LlvmTargetLibraryInfo library,
        LlvmCycleInfo cycles, LlvmDseOptions? options = null, CompilationTrace? trace = null)
    {
        var state = new State(function, alias, memorySsa, dominators, postDominators, library, cycles,
            options ?? new(), trace?.Detail);
        if (trace?.Detail is not { } detail) return state.Run();
        return detail.MeasureDetailed("optimizer.pass.llvm-dse", state, static work => work.Run(),
            input: new CompilationSize(1, function.BasicBlocks.Sum(block => block.Instructions.Count), 0),
            size: _ => new CompilationSize(1, function.BasicBlocks.Sum(block => block.Instructions.Count), 0),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name }, metrics: _ => state.Metrics!);
    }
    private enum Overwrite { Begin, Complete, End, PartialEarlierWithFullLater, MaybePartial, None, Unknown }
    private sealed partial class State
    {
        private readonly LlvmFunction function;
        private readonly LlvmMemorySsa memory;
        private readonly LlvmMemorySsaUpdater updater;
        private readonly LlvmDominatorTree dominators, postDominators;
        private readonly LlvmCycleInfo cycles;
        private readonly LlvmTargetLibraryInfo library;
        private readonly LlvmDataLayout layout;
        private readonly LlvmDseOptions options;
        private readonly CompilationTrace? trace;
        private readonly LlvmEarliestEscapeAnalysis escapes;
        private readonly LlvmBatchAAResults alias;
        private readonly List<LlvmMemoryDef> definitions = [];
        private readonly HashSet<LlvmMemoryAccess> skipped = [];
        private readonly Dictionary<LlvmValue, bool> capturedBeforeReturn = [];
        private readonly Dictionary<LlvmValue, bool> invisibleAfterReturn = [];
        private readonly HashSet<LlvmBasicBlock> throwingBlocks = [];
        private readonly Dictionary<LlvmBasicBlock, uint> postOrder = [];
        private readonly List<LlvmInstruction> toRemove = [];
        private bool anyUnreachableExit, iterateEndOfFunction;
        internal readonly Dictionary<string, ulong>? Metrics;
        internal State(LlvmFunction function, LlvmAAResults aa, LlvmMemorySsa memory, LlvmDominatorTree dominators,
            LlvmDominatorTree postDominators, LlvmTargetLibraryInfo library, LlvmCycleInfo cycles,
            LlvmDseOptions options, CompilationTrace? trace)
        {
            (this.function, this.memory, this.dominators, this.postDominators, this.library, this.cycles, this.options, this.trace)
                = (function, memory, dominators, postDominators, library, cycles, options, trace);
            layout = function.Parent!.DataLayout;
            Metrics = trace is null ? null : [];
            updater = new(memory);
            escapes = new(dominators, loops: null, cycles: cycles, metrics: Metrics);
            alias = new(aa, escapes);
        }
        internal bool Run()
        {
            Phase("collect", Collect);
            bool changed = Phase("eliminate-definitions", EliminateDefinitions);
            if (options.PartialOverwriteTracking) changed |= Phase("partial-overwrites", RemovePartialOverlaps);
            changed |= Phase("redundant-stores", EliminateRedundantExistingValues);
            changed |= Phase("end-of-function", EliminateAtEndOfFunction);
            changed |= Phase("dominating-conditions", EliminateViaDominatingConditions);
            Phase("erase-delayed", () => { while (toRemove.Count != 0) { var instruction = toRemove[^1]; toRemove.RemoveAt(toRemove.Count - 1); instruction.EraseFromParent(); } });
            return changed;
        }
        private void Collect()
        {
            uint number = 0;
            foreach (var block in PostOrderBlocks())
            {
                postOrder.Add(block, number++);
                foreach (var instruction in block.Instructions)
                {
                    var access = memory.GetMemoryAccess(instruction);
                    if (instruction.MayThrow() && access is null) throwingBlocks.Add(block);
                    // No coroutine-end/dead-on-return argument attributes are represented.
                    if (access is LlvmMemoryDef definition && definitions.Count < options.DefinitionsLimit && GetWriteLocation(instruction) is not null)
                        definitions.Add(definition);
                    Count("instructions.collected");
                }
            }
            anyUnreachableExit = postDominators.Roots.Any(block => block.GetTerminator() is LlvmUnreachableInstruction);
            Count("definitions.collected", (ulong)definitions.Count);
        }
        private IEnumerable<LlvmBasicBlock> PostOrderBlocks()
        {
            var seen = new HashSet<LlvmBasicBlock> { function.BasicBlocks[0] };
            var work = new List<(LlvmBasicBlock Block, int Next)> { (function.BasicBlocks[0], 0) };
            while (work.Count != 0)
            {
                var (block, next) = work[^1];
                var terminator = block.GetTerminator();
                if (next == terminator.SuccessorCount) { work.RemoveAt(work.Count - 1); yield return block; continue; }
                work[^1] = (block, next + 1);
                var successor = terminator.GetSuccessor(next);
                if (seen.Add(successor)) work.Add((successor, 0));
            }
        }
        private void Count(string name, ulong value = 1)
        {
            if (Metrics is not null) Metrics["dse." + name] = Metrics.GetValueOrDefault("dse." + name) + value;
        }
        private void Phase(string name, Action action)
        { if (trace is null) action(); else trace.Run("optimizer.llvm-dse." + name, action, static run => run()); }
        private bool Phase(string name, Func<bool> action) => trace is null ? action()
            : trace.Measure("optimizer.llvm-dse." + name, action, static run => run());
    }
}
