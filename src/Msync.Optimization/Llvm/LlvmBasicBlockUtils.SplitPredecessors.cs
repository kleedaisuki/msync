// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// BasicBlockUtils.cpp SplitBlockPredecessors wrappers; shared implementation
// is the same represented source body used by LoopSimplify.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmBasicBlockUtils
{
    public static LlvmBasicBlock SplitBlockPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, LlvmDomTreeUpdater? updater,
        LlvmLoopInfo? loops = null, LlvmMemorySsaUpdater? memoryUpdater = null,
        bool preserveLcssa = false, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        var split = LlvmLoopSimplify.SplitPredecessors(block, predecessors, suffix, null,
            loops, preserveLcssa, trace?.Detail, memoryUpdater, updater);
        if (metrics is not null)
        {
            metrics["cfg.split_predecessors.blocks"] = metrics.GetValueOrDefault("cfg.split_predecessors.blocks") + 1;
            metrics["cfg.split_predecessors.predecessors"] = metrics.GetValueOrDefault("cfg.split_predecessors.predecessors") + (ulong)predecessors.Count;
        }
        return split;
    }

    public static LlvmBasicBlock SplitBlockPredecessors(LlvmBasicBlock block,
        IReadOnlyList<LlvmBasicBlock> predecessors, string suffix, LlvmDominatorTree? tree = null,
        LlvmLoopInfo? loops = null, LlvmMemorySsaUpdater? memoryUpdater = null,
        bool preserveLcssa = false, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        var split = LlvmLoopSimplify.SplitPredecessors(block, predecessors, suffix, tree,
            loops, preserveLcssa, trace?.Detail, memoryUpdater);
        if (metrics is not null)
        {
            metrics["cfg.split_predecessors.blocks"] = metrics.GetValueOrDefault("cfg.split_predecessors.blocks") + 1;
            metrics["cfg.split_predecessors.predecessors"] = metrics.GetValueOrDefault("cfg.split_predecessors.predecessors") + (ulong)predecessors.Count;
        }
        return split;
    }
}
