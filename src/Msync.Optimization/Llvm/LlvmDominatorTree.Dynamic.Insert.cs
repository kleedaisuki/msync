// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// GenericDomTreeConstruction.h InsertEdge/InsertReachable/InsertUnreachable.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private void DynamicInsertEdge(BatchUpdateInfo? batch, LlvmBasicBlock? from, LlvmBasicBlock to,
        Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(from is not null || IsPostDominator);
        var source = GetNode(from);
        if (source is null)
        {
            if (!IsPostDominator) { CountMutation(metrics, "domtree.dynamic.insert.unreachable_source"); return; }
            source = CreateNode(from!, GetNode(null));
            Roots = Roots.Append(from!).ToArray();
        }
        dfsInfoValid = false;
        var destination = GetNode(to);
        CountMutation(metrics, "domtree.dynamic.insert.queries");
        if (destination is null) DynamicInsertUnreachable(batch, source, to, metrics);
        else DynamicInsertReachable(batch, source, destination, metrics);
    }

    private void DynamicInsertReachable(BatchUpdateInfo? batch, LlvmDominatorTreeNode from,
        LlvmDominatorTreeNode to, Dictionary<string, ulong>? metrics)
    {
        if (IsPostDominator && ReferenceEquals(to.IDom, RootNode) && Roots.Contains(to.Block!))
        {
            CountMutation(metrics, "domtree.dynamic.rebuild.post_root_reverse_reachable");
            CalculateFromScratch(batch, metrics);
            return;
        }
        var ncdBlock = from.Block is not null && to.Block is not null
            ? FindNearestCommonDominator(from.Block, to.Block, metrics) : null;
        var ncd = GetNode(ncdBlock)!;
        Debug.Assert(ncd is not null);
        uint ncdLevel = ncd.Level;
        if (ncdLevel + 1 >= to.Level) { CountMutation(metrics, "domtree.dynamic.insert.no_affected_depth"); return; }
        var bucket = new PriorityQueue<LlvmDominatorTreeNode, uint>(Comparer<uint>.Create(static (a, b) => b.CompareTo(a)));
        HashSet<LlvmDominatorTreeNode> visited = new(ReferenceEqualityComparer.Instance) { to };
        List<LlvmDominatorTreeNode> affected = [], unaffectedOnLevel = [];
        bucket.Enqueue(to, to.Level);
        while (bucket.TryDequeue(out var node, out _))
        {
            affected.Add(node);
            uint currentLevel = node.Level;
            CountMutation(metrics, "domtree.dynamic.insert.bucket_popped");
            while (true)
            {
                foreach (var successor in DynamicChildren(node.Block!, batch, IsPostDominator))
                {
                    var successorNode = GetNode(successor)!;
                    Debug.Assert(successorNode is not null, "Unreachable successor at reachable insertion");
                    CountMutation(metrics, "domtree.dynamic.insert.successor_edges");
                    uint level = successorNode.Level;
                    if (level <= ncdLevel + 1 || !visited.Add(successorNode)) continue;
                    if (level > currentLevel)
                    {
                        unaffectedOnLevel.Add(successorNode);
                        CountMutation(metrics, "domtree.dynamic.insert.visited_unaffected");
                    }
                    else bucket.Enqueue(successorNode, level);
                }
                if (unaffectedOnLevel.Count == 0) break;
                node = unaffectedOnLevel[^1]; unaffectedOnLevel.RemoveAt(unaffectedOnLevel.Count - 1);
            }
        }
        foreach (var affectedNode in affected)
        {
            var (steps, levels) = affectedNode.SetIDom(ncd);
            CountMutation(metrics, "domtree.dynamic.insert.affected");
            CountMutation(metrics, "domtree.dynamic.children.unlink_steps", steps);
            CountMutation(metrics, "domtree.dynamic.levels.updated", levels);
        }
        if (IsPostDominator) UpdateRootsAfterUpdate(batch, metrics);
    }

    private void DynamicInsertUnreachable(BatchUpdateInfo? batch, LlvmDominatorTreeNode from,
        LlvmBasicBlock to, Dictionary<string, ulong>? metrics)
    {
        List<(LlvmBasicBlock From, LlvmDominatorTreeNode To)> connecting = [];
        var construction = new Construction(this, trace, metrics, batch);
        construction.RunDFS(to, 0, 0, condition: (source, destination) =>
        {
            var node = GetNode(destination);
            if (node is null) return true;
            connecting.Add((source, node));
            return false;
        });
        construction.RunSemiNCA();
        CreateNode(construction.NumToNode[0]!, from);
        construction.AttachNewSubtree();
        foreach (var edge in connecting)
        {
            CountMutation(metrics, "domtree.dynamic.insert.connecting_edges");
            DynamicInsertReachable(batch, GetNode(edge.From)!, edge.To, metrics);
        }
    }
}
