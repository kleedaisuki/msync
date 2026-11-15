// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// GenericDomTreeConstruction.h attachNewSubtree and reattachExistingSubtree.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private sealed partial class Construction
    {
        internal IReadOnlyList<LlvmBasicBlock?> NumToNode => numToNode;
        internal void AttachNewSubtree()
        {
            for (int number = 1; number < numToNode.Count; ++number)
            {
                var block = numToNode[number]!;
                Debug.Assert(Tree.GetNode(block) is null);
                Tree.nodes.Add(block, new(block, Tree.GetNode(numToNode[GetInfo(block).IDom])));
                Count("dynamic.subtree.nodes.created");
            }
            for (int number = numToNode.Count; --number > 0;)
            {
                var node = Tree.GetNode(numToNode[number])!;
                node.IDom!.AddChild(node);
                Count("dynamic.subtree.nodes.attached");
            }
            Tree.nodeCapacity = Tree.Function.GetMaxBlockNumber() + (Tree.IsPostDominator ? 1U : 0U);
        }
        internal void ReattachExistingSubtree(LlvmDominatorTreeNode attachTo)
        {
            Reparent(Tree.GetNode(numToNode[0])!, attachTo);
            for (int number = 1; number < numToNode.Count; ++number)
                Reparent(Tree.GetNode(numToNode[number])!, Tree.GetNode(numToNode[GetInfo(numToNode[number]).IDom])!);
        }
        private void Reparent(LlvmDominatorTreeNode node, LlvmDominatorTreeNode idom)
        {
            var (steps, levels) = node.SetIDom(idom);
            Count("dynamic.subtree.reattached");
            Count("dynamic.children.unlink_steps", steps);
            Count("dynamic.levels.updated", levels);
        }
    }
}
