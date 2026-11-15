// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericDomTree.h: DominatorTreeBase::splitBlock/Split.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    public void SplitBlock(LlvmBasicBlock newBlock, Dictionary<string, ulong>? metrics = null)
    {
        using var successors = SplitChildren(newBlock).GetEnumerator();
        bool hasSuccessor = successors.MoveNext();
        Debug.Assert(hasSuccessor, "Split block must have one successor");
        var successor = successors.Current;
        Debug.Assert(!successors.MoveNext(), "Split block must have one successor");
        var predecessors = SplitInverseChildren(newBlock).ToList();
        Debug.Assert(predecessors.Count != 0, "Split block has no predecessors");
        CountMutation(metrics, "domtree.mutation.split.queries");

        bool dominatesSuccessor = true;
        foreach (var predecessor in SplitInverseChildren(successor))
        {
            CountMutation(metrics, "domtree.mutation.split.successor_predecessors");
            if (ReferenceEquals(predecessor, newBlock) || Dominates(successor, predecessor) || !IsReachableFromEntry(predecessor)) continue;
            dominatesSuccessor = false;
            break;
        }
        LlvmBasicBlock? idom = null;
        int index = 0;
        for (; index < predecessors.Count; ++index)
        {
            CountMutation(metrics, "domtree.mutation.split.predecessors");
            if (!IsReachableFromEntry(predecessors[index])) continue;
            idom = predecessors[index];
            break;
        }
        if (idom is null)
        {
            CountMutation(metrics, "domtree.mutation.split.unreachable");
            return;
        }
        for (++index; index < predecessors.Count; ++index)
        {
            CountMutation(metrics, "domtree.mutation.split.predecessors");
            if (IsReachableFromEntry(predecessors[index])) idom = FindNearestCommonDominator(idom, predecessors[index], metrics)!;
        }
        var node = AddNewBlock(newBlock, idom, metrics);
        if (dominatesSuccessor) ChangeImmediateDominator(GetNode(successor)!, node, metrics);
        CountMutation(metrics, dominatesSuccessor ? "domtree.mutation.split.successor_reparented" : "domtree.mutation.split.successor_unchanged");
    }

    private IEnumerable<LlvmBasicBlock> SplitChildren(LlvmBasicBlock block) =>
        IsPostDominator ? Predecessors(block) : Successors(block);
    private IEnumerable<LlvmBasicBlock> SplitInverseChildren(LlvmBasicBlock block) =>
        IsPostDominator ? Successors(block) : Predecessors(block);
}
