// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MustExecute.h/.cpp
// LoopSafetyInfo and allLoopPathsLeadToBlock, collectTransitivePredecessors,
// CanProveNotTakenFirstIteration. This is loop-execution analysis, not validation.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public abstract class LlvmLoopSafetyInfo
{
    protected readonly LlvmLoop CurrentLoop;
    protected readonly Dictionary<string, ulong>? Metrics;
    private readonly Dictionary<LlvmBasicBlock, bool> guaranteedToExecute = new(ReferenceEqualityComparer.Instance);
    private Dictionary<LlvmBasicBlock, IReadOnlyList<LlvmBasicBlock>>? blockColors;

    protected LlvmLoopSafetyInfo(LlvmLoop loop, Dictionary<string, ulong>? metrics = null) => (CurrentLoop, Metrics) = (loop, metrics);
    public abstract bool BlockMayThrow(LlvmBasicBlock block);
    public abstract bool AnyBlockMayThrow();
    public abstract bool IsGuaranteedToExecute(LlvmInstruction instruction, LlvmDominatorTree dominators);

    public IReadOnlyDictionary<LlvmBasicBlock, IReadOnlyList<LlvmBasicBlock>> GetBlockColors() =>
        blockColors ??= new(ReferenceEqualityComparer.Instance);

    public void CopyColors(LlvmBasicBlock newBlock, LlvmBasicBlock oldBlock)
    {
        if (blockColors is null) return;
        if (!blockColors.TryGetValue(oldBlock, out var colors)) blockColors[oldBlock] = colors = Array.Empty<LlvmBasicBlock>();
        blockColors[newBlock] = colors;
    }

    public bool AllLoopPathsLeadToBlock(LlvmBasicBlock block, LlvmDominatorTree dominators)
    {
        Debug.Assert(CurrentLoop.Contains(block));
        Count("loop_execution.paths.queries");
        if (block == CurrentLoop.Header) return true;
        if (guaranteedToExecute.TryGetValue(block, out bool known))
        { Count("loop_execution.paths.cache_hit"); return known; }
        bool result = AllLoopPathsLeadToBlockImpl(block, dominators);
        guaranteedToExecute.Add(block, result);
        Count(result ? "loop_execution.paths.guaranteed" : "loop_execution.paths.not_guaranteed");
        return result;
    }

    private bool AllLoopPathsLeadToBlockImpl(LlvmBasicBlock block, LlvmDominatorTree dominators)
    {
        HashSet<LlvmBasicBlock> predecessors = new(ReferenceEqualityComparer.Instance);
        CollectTransitivePredecessors(block, predecessors);
        foreach (var predecessor in LlvmDominatorTree.Predecessors(CurrentLoop.Header))
            if (predecessors.Contains(predecessor))
            { Count("loop_execution.paths.backedge"); return false; }
        HashSet<LlvmBasicBlock> checkedSuccessors = new(ReferenceEqualityComparer.Instance);
        foreach (var predecessor in predecessors)
        {
            if (BlockMayThrow(predecessor))
            { Count("loop_execution.paths.implicit_exit"); return false; }
            if (dominators.Dominates(block, predecessor)) continue;
            foreach (var successor in LlvmDominatorTree.Successors(predecessor))
            {
                Count("loop_execution.paths.successors");
                if (!checkedSuccessors.Add(successor) || successor == block || predecessors.Contains(successor)) continue;
                if (!CurrentLoop.Contains(successor) && CanProveNotTakenFirstIteration(successor, dominators)) continue;
                Count("loop_execution.paths.diverging_successor");
                return false;
            }
        }
        return true;
    }

    protected void CollectTransitivePredecessors(LlvmBasicBlock block, HashSet<LlvmBasicBlock> predecessors)
    {
        Debug.Assert(predecessors.Count == 0 && CurrentLoop.Contains(block));
        if (block == CurrentLoop.Header) return;
        List<LlvmBasicBlock> work = [];
        foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
        {
            if (!CurrentLoop.Contains(predecessor)) continue;
            predecessors.Add(predecessor);
            work.Add(predecessor);
        }
        while (work.Count != 0)
        {
            var predecessor = work[^1]; work.RemoveAt(work.Count - 1);
            Count("loop_execution.paths.predecessors");
            if (predecessor == CurrentLoop.Header) continue;
            foreach (var earlier in LlvmDominatorTree.Predecessors(predecessor))
                if (CurrentLoop.Contains(earlier) && predecessors.Add(earlier)) work.Add(earlier);
        }
    }

    private bool CanProveNotTakenFirstIteration(LlvmBasicBlock exit, LlvmDominatorTree dominators)
    {
        Count("loop_execution.first_iteration.queries");
        using var predecessors = LlvmDominatorTree.Predecessors(exit).GetEnumerator();
        if (!predecessors.MoveNext()) return false;
        var exiting = predecessors.Current;
        if (predecessors.MoveNext() || exiting.GetTerminatorOrNull() is not LlvmCondBrInstruction branch) return false;
        if (branch.Condition is LlvmConstantInt constant) return branch.GetSuccessor(constant.IsZero ? 0 : 1) == exit;
        if (branch.Condition is not LlvmCmpInstruction compare) return false;
        LlvmCmpPredicate predicate = compare.Predicate;
        var phi = compare.Left as LlvmPhiInstruction;
        var rhs = compare.Right;
        if (phi?.Parent != CurrentLoop.Header)
        {
            predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            phi = compare.Right as LlvmPhiInstruction;
            rhs = compare.Left;
            if (phi?.Parent != CurrentLoop.Header) return false;
        }
        LlvmBasicBlock? predecessor = CurrentLoop.GetLoopPredecessor();
        if (predecessor is null) return false;
        var start = phi!.GetIncomingValue(phi.GetBasicBlockIndex(predecessor));
        var layout = exit.Module!.DataLayout;
        var simplified = (byte)predicate >= 32
            ? LlvmInstructionSimplify.SimplifyICmp(predicate, start, rhs, layout, branch, dominators, Metrics)
            : LlvmInstructionSimplify.SimplifyFCmp(predicate, start, rhs, LlvmFastMathFlags.None, layout, branch, dominators, Metrics);
        if (simplified is not LlvmConstantInt result) return false;
        bool notTaken = exit == branch.GetSuccessor(0) ? result.IsZero : result.IsMinusOne;
        if (notTaken) Count("loop_execution.first_iteration.exit_not_taken");
        return notTaken;
    }

    protected void Count(string name)
    { if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1; }
}
