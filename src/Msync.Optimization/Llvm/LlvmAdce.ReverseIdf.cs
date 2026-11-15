// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// GenericIteratedDominanceFrontier.h IDFCalculatorBase<Node,true>::calculate.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmAdce
{
    private static List<LlvmBasicBlock> ReverseIdf(LlvmDominatorTree tree,
        IReadOnlySet<LlvmBasicBlock> definitions, IReadOnlySet<LlvmBasicBlock> liveIn,
        Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(tree.IsPostDominator);
        tree.UpdateDFSNumbers();
        int count = (int)(tree.RootNode?.DFSNumOut ?? 0);
        var visitedQueue = new bool[count]; var visitedWork = new bool[count];
        var work = new Stack<LlvmDominatorTreeNode>();
        var queue = new PriorityQueue<LlvmDominatorTreeNode, (uint Level, uint Dfs)>(
            Comparer<(uint Level, uint Dfs)>.Create(static (a, b) => b.CompareTo(a)));
        var result = new List<LlvmBasicBlock>();
        foreach (var block in definitions)
        {
            if (tree.GetNode(block) is not { } node) continue;
            queue.Enqueue(node, (node.Level, node.DFSNumIn)); visitedWork[node.DFSNumIn] = true;
            IdfCount(metrics, "roots");
        }
        while (queue.TryDequeue(out var root, out var rootPriority))
        {
            IdfCount(metrics, "queue_pops"); work.Push(root);
            while (work.TryPop(out var node))
            {
                IdfCount(metrics, "tree_nodes");
                // Reverse IDF's CFG children are predecessors, not successors.
                foreach (var predecessor in LlvmDominatorTree.Predecessors(node.Block!))
                {
                    IdfCount(metrics, "cfg_edges");
                    var predecessorNode = tree.GetNode(predecessor)!;
                    if (predecessorNode.Level > rootPriority.Level) continue;
                    uint index = predecessorNode.DFSNumIn;
                    if (visitedQueue[index]) continue;
                    visitedQueue[index] = true;
                    if (!liveIn.Contains(predecessor)) { IdfCount(metrics, "pruned_not_live_in"); continue; }
                    result.Add(predecessor); IdfCount(metrics, "frontier_blocks");
                    if (!definitions.Contains(predecessor)) queue.Enqueue(predecessorNode, (predecessorNode.Level, index));
                }
                foreach (var child in node.Children)
                {
                    if (visitedWork[child.DFSNumIn]) continue;
                    visitedWork[child.DFSNumIn] = true; work.Push(child);
                }
            }
        }
        return result;
    }
    private static void IdfCount(Dictionary<string, ulong>? metrics, string name)
    {
        if (metrics is null) return;
        string key = "reverse_idf." + name;
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }
}
