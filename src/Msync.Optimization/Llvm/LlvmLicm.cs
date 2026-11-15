// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LICM.cpp LoopInvariantCodeMotion::runOnLoop, modern MemorySSA path.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    public static bool Run(LlvmLoop loop, LlvmLoopInfo loopInfo, LlvmDominatorTree dominators,
        LlvmAAResults alias, LlvmMemorySsaUpdater updater, LlvmTargetLibraryInfo libraryInfo,
        LlvmTargetTransformInfo targetInfo, LlvmLicmOptions? options = null, CompilationTrace? trace = null,
        LlvmLicmScalarOptions? scalarOptions = null, bool loopNestMode = false)
    {
        trace = trace?.Detail;
        var work = new Work(loop, loopInfo, dominators, alias, updater, libraryInfo, targetInfo,
            options ?? new(), scalarOptions ?? new(), trace, loopNestMode);
        return trace is null ? work.Run() : trace.MeasureDetailed("optimizer.pass.llvm-licm", work,
            static state => state.Run(), attributes: Attributes(loop.Header), metrics: _ => work.Metrics!);
    }

    private static Dictionary<string, string> Attributes(LlvmBasicBlock block)
    {
        var attributes = new Dictionary<string, string>
        { ["function.name"] = block.Parent!.Name, ["loop.header"] = block.Name };
        if (block.FirstInstruction?.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }

    private sealed partial class Work
    {
        private readonly LlvmLoop Loop;
        private readonly LlvmLoopInfo LoopInfo;
        private readonly LlvmDominatorTree Dominators;
        private readonly LlvmAAResults Alias;
        private readonly LlvmMemorySsaUpdater Updater;
        private LlvmMemorySsa Memory => Updater.MemorySsa;
        private readonly LlvmTargetTransformInfo TargetInfo;
        private readonly LlvmLicmOptions Options;
        private readonly LlvmLicmScalarOptions ScalarOptions;
        private readonly CompilationTrace? Trace;
        private readonly bool LoopNestMode;
        private readonly LlvmICFLoopSafetyInfo Safety;
        private readonly Action<LlvmInstruction> erase;
        internal readonly Dictionary<string, ulong>? Metrics;

        internal Work(LlvmLoop loop, LlvmLoopInfo loopInfo, LlvmDominatorTree dominators,
            LlvmAAResults alias, LlvmMemorySsaUpdater updater, LlvmTargetLibraryInfo libraryInfo,
            LlvmTargetTransformInfo targetInfo, LlvmLicmOptions options, LlvmLicmScalarOptions scalarOptions,
            CompilationTrace? trace, bool loopNestMode)
        {
            Loop = loop; LoopInfo = loopInfo; Dominators = dominators; Alias = alias; Updater = updater;
            TargetInfo = targetInfo; Options = options; ScalarOptions = scalarOptions;
            Trace = trace; LoopNestMode = loopNestMode; Metrics = trace is null ? null : [];
            Safety = new(loop, Metrics); erase = EraseInstruction;
        }

        internal bool Run()
        {
            Debug.Assert(Loop.IsLCSSAForm(Dominators));
            // No disable-LICM/coroutine/noalias-scope metadata is represented.
            bool changed = false;
            var flags = new SinkAndHoistFlags(Options.MssaOptCap ?? ScalarOptions.MssaOptimizationCap,
                Options.MssaNoAccForPromotionCap ?? ScalarOptions.MaxAccessesForPromotion, true, Loop, Memory);
            LlvmBasicBlock? preheader = Loop.GetLoopPreheader();
            if (Loop.HasDedicatedExits())
                changed |= LoopNestMode ? SinkLoopNest(flags) : SinkRegion(Loop, flags, null);
            flags.IsSink = false;
            if (preheader is not null) changed |= HoistRegion(flags);
            if (!ScalarOptions.DisablePromotion && preheader is not null && Loop.HasDedicatedExits() && !flags.TooManyMemoryAccesses)
                changed |= PromoteAll();
            Debug.Assert(Loop.IsLCSSAForm(Dominators));
            Debug.Assert(Loop.ParentLoop is null || Loop.ParentLoop.IsLCSSAForm(Dominators));
            return changed;
        }

        private bool PromoteAll()
        {
            if (Trace is null) return PromoteAllCore();
            return Trace.MeasureDetailed("optimizer.llvm-licm.promotion", this, static state => state.PromoteAllCore(),
                attributes: Attributes(Loop.Header));
        }

        private bool PromoteAllCore()
        {
            List<LlvmBasicBlock> exits = [];
            Loop.GetUniqueExitBlocks(exits);
            if (exits.Any(block => block.GetTerminator().Opcode == LlvmOpcode.CatchSwitch)) return false;
            bool promoted = false;
            bool local;
            var predecessors = new Dictionary<LlvmBasicBlock, LlvmBasicBlock[]>();
            do
            {
                Count("promotion.iterations");
                local = false;
                List<LlvmInstruction> insertions = exits.Select(FirstInsertion).ToList();
                List<LlvmMemoryAccess?> memoryInsertions = exits.Select(_ => (LlvmMemoryAccess?)null).ToList();
                foreach (var candidate in CollectPromotionCandidates())
                    local |= PromoteLoopAccesses(candidate.Pointers, exits, insertions, memoryInsertions,
                        predecessors, candidate.HasReadsOutsideSet);
                promoted |= local;
            } while (local);
            if (promoted) LlvmLcssa.FormLCSSARecursively(Loop, Dominators, LoopInfo, Trace);
            return promoted;
        }

        private void Count(string key, ulong amount = 1)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + amount; }
        private bool Retain(string reason)
        { if (Metrics is not null) Count("retained." + reason); return false; }
        private static LlvmInstruction FirstInsertion(LlvmBasicBlock block)
        {
            var instruction = block.FirstInstruction;
            while (instruction is LlvmPhiInstruction) instruction = instruction.Next;
            if (instruction!.IsEHPad) instruction = instruction.Next;
            return instruction!;
        }
    }
}
