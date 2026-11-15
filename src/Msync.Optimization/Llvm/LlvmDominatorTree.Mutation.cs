// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericDomTree.h: mutable DominatorTreeBase primitives.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    public LlvmDominatorTreeNode AddNewBlock(LlvmBasicBlock block, LlvmBasicBlock immediateDominator,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(GetNode(block) is null, "Block already in dominator tree");
        var idom = GetNode(immediateDominator);
        Debug.Assert(idom is not null, "Missing immediate dominator");
        dfsInfoValid = false;
        var node = CreateNode(block, idom);
        CountMutation(metrics, "domtree.mutation.nodes.added");
        return node;
    }

    public LlvmDominatorTreeNode SetNewRoot(LlvmBasicBlock block, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(GetNode(block) is null, "Block already in dominator tree");
        Debug.Assert(!IsPostDominator, "Cannot change root of post-dominator tree");
        dfsInfoValid = false;
        var node = CreateNode(block);
        if (Roots.Count != 0)
        {
            Debug.Assert(Roots.Count == 1);
            var oldRoot = GetNode(Roots[0])!;
            node.AddChild(oldRoot);
            oldRoot.IDom = node;
            CountMutation(metrics, "domtree.mutation.levels.updated", oldRoot.UpdateLevel());
        }
        Roots = Array.AsReadOnly(new[] { block });
        RootNode = node;
        CountMutation(metrics, "domtree.mutation.roots.changed");
        return node;
    }

    public void ChangeImmediateDominator(LlvmDominatorTreeNode node, LlvmDominatorTreeNode newIDom,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(node is not null && newIDom is not null);
        dfsInfoValid = false;
        var (unlinkSteps, levelUpdates) = node.SetIDom(newIDom);
        CountMutation(metrics, "domtree.mutation.idom.changes");
        CountMutation(metrics, "domtree.mutation.children.unlink_steps", unlinkSteps);
        CountMutation(metrics, "domtree.mutation.levels.updated", levelUpdates);
    }

    public void ChangeImmediateDominator(LlvmBasicBlock block, LlvmBasicBlock newIDom,
        Dictionary<string, ulong>? metrics = null) =>
        ChangeImmediateDominator(GetNode(block)!, GetNode(newIDom)!, metrics);

    public void EraseNode(LlvmBasicBlock block, Dictionary<string, ulong>? metrics = null)
    {
        var node = GetNode(block);
        Debug.Assert(node is not null, "Removing node not in dominator tree");
        Debug.Assert(node.IsLeaf, "Node is not a leaf");
        dfsInfoValid = false;
        if (node.IDom is { } idom)
            CountMutation(metrics, "domtree.mutation.children.unlink_steps", idom.RemoveChild(node));
        nodes.Remove(block);
        CountMutation(metrics, "domtree.mutation.nodes.erased");
        if (!IsPostDominator) return;
        var roots = Roots.ToList();
        int index = roots.IndexOf(block);
        if (index < 0) return;
        roots[index] = roots[^1];
        roots.RemoveAt(roots.Count - 1);
        Roots = roots.AsReadOnly();
        CountMutation(metrics, "domtree.mutation.post_roots.erased");
    }

    public LlvmBasicBlock? FindNearestCommonDominator(LlvmBasicBlock left, LlvmBasicBlock right,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(ReferenceEquals(left.Parent, right.Parent), "Blocks are not in the same function");
        if (!IsPostDominator)
        {
            var entry = Function.BasicBlocks[0];
            if (ReferenceEquals(left, entry) || ReferenceEquals(right, entry)) return entry;
        }
        var a = GetNode(left);
        var b = GetNode(right);
        Debug.Assert(a is not null && b is not null, "Both blocks must have tree nodes");
        while (!ReferenceEquals(a, b))
        {
            if (a!.Level < b!.Level) (a, b) = (b, a);
            a = a.IDom;
            CountMutation(metrics, "domtree.mutation.ncd.idom_steps");
        }
        return a!.Block;
    }

    private LlvmDominatorTreeNode CreateNode(LlvmBasicBlock block, LlvmDominatorTreeNode? idom = null)
    {
        var node = new LlvmDominatorTreeNode(block, idom);
        nodes.Add(block, node);
        nodeCapacity = Math.Max(nodeCapacity, Function.GetMaxBlockNumber() + (IsPostDominator ? 1U : 0U));
        idom?.AddChild(node);
        return node;
    }

    private void CountMutation(Dictionary<string, ulong>? metrics, string name, ulong value = 1)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + value;
        trace?.Metric(name, (long)value);
    }
}
