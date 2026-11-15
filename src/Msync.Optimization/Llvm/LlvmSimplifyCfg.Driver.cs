// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFGOpt::simplifyOnce/run and llvm::simplifyCFG.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    public static bool SimplifyCFG(LlvmBasicBlock block, LlvmTargetTransformInfo targetInfo,
        LlvmDomTreeUpdater? updater = null, LlvmSimplifyCfgOptions? options = null,
        IReadOnlyList<LlvmWeakValueHandle>? loopHeaders = null, CompilationTrace? trace = null,
        LlvmSimplifyCfgTuning? tuning = null)
    {
        trace = trace?.Detail;
        var work = new Work(block.Module!.DataLayout, targetInfo, updater, options ?? new(),
            loopHeaders ?? [], trace, tuning ?? new());
        return trace is null ? work.Run(block) : trace.MeasureDetailed("optimizer.llvm-simplifycfg.block",
            (Work: work, Block: block), static state => state.Work.Run(state.Block),
            attributes: Attributes(block.GetTerminator()), metrics: _ => work.Metrics!);
    }

    internal sealed partial class Work(LlvmDataLayout layout, LlvmTargetTransformInfo targetInfo,
        LlvmDomTreeUpdater? updater, LlvmSimplifyCfgOptions options, IReadOnlyList<LlvmWeakValueHandle> loopHeaders,
        CompilationTrace? trace, LlvmSimplifyCfgTuning tuning)
    {
        internal readonly LlvmDataLayout Layout = layout;
        internal readonly LlvmTargetTransformInfo TargetInfo = targetInfo;
        internal readonly LlvmDomTreeUpdater? DominatorUpdater = updater;
        internal readonly LlvmSimplifyCfgOptions Options = options;
        internal readonly IReadOnlyList<LlvmWeakValueHandle> LoopHeaders = loopHeaders;
        internal readonly CompilationTrace? Trace = trace;
        internal readonly LlvmSimplifyCfgTuning Tuning = tuning;
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        private bool resimplify;

        internal void Count(string key, ulong amount = 1)
        {
            if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + amount;
        }
        private bool IsLoopHeader(LlvmBasicBlock block) => LoopHeaders.Any(handle => ReferenceEquals(handle.Value, block));
        private bool RequestResimplify() { resimplify = true; Count("resimplifications.requested"); return true; }
        internal bool Run(LlvmBasicBlock block)
        {
            bool changed = false;
            do { resimplify = false; Count("iterations"); changed |= SimplifyOnce(block); } while (resimplify);
            return changed;
        }
        private bool SimplifyOnce(LlvmBasicBlock block)
        {
            Count("blocks.visited");
            if ((!LlvmDominatorTree.Predecessors(block).Any() && block != block.Parent!.BasicBlocks[0]) ||
                LlvmBasicBlockUtils.GetSinglePredecessor(block) == block)
            {
                Count("blocks.deleted-unreachable");
                LlvmBasicBlockUtils.DeleteDeadBlock(block, DominatorUpdater, trace: Trace, metrics: Metrics);
                return true;
            }
            bool changed = LlvmLocalCfg.ConstantFoldTerminator(block, true, DominatorUpdater, Metrics);
            changed |= LlvmBasicBlockUtils.EliminateDuplicatePHINodes(block, Trace, Metrics);
            if (RemoveUndefIntroducingPredecessor(block)) return RequestResimplify();
            if (LlvmBasicBlockUtils.MergeBlockIntoPredecessor(block, DominatorUpdater, trace: Trace, metrics: Metrics))
            { Count("blocks.merged"); return true; }
            if (Tuning.SinkCommon && Options.SinkCommonInsts)
            {
                if (SinkCommonCodeFromPredecessors(block)) return true;
                if (SimplifyDuplicatePredecessors(block)) return true;
            }
            if (Options.SpeculateBlocks && block.FirstInstruction is LlvmPhiInstruction { IncomingCount: 2 } phi &&
                FoldTwoEntryPhi(phi)) return true;
            LlvmInstruction terminator = block.GetTerminator();
            var builder = new LlvmTransformBuilder(terminator, Metrics) { CurrentSource = terminator.Source };
            return (terminator switch
            {
                LlvmUncondBrInstruction branch => SimplifyUncondBranch(branch, builder),
                LlvmCondBrInstruction branch => SimplifyCondBranch(branch, builder),
                LlvmSwitchInstruction @switch => SimplifySwitch(@switch, builder),
                LlvmUnreachableInstruction unreachable => SimplifyUnreachable(unreachable),
                _ => false
            }) | changed;
        }
        private static LlvmInstruction FirstNonPhi(LlvmBasicBlock block)
        {
            LlvmInstruction instruction = block.FirstInstruction!;
            while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
            return instruction;
        }
        private static LlvmBasicBlock? AllPredecessorsSameSource(LlvmBasicBlock block)
        {
            LlvmBasicBlock? common = null;
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block))
            {
                LlvmBasicBlock? source = LlvmBasicBlockUtils.GetSinglePredecessor(predecessor);
                if (source is null || common is not null && common != source) return null;
                common = source;
            }
            return common;
        }
    }
}
