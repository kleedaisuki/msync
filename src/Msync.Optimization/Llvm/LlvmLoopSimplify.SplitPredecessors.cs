// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/BasicBlockUtils.cpp SplitBlockPredecessorsImpl and UpdatePHINodes,
// Direct-DT/DTU/LoopInfo and optional MSSA paths, plus graph-only overloads.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    public static LlvmBasicBlock SplitBlockPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return SplitPredecessors(block, predecessors, suffix, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.split-predecessors",
            (Block: block, Predecessors: predecessors, Suffix: suffix, Trace: trace),
            static state => SplitPredecessors(state.Block, state.Predecessors, state.Suffix, state.Trace),
            attributes: Attributes(block));
    }
    public static LlvmBasicBlock SplitBlockPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, LlvmDominatorTree dominators,
        LlvmLoopInfo loopInfo, bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return SplitPredecessors(block, predecessors, suffix, dominators, loopInfo, preserveLcssa, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.split-predecessors",
            (Block: block, Predecessors: predecessors, Suffix: suffix, Tree: dominators, Loops: loopInfo, Preserve: preserveLcssa, Trace: trace),
            static state => SplitPredecessors(state.Block, state.Predecessors, state.Suffix, state.Tree, state.Loops, state.Preserve, state.Trace),
            attributes: Attributes(block));
    }
    public static LlvmBasicBlock SplitBlockPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, LlvmDominatorTree dominators,
        LlvmLoopInfo loopInfo, LlvmMemorySsaUpdater updater, bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return SplitPredecessors(block, predecessors, suffix, dominators, loopInfo, preserveLcssa, null, updater);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.split-predecessors",
            (Block: block, Predecessors: predecessors, Suffix: suffix, Tree: dominators, Loops: loopInfo, Updater: updater, Preserve: preserveLcssa, Trace: trace),
            static state => SplitPredecessors(state.Block, state.Predecessors, state.Suffix, state.Tree, state.Loops, state.Preserve, state.Trace, state.Updater),
            attributes: Attributes(block));
    }
    private static LlvmBasicBlock SplitPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, CompilationTrace? trace)
        => SplitPredecessors(block, predecessors, suffix, null, null, false, trace);
    internal static LlvmBasicBlock SplitPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, LlvmDominatorTree? tree,
        LlvmLoopInfo? loops, bool preserveLcssa, CompilationTrace? trace, LlvmMemorySsaUpdater? updater = null,
        LlvmDomTreeUpdater? dominatorUpdater = null)
    {
        // The represented terminators have no landing-pad/funclet/indirectbr paths.
        var split = new LlvmBasicBlock(block.Context, block.Name + suffix, block.Parent, block);
        var branch = new LlvmUncondBrInstruction(block) { Source = FirstNonPhi(block).Source };
        split.Append(branch);
        foreach (var predecessor in predecessors)
        {
            predecessor.GetTerminator().ReplaceSuccessorWith(block, split);
            trace?.Metric("cfg.predecessors_redirected", 1);
        }
        if (predecessors.Count == 0)
        {
            foreach (var phi in Phis(block))
            {
                phi.AddIncoming(LlvmPoisonValue.Get(phi.Type), split);
                phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = phi.LogicalType;
                trace?.Metric("phi.poison_inputs_added", 1);
            }
        }
        bool hasLoopExit = UpdateAnalysisInformation(block, split, predecessors, tree, loops, preserveLcssa, trace, updater, dominatorUpdater);
        if (predecessors.Count != 0) UpdatePhis(block, split, predecessors, branch, hasLoopExit, trace);
        trace?.Metric("cfg.blocks_inserted", 1);
        return split;
    }
    private static void UpdatePhis(LlvmBasicBlock original, LlvmBasicBlock split,
        IReadOnlyList<LlvmBasicBlock> predecessors, LlvmInstruction branch, bool hasLoopExit, CompilationTrace? trace)
    {
        var predSet = predecessors.ToHashSet();
        foreach (var phi in Phis(original))
        {
            LlvmValue? common = null;
            if (!hasLoopExit)
            {
                common = phi.GetIncomingValue(phi.GetBasicBlockIndex(predecessors[0]));
                for (int i = 0; i < phi.IncomingCount; ++i)
                {
                    if (!predSet.Contains(phi.GetIncomingBlock(i))) continue;
                    if (common is null) common = phi.GetIncomingValue(i);
                    else if (!ReferenceEquals(common, phi.GetIncomingValue(i))) { common = null; break; }
                }
            }
            if (common is not null)
            {
                for (int i = phi.IncomingCount - 1; i >= 0; --i)
                    if (predSet.Contains(phi.GetIncomingBlock(i))) phi.RemoveIncomingValue(i, false);
                phi.AddIncoming(common, split);
                phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = phi.LogicalType;
                trace?.Metric("phi.common_value_forwarded", 1); continue;
            }
            var joined = new LlvmPhiInstruction(phi.Type, predecessors.Count)
            { LogicalType = phi.LogicalType, Source = phi.Source };
            joined.SetName(phi.Name + ".ph"); split.InsertBefore(joined, branch);
            for (int i = phi.IncomingCount - 1; i >= 0; --i)
            {
                var predecessor = phi.GetIncomingBlock(i);
                if (!predSet.Contains(predecessor)) continue;
                int logical = phi.GetOperandUse(i).LogicalType;
                var value = phi.RemoveIncomingValue(i, false);
                joined.AddIncoming(value, predecessor);
                joined.GetOperandUse(joined.IncomingCount - 1).LogicalType = logical;
                trace?.Metric("phi.inputs_moved", 1);
            }
            phi.AddIncoming(joined, split);
            phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = phi.LogicalType;
            trace?.Metric("phi.created", 1);
        }
    }
    private static LlvmInstruction FirstNonPhi(LlvmBasicBlock block)
    {
        var instruction = block.FirstInstruction!;
        while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
        return instruction;
    }
    private static IEnumerable<LlvmPhiInstruction> Phis(LlvmBasicBlock block)
    {
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            yield return phi;
    }
    private static Dictionary<string, string> Attributes(LlvmBasicBlock block)
    {
        var attributes = new Dictionary<string, string>
        { ["function.name"] = block.Parent!.Name, ["block.name"] = block.Name };
        if (FirstNonPhi(block).Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }
}
