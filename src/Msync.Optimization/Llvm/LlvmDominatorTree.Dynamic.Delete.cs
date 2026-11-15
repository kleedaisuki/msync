// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericDomTreeConstruction.h DeleteEdge/DeleteReachable/
// HasProperSupport/DeleteUnreachable. Shared Semi-NCA/GraphDiff state lives in
// the dynamic construction component; whole-tree fallbacks are source decisions.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private void DynamicDeleteEdge(BatchUpdateInfo? batch, LlvmBasicBlock from, LlvmBasicBlock to,
        Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(!DynamicChildren(from, batch, IsPostDominator).Contains(to), "Deleted edge still exists in the CFG!");
        DeleteCount(metrics, "delete.edges");
        var fromNode = GetNode(from);
        if (fromNode is null) { DeleteCount(metrics, "delete.unreachable_source"); return; }
        var toNode = GetNode(to);
        if (toNode is null) { DeleteCount(metrics, "delete.already_unreachable_destination"); return; }
        var commonBlock = FindNearestCommonDominator(from, to, metrics);
        var common = GetNode(commonBlock);
        if (!ReferenceEquals(toNode, common))
        {
            dfsInfoValid = false;
            if (!ReferenceEquals(fromNode, toNode.IDom) || DynamicHasProperSupport(batch, toNode, metrics))
                DynamicDeleteReachable(batch, fromNode, toNode, metrics);
            else DynamicDeleteUnreachable(batch, toNode, metrics);
        }
        else DeleteCount(metrics, "delete.destination_dominates_source");
        if (IsPostDominator) UpdateRootsAfterUpdate(batch, metrics);
    }
    private void DynamicDeleteReachable(BatchUpdateInfo? batch, LlvmDominatorTreeNode from,
        LlvmDominatorTreeNode to, Dictionary<string, ulong>? metrics)
    {
        DeleteCount(metrics, "delete.reachable");
        var subtreeBlock = FindNearestCommonDominator(from.Block!, to.Block!, metrics);
        Debug.Assert(subtreeBlock is not null || IsPostDominator);
        var subtree = GetNode(subtreeBlock)!;
        var previousParent = subtree.IDom;
        if (previousParent is null)
        {
            DeleteCount(metrics, "delete.reachable.root_rebuild");
            CalculateFromScratch(batch, metrics); return;
        }
        uint level = subtree.Level;
        var construction = new Construction(this, trace, metrics, batch);
        construction.RunDFS(subtreeBlock!, 0, 0,
            condition: (_, destination) => GetNode(destination)!.Level > level);
        construction.RunSemiNCA();
        construction.ReattachExistingSubtree(previousParent);
        DeleteCount(metrics, "delete.reachable.subtree_rebuild");
        DeleteCount(metrics, "delete.reachable.rebuilt_nodes", (ulong)construction.NumToNode.Count);
    }
    private bool DynamicHasProperSupport(BatchUpdateInfo? batch, LlvmDominatorTreeNode node,
        Dictionary<string, ulong>? metrics)
    {
        foreach (var predecessor in DynamicChildren(node.Block!, batch, !IsPostDominator))
        {
            DeleteCount(metrics, "delete.support.predecessors");
            if (GetNode(predecessor) is null) continue;
            var support = FindNearestCommonDominator(node.Block!, predecessor, metrics);
            if (!ReferenceEquals(support, node.Block))
            { DeleteCount(metrics, "delete.support.found"); return true; }
        }
        DeleteCount(metrics, "delete.support.not_found"); return false;
    }
    private void DynamicDeleteUnreachable(BatchUpdateInfo? batch, LlvmDominatorTreeNode destination,
        Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(destination.Block is not null);
        DeleteCount(metrics, "delete.unreachable");
        if (IsPostDominator)
        {
            // Source models newly reverse-unreachable region by a virtual-root edge.
            Roots = Roots.Append(destination.Block!).ToArray();
            DynamicInsertReachable(batch, GetNode(null)!, destination, metrics);
            DeleteCount(metrics, "delete.postdom.new_root"); return;
        }
        var affected = new List<LlvmBasicBlock>();
        uint level = destination.Level;
        var construction = new Construction(this, trace, metrics, batch);
        int last = construction.RunDFS(destination.Block!, 0, 0, condition: (_, to) =>
        {
            var node = GetNode(to)!;
            Debug.Assert(node is not null);
            if (node.Level > level) return true;
            if (!affected.Contains(to)) affected.Add(to);
            return false;
        });
        var minimum = destination;
        foreach (var block in affected)
        {
            var node = GetNode(block)!;
            var commonBlock = FindNearestCommonDominator(node.Block!, destination.Block!, metrics);
            Debug.Assert(commonBlock is not null || IsPostDominator);
            var common = GetNode(commonBlock)!;
            if (!ReferenceEquals(common, node) && common.Level < minimum.Level) minimum = common;
            DeleteCount(metrics, "delete.unreachable.affected_nodes");
        }
        if (minimum.IDom is null)
        {
            DeleteCount(metrics, "delete.unreachable.root_rebuild");
            CalculateFromScratch(batch, metrics); return;
        }
        // Source reverse preorder erases children before their parents.
        for (int i = last; i-- > 0;)
        {
            EraseNode(construction.NumToNode[i]!, metrics);
            DeleteCount(metrics, "delete.unreachable.nodes_erased");
        }
        if (ReferenceEquals(minimum, destination)) return;
        uint minimumLevel = minimum.Level;
        var previousParent = minimum.IDom!;
        construction.Clear();
        construction.RunDFS(minimum.Block!, 0, 0, condition: (from, to) =>
        {
            var toNode = GetNode(to);
            if (toNode is not null) return toNode.Level > minimumLevel;
            CreateNode(to, GetNode(from));
            DeleteCount(metrics, "delete.unreachable.nodes_recreated"); return true;
        });
        construction.RunSemiNCA();
        construction.ReattachExistingSubtree(previousParent);
        DeleteCount(metrics, "delete.unreachable.subtree_rebuild");
        DeleteCount(metrics, "delete.unreachable.rebuilt_nodes", (ulong)construction.NumToNode.Count);
    }
    private static void DeleteCount(Dictionary<string, ulong>? metrics, string key, ulong count = 1)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + count; }
}
