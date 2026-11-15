// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericDomTree.h, IR/Dominators.h and IR/CFG.h.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTreeNode
{
    internal LlvmDominatorTreeNode(LlvmBasicBlock? block, LlvmDominatorTreeNode? idom)
        => (Block, IDom, Level) = (block, idom, idom is null ? 0 : idom.Level + 1);
    public LlvmBasicBlock? Block { get; }
    public LlvmDominatorTreeNode? IDom { get; internal set; }
    public uint Level { get; internal set; }
    public uint DFSNumIn { get; internal set; } = uint.MaxValue;
    public uint DFSNumOut { get; internal set; } = uint.MaxValue;
    internal LlvmDominatorTreeNode? FirstChild;
    internal LlvmDominatorTreeNode? Sibling;
    public IEnumerable<LlvmDominatorTreeNode> Children
    {
        get { for (var node = FirstChild; node is not null; node = node.Sibling) yield return node; }
    }
    internal void AddChild(LlvmDominatorTreeNode child)
    {
        Debug.Assert(child.Sibling is null);
        child.Sibling = FirstChild;
        FirstChild = child;
    }
    internal bool DominatedBy(LlvmDominatorTreeNode other) => DFSNumIn >= other.DFSNumIn && DFSNumOut <= other.DFSNumOut;
}

/// <summary>LLVM Semi-NCA construction with source dominator-tree mutation primitives.</summary>
public sealed partial class LlvmDominatorTree
{
    private readonly Dictionary<LlvmBasicBlock, LlvmDominatorTreeNode> nodes = new(ReferenceEqualityComparer.Instance);
    private readonly CompilationTrace? trace;
    private bool dfsInfoValid;
    private uint slowQueries;
    private uint nodeCapacity;
    public LlvmFunction Function { get; private set; }
    public bool IsPostDominator { get; }
    public LlvmDominatorTreeNode? RootNode { get; private set; }
    public IReadOnlyList<LlvmBasicBlock> Roots { get; private set; } = [];
    public LlvmDominatorTree(LlvmFunction function, CompilationTrace? trace = null, bool isPostDominator = false)
    {
        Function = function;
        blockNumberEpoch = function.GetBlockNumberEpoch();
        IsPostDominator = isPostDominator;
        this.trace = trace?.Detail;
        nodeCapacity = function.GetMaxBlockNumber() + (isPostDominator ? 1U : 0U);
        var construction = new Construction(this, this.trace);
        if (this.trace is null) construction.Calculate();
        else this.trace.MeasureDetailed("optimizer.analysis.llvm-dominators", construction,
            static state => { state.Calculate(); return state.Tree; },
            input: new CompilationSize(1, 0, function.BasicBlocks.Count),
            size: _ => new CompilationSize(1, 0, nodes.Count),
            attributes: new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["dominance.direction"] = isPostDominator ? "post" : "forward"
            }, metrics: _ => construction.Metrics!);
    }
    public LlvmDominatorTreeNode? GetNode(LlvmBasicBlock? block)
    {
        Debug.Assert(blockNumberEpoch == Function.GetBlockNumberEpoch(), "Dominator-tree block numbers need updating after renumbering");
        return block is null ? IsPostDominator ? RootNode : null : nodes.GetValueOrDefault(block);
    }
    public bool IsReachableFromEntry(LlvmBasicBlock block)
    {
        Debug.Assert(!IsPostDominator);
        return GetNode(block) is not null;
    }
    public bool Dominates(LlvmBasicBlock? a, LlvmBasicBlock? b) => ReferenceEquals(a, b) || Dominates(GetNode(a), GetNode(b));
    public bool Dominates(LlvmDominatorTreeNode? a, LlvmDominatorTreeNode? b)
    {
        if (ReferenceEquals(a, b) || b is null) return true;
        if (a is null) return false;
        if (ReferenceEquals(b.IDom, a)) return true;
        if (ReferenceEquals(a.IDom, b) || a.Level >= b.Level) return false;
        if (dfsInfoValid) return b.DominatedBy(a);
        if (++slowQueries > 32)
        {
            UpdateDFSNumbers();
            return b.DominatedBy(a);
        }
        while (b.IDom is { } idom && idom.Level >= a.Level)
        {
            if (ReferenceEquals(idom, a)) return true;
            b = idom;
        }
        return false;
    }
    public bool ProperlyDominates(LlvmBasicBlock? a, LlvmBasicBlock? b) => !ReferenceEquals(a, b) && Dominates(a, b);
    public void UpdateDFSNumbers()
    {
        if (dfsInfoValid) { slowQueries = 0; return; }
        if (RootNode is null) return;
        if (trace is null) NumberTree();
        else trace.Run("optimizer.llvm-dominators.tree-dfs", this, static tree => tree.NumberTree());
    }
    private void NumberTree()
    {
        var stack = new List<(LlvmDominatorTreeNode Node, LlvmDominatorTreeNode? Child)>();
        uint number = 0;
        RootNode!.DFSNumIn = number++;
        stack.Add((RootNode, RootNode.FirstChild));
        while (stack.Count != 0)
        {
            var (node, child) = stack[^1];
            if (child is null)
            {
                node.DFSNumOut = number;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack[^1] = (node, child.Sibling);
            child.DFSNumIn = number++;
            stack.Add((child, child.FirstChild));
        }
        slowQueries = 0;
        dfsInfoValid = true;
        trace?.Metric("domtree.nodes.numbered", number);
    }
    public static IEnumerable<LlvmBasicBlock> Predecessors(LlvmBasicBlock block)
    {
        foreach (LlvmUse use in block.Uses)
        {
            Debug.Assert(use.User is LlvmInstruction { IsTerminator: true });
            yield return ((LlvmInstruction)use.User).Parent!;
        }
    }
    public static IEnumerable<LlvmBasicBlock> Successors(LlvmBasicBlock block)
    {
        LlvmInstruction terminator = block.GetTerminator();
        for (int index = 0; index < terminator.SuccessorCount; ++index) yield return terminator.GetSuccessor(index);
    }
}
