// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericDomTreeConstruction.h: SemiNCAInfo static construction.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private sealed partial class Construction
    {
        private sealed class Info
        {
            internal int DFSNumPlus1, Parent, Semi, Label, IDom, ReverseChildrenStart;
        }
        private readonly Dictionary<LlvmBasicBlock, Info> infos = new(ReferenceEqualityComparer.Instance);
        private readonly Info virtualRoot = new();
        internal readonly List<LlvmBasicBlock?> numToNode = [];
        private readonly List<(int Predecessor, int Next)> reverseChildren = [];
        private readonly CompilationTrace? trace;
        private readonly BatchUpdateInfo? batch;
        internal LlvmDominatorTree Tree { get; }
        internal Dictionary<string, ulong>? Metrics { get; }
        internal Construction(LlvmDominatorTree tree, CompilationTrace? trace, Dictionary<string, ulong>? metrics = null, BatchUpdateInfo? batch = null)
        {
            Tree = tree;
            this.batch = batch;
            this.trace = trace;
            if (trace is not null || metrics is not null) Metrics = metrics ?? [];
            foreach (var block in tree.Function.BasicBlocks) infos.Add(block, new());
        }
        private Info GetInfo(LlvmBasicBlock? block)
        {
            if (block is null) return virtualRoot;
            if (!infos.TryGetValue(block, out var info)) infos.Add(block, info = new());
            return info;
        }
        private void Count(string name, ulong count = 1)
        {
            if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + count;
        }
        internal void Calculate()
        {
            Phase("roots", FindTreeRoots);
            Phase("cfg-dfs", FullDFS);
            Phase("semi-nca", RunSemiNCA);
            Phase("tree-attach", AttachTree);
            Count("cfg.blocks.total", (ulong)infos.Count);
            Count("domtree.nodes", (ulong)Tree.nodes.Count);
            Count("cfg.blocks.unreachable", (ulong)(infos.Count - Tree.nodes.Count));
        }
        private void Phase(string name, Action action)
        {
            if (trace is null) action();
            else trace.Run("optimizer.llvm-dominators." + name, action, static run => run());
        }
        // GenericDomTreeConstruction getChildren reverses forward successors so
        // the LIFO worklist visits their natural order. Inverse edges stay in use order.
        private IEnumerable<LlvmBasicBlock> Children(LlvmBasicBlock block, bool inverse)
        {
            if (batch is not null)
            {
                foreach (var child in batch.PreView.GetChildren(block, inverse)) yield return child;
                yield break;
            }
            if (inverse)
            {
                foreach (var predecessor in Predecessors(block)) yield return predecessor;
                yield break;
            }
            var terminator = block.GetTerminator();
            for (int index = terminator.SuccessorCount; index-- > 0;) yield return terminator.GetSuccessor(index);
        }
        internal int RunDFS(LlvmBasicBlock start, int lastNum, int attachToNum, bool reverse = false,
            Dictionary<LlvmBasicBlock, int>? successorOrder = null, Func<LlvmBasicBlock, LlvmBasicBlock, bool>? condition = null)
        {
            var work = new List<(LlvmBasicBlock Block, int Parent)> { (start, attachToNum) };
            GetInfo(start).Parent = attachToNum;
            Count("dfs.walks");
            while (work.Count != 0)
            {
                var (block, parent) = work[^1];
                work.RemoveAt(work.Count - 1);
                Info info = GetInfo(block);
                reverseChildren.Add((parent, info.ReverseChildrenStart));
                info.ReverseChildrenStart = reverseChildren.Count;
                Count("dfs.worklist.popped");
                Count("dfs.reverse_edges.recorded");
                if (info.DFSNumPlus1 != 0) { Count("dfs.already_visited"); continue; }
                info.Parent = parent;
                int number = lastNum++;
                info.Semi = info.Label = number;
                info.DFSNumPlus1 = number + 1;
                numToNode.Add(block);
                Count("dfs.nodes.visited");
                if (successorOrder is null)
                {
                    foreach (var child in Children(block, reverse != Tree.IsPostDominator))
                    {
                        Count("dfs.cfg_edges.visited");
                        if (condition is not null && !condition(block, child)) { Count("dfs.edges.pruned"); continue; }
                        work.Add((child, number));
                    }
                    continue;
                }
                var children = Children(block, reverse != Tree.IsPostDominator).ToList();
                if (children.Count > 1) children.Sort((a, b) => successorOrder[a].CompareTo(successorOrder[b]));
                foreach (var child in children)
                {
                    Count("dfs.cfg_edges.visited");
                    if (condition is not null && !condition(block, child)) { Count("dfs.edges.pruned"); continue; }
                    work.Add((child, number));
                }
            }
            return lastNum;
        }
        private int Eval(int vertex, int lastLinked, List<Info> stack, Info[] numToInfo)
        {
            Count("eval.calls");
            Info info = numToInfo[vertex];
            if (info.Parent < lastLinked) { Count("eval.unlinked"); return info.Label; }
            Debug.Assert(stack.Count == 0);
            do
            {
                stack.Add(info);
                info = numToInfo[info.Parent];
                Count("eval.ancestors.walked");
            } while (info.Parent >= lastLinked);
            Info parentInfo = info;
            int parentLabel = parentInfo.Label;
            do
            {
                info = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                info.Parent = parentInfo.Parent;
                int label = info.Label;
                if (parentLabel < label) { info.Label = parentLabel; Count("eval.labels.lowered"); }
                else parentLabel = label;
                parentInfo = info;
                Count("eval.path_compressions");
            } while (stack.Count != 0);
            return info.Label;
        }
        internal void RunSemiNCA()
        {
            var numToInfo = new Info[numToNode.Count];
            for (int index = 0; index < numToInfo.Length; ++index)
            {
                Info info = GetInfo(numToNode[index]);
                info.IDom = info.Parent;
                numToInfo[index] = info;
            }
            var stack = new List<Info>();
            for (int index = numToInfo.Length; --index > 0;)
            {
                Info info = numToInfo[index];
                info.Semi = info.Parent;
                for (int reverseIndex = info.ReverseChildrenStart; reverseIndex != 0;)
                {
                    var edge = reverseChildren[reverseIndex - 1];
                    reverseIndex = edge.Next;
                    int semi = Eval(edge.Predecessor, index + 1, stack, numToInfo);
                    if (semi < info.Semi) { info.Semi = semi; Count("semi.lowered"); }
                    Count("semi.reverse_edges.visited");
                }
                info.Label = info.Semi;
                Count("semi.vertices.processed");
            }
            for (int index = 1; index < numToInfo.Length; ++index)
            {
                Info info = numToInfo[index];
                int idom = info.IDom;
                while (idom > info.Semi) { idom = numToInfo[idom].IDom; Count("nca.idom_steps"); }
                info.IDom = idom;
                Count("nca.vertices.processed");
            }
        }
        private void AddVirtualRoot()
        {
            Debug.Assert(Tree.IsPostDominator && numToNode.Count == 0);
            virtualRoot.Semi = virtualRoot.Label = 0;
            virtualRoot.DFSNumPlus1 = 1;
            numToNode.Add(null);
        }
        private void FullDFS()
        {
            if (!Tree.IsPostDominator)
            {
                RunDFS(Tree.Roots[0], 0, 0);
                return;
            }
            AddVirtualRoot();
            int number = 1;
            foreach (var root in Tree.Roots) number = RunDFS(root, number, 0);
        }
        private void AttachTree()
        {
            if (Tree.Roots.Count == 0) return;
            Tree.RootNode = new(Tree.IsPostDominator ? null : Tree.Roots[0], null);
            if (Tree.RootNode.Block is { } root) Tree.nodes.Add(root, Tree.RootNode);
            for (int number = 1; number < numToNode.Count; ++number)
            {
                var block = numToNode[number]!;
                var idom = Tree.GetNode(numToNode[GetInfo(block).IDom]);
                Debug.Assert(idom is not null);
                Tree.nodes.Add(block, new(block, idom));
            }
            for (int number = numToNode.Count; --number > 0;)
            {
                var node = Tree.GetNode(numToNode[number])!;
                node.IDom!.AddChild(node);
                Count("tree.nodes.attached");
            }
        }
    }
}
