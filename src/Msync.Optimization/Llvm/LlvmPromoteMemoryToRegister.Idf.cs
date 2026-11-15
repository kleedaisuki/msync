// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericIteratedDominanceFrontier.h, IDFCalculatorBase::calculate.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed class LlvmForwardIdfCalculator(LlvmDominatorTree tree, Dictionary<string, ulong>? metrics)
{
    internal IReadOnlySet<LlvmBasicBlock> DefiningBlocks { get; set; } = null!;
    internal IReadOnlySet<LlvmBasicBlock>? LiveInBlocks { get; set; }
    // Source GraphDiff supplies a snapshot CFG for incremental MemorySSA updates.
    internal Func<LlvmBasicBlock, IEnumerable<LlvmBasicBlock>>? SuccessorBlocks { get; set; }
    internal void Calculate(List<LlvmBasicBlock> result)
    {
        tree.UpdateDFSNumbers();
        int nodeCount = (int)(tree.RootNode?.DFSNumOut ?? 0);
        var visitedQueue = new bool[nodeCount];
        var visitedWork = new bool[nodeCount];
        var worklist = new Stack<LlvmDominatorTreeNode>();
        // C++ priority_queue<..., less_second> pops the lexicographically largest pair.
        var priority = new PriorityQueue<LlvmDominatorTreeNode, (uint Level, uint Dfs)>(
            Comparer<(uint Level, uint Dfs)>.Create(static (a, b) => b.CompareTo(a)));
        foreach (var block in DefiningBlocks)
        {
            if (tree.GetNode(block) is not { } node) continue;
            priority.Enqueue(node, (node.Level, node.DFSNumIn));
            visitedWork[node.DFSNumIn] = true;
            Count("idf.roots");
        }
        while (priority.TryDequeue(out var root, out var rootPriority))
        {
            Count("idf.queue_pops");
            worklist.Push(root);
            while (worklist.TryPop(out var node))
            {
                Count("idf.tree_nodes");
                foreach (var successor in SuccessorBlocks?.Invoke(node.Block!) ?? LlvmDominatorTree.Successors(node.Block!))
                {
                    Count("idf.cfg_edges");
                    var successorNode = tree.GetNode(successor)!;
                    if (successorNode.Level > rootPriority.Level) continue;
                    uint index = successorNode.DFSNumIn;
                    if (visitedQueue[index]) continue;
                    visitedQueue[index] = true;
                    if (LiveInBlocks is not null && !LiveInBlocks.Contains(successor))
                    { Count("idf.pruned_not_live"); continue; }
                    result.Add(successor);
                    Count("idf.frontier_blocks");
                    if (!DefiningBlocks.Contains(successor))
                        priority.Enqueue(successorNode, (successorNode.Level, index));
                }
                foreach (var child in node.Children)
                {
                    if (visitedWork[child.DFSNumIn]) continue;
                    visitedWork[child.DFSNumIn] = true;
                    worklist.Push(child);
                }
            }
        }
    }
    private void Count(string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
