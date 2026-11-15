// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// GenericDomTreeConstruction.h: FindRoots / RemoveRedundantRoots.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private sealed partial class Construction
    {
        private bool HasForwardSuccessors(LlvmBasicBlock block) => batch is null ? block.GetTerminator().SuccessorCount != 0 : batch.PreView.GetChildren(block).Count != 0;
        internal void FindTreeRoots()
        {
            if (!Tree.IsPostDominator)
            {
                Tree.Roots = Array.AsReadOnly(new[] { Tree.Function.BasicBlocks[0] });
                Count("roots.entry");
                return;
            }
            var finder = new Construction(Tree, trace, Metrics, batch);
            var roots = new List<LlvmBasicBlock>();
            finder.AddVirtualRoot();
            int number = 1;
            foreach (var block in Tree.Function.BasicBlocks)
            {
                Count("roots.blocks.scanned");
                if (HasForwardSuccessors(block)) continue;
                roots.Add(block);
                number = finder.RunDFS(block, number, 0);
                Count("roots.trivial");
            }
            bool hasNonTrivialRoots = Tree.Function.BasicBlocks.Count + 1 != number;
            Dictionary<LlvmBasicBlock, int>? successorOrder = null;
            if (hasNonTrivialRoots)
            {
                foreach (var block in Tree.Function.BasicBlocks)
                {
                    if (finder.GetInfo(block).DFSNumPlus1 != 0) continue;
                    successorOrder ??= finder.CreateSuccessorOrder();
                    int newNumber = finder.RunDFS(block, number, number, reverse: true, successorOrder);
                    LlvmBasicBlock furthestAway = finder.numToNode[newNumber - 1]!;
                    roots.Add(furthestAway);
                    Count("roots.nontrivial_candidates");
                    for (int index = newNumber; index-- > number;)
                    {
                        finder.infos[finder.numToNode[index]!] = new();
                        finder.numToNode.RemoveAt(finder.numToNode.Count - 1);
                        Count("roots.forward_dfs_nodes.reset");
                    }
                    number = finder.RunDFS(furthestAway, number, 0);
                }
            }
            Debug.Assert(Tree.Function.BasicBlocks.Count + 1 == number);
            if (hasNonTrivialRoots) finder.RemoveRedundantRoots(roots);
            Tree.Roots = roots.AsReadOnly();
            Count("roots.selected", (ulong)roots.Count);
        }
        private Dictionary<LlvmBasicBlock, int> CreateSuccessorOrder()
        {
            var order = new Dictionary<LlvmBasicBlock, int>(ReferenceEqualityComparer.Instance);
            foreach (var block in Tree.Function.BasicBlocks)
            {
                if (GetInfo(block).DFSNumPlus1 != 0) continue;
                foreach (var successor in Children(block, inverse: false)) order.TryAdd(successor, 0);
            }
            int number = 0;
            foreach (var block in Tree.Function.BasicBlocks)
            {
                ++number;
                if (order.ContainsKey(block)) order[block] = number;
            }
            Count("roots.successor_order.entries", (ulong)order.Count);
            return order;
        }
        internal void Clear()
        {
            numToNode.Clear();
            foreach (var info in infos.Values)
                info.DFSNumPlus1 = info.Parent = info.Semi = info.Label = info.IDom = info.ReverseChildrenStart = 0;
            virtualRoot.DFSNumPlus1 = virtualRoot.Parent = virtualRoot.Semi = virtualRoot.Label
                = virtualRoot.IDom = virtualRoot.ReverseChildrenStart = 0;
            reverseChildren.Clear();
        }
        private void RemoveRedundantRoots(List<LlvmBasicBlock> roots)
        {
            Clear();
            for (int index = 0; index < roots.Count; ++index)
            {
                LlvmBasicBlock root = roots[index];
                if (!HasForwardSuccessors(root)) continue;
                Clear();
                int number = RunDFS(root, 0, 0, reverse: true);
                Count("roots.redundancy_checks");
                for (int node = 1; node < number; ++node)
                {
                    if (!roots.Contains(numToNode[node]!)) continue;
                    roots[index] = roots[^1];
                    roots.RemoveAt(roots.Count - 1);
                    --index;
                    Count("roots.redundant_removed");
                    break;
                }
            }
        }
    }
}
