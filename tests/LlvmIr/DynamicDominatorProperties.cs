using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class DynamicDominatorProperties
{
    internal static int Run()
    {
        var context = new LlvmContext();
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "dynamic-domtree");
        var entry = new LlvmBasicBlock(context, "entry", function);
        var a = new LlvmBasicBlock(context, "a", function);
        var b = new LlvmBasicBlock(context, "b", function);
        var c = new LlvmBasicBlock(context, "c", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        var unreachable = new LlvmBasicBlock(context, "unreachable", function);
        var condition = LlvmUndefValue.Get(LlvmIntegerType.Get(context, 1));
        entry.Append(new LlvmUncondBrInstruction(a));
        a.Append(new LlvmUncondBrInstruction(b)); b.Append(new LlvmUncondBrInstruction(c));
        c.Append(new LlvmUncondBrInstruction(exit)); exit.Append(new LlvmReturnInstruction(context));
        unreachable.Append(new LlvmReturnInstruction(context));
        var tree = new LlvmDominatorTree(function);
        var metrics = new Dictionary<string, ulong>();
        int comparisons = 0;
        Replace(a, new LlvmCondBrInstruction(condition, b, c));
        tree.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, a, c)], metrics: metrics);
        Compare();
        Program.Require(ReferenceEquals(tree.GetNode(c)!.IDom!.Block, a), "Depth-based insertion did not update c.");
        Replace(a, new LlvmUncondBrInstruction(b));
        tree.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, a, c)], metrics: metrics);
        Compare();
        Program.Require(metrics.GetValueOrDefault("delete.reachable.subtree_rebuild") == 1
            && metrics.GetValueOrDefault("domtree.dynamic.rebuild.completed") == 0,
            "A source local deletion rebuild became a whole-tree reconstruction.");

        Replace(a, new LlvmCondBrInstruction(condition, b, unreachable));
        Replace(unreachable, new LlvmUncondBrInstruction(c));
        tree.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, a, unreachable), new(LlvmCfgUpdateKind.Insert, unreachable, c)], metrics: metrics);
        Compare();
        Replace(a, new LlvmUncondBrInstruction(b));
        Replace(unreachable, new LlvmReturnInstruction(context));
        tree.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, a, unreachable), new(LlvmCfgUpdateKind.Delete, unreachable, c)], metrics: metrics);
        Compare();

        // Mixed updates: the real CFG already contains both changes. PostView
        // puts the removed edge back while insert-side SSA updates execute.
        Replace(a, new LlvmCondBrInstruction(condition, b, c));
        Replace(b, new LlvmReturnInstruction(context));
        tree.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, a, c), new(LlvmCfgUpdateKind.Delete, b, c)],
            [new(LlvmCfgUpdateKind.Insert, b, c)], metrics);
        Replace(b, new LlvmUncondBrInstruction(c));
        Compare();
        Replace(b, new LlvmReturnInstruction(context));
        tree.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, b, c)], metrics: metrics);
        Compare();

        var updates = new LlvmCfgUpdate[]
        {
            new(LlvmCfgUpdateKind.Insert, a, unreachable), new(LlvmCfgUpdateKind.Insert, b, unreachable),
            new(LlvmCfgUpdateKind.Delete, a, unreachable), new(LlvmCfgUpdateKind.Insert, a, unreachable)
        };
        var diff = new LlvmCfgDiff(updates, metrics: metrics);
        Program.Require(diff.NumLegalizedUpdates == 2
            && diff.PopUpdateForIncrementalUpdates() == updates[1]
            && diff.PopUpdateForIncrementalUpdates() == updates[3], "CFG updates lost source last-occurrence legalization order.");
        Replace(a, new LlvmCondBrInstruction(condition, b, b));
        var removeParallel = new LlvmCfgDiff([new(LlvmCfgUpdateKind.Delete, a, b)]);
        Program.Require(removeParallel.GetChildren(a).Count == 0, "GraphDiff deletion failed to remove all parallel CFG edges.");
        var reverse = new LlvmCfgDiff([new(LlvmCfgUpdateKind.Insert, a, b)], reverseApplyUpdates: true);
        Program.Require(reverse.GetChildren(a).Count == 0, "GraphDiff reverse application did not undo an inserted edge.");
        var inverse = new LlvmCfgDiff([new(LlvmCfgUpdateKind.Delete, a, b)], inverseGraph: true);
        Program.Require(inverse.GetChildren(b, inverseEdge: true).Count == 0, "GraphDiff inverse-graph direction changed.");
        return comparisons;

        void Compare()
        {
            var expected = new LlvmDominatorTree(function);
            foreach (var left in function.BasicBlocks)
            {
                Program.Require(tree.GetNode(left)?.Level == expected.GetNode(left)?.Level
                    && ReferenceEquals(tree.GetNode(left)?.IDom?.Block, expected.GetNode(left)?.IDom?.Block),
                    $"Dynamic IDom/level differs at {left.Name}.");
                foreach (var right in function.BasicBlocks)
                {
                    Program.Require(tree.Dominates(left, right) == expected.Dominates(left, right), $"Dynamic dominance differs at {left.Name}/{right.Name}.");
                    ++comparisons;
                }
            }
            tree.UpdateDFSNumbers();
        }
        static void Replace(LlvmBasicBlock block, LlvmInstruction terminator)
        { block.GetTerminator().EraseFromParent(); block.Append(terminator); }
    }
}
