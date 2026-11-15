using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class DomTreeUpdaterProperties
{
    internal static int Run()
    {
        int checks = 0;
        EagerAndPermissive();
        LazyDeletion();
        CriticalSplits();
        Recalculate();
        NoTreesDeletion();
        return checks;

        void Compare(LlvmDominatorTree actual)
        {
            var expected = new LlvmDominatorTree(actual.Function, isPostDominator: actual.IsPostDominator);
            foreach (var block in actual.Function.BasicBlocks)
            {
                Program.Require((actual.GetNode(block) is null) == (expected.GetNode(block) is null), "DTU reachability differs from fresh source construction.");
                Program.Require(ReferenceEquals(actual.GetNode(block)?.IDom?.Block, expected.GetNode(block)?.IDom?.Block)
                    && actual.GetNode(block)?.Level == expected.GetNode(block)?.Level, "DTU IDom differs from fresh source construction.");
                foreach (var other in actual.Function.BasicBlocks)
                {
                    Program.Require(actual.Dominates(block, other) == expected.Dominates(block, other), "DTU dominance differs from fresh source construction.");
                    ++checks;
                }
            }
        }

        void EagerAndPermissive()
        {
            var (function, entry, left, right, exit) = Diamond("updater-eager");
            var tree = new LlvmDominatorTree(function);
            var post = new LlvmDominatorTree(function, isPostDominator: true);
            var metrics = new Dictionary<string, ulong>();
            using var updater = new LlvmDomTreeUpdater(tree, post, LlvmDomTreeUpdateStrategy.Eager, metrics: metrics);
            Replace(left, new LlvmCondBrInstruction(function.Arguments[0], exit, right));
            updater.ApplyUpdatesPermissive([
                new(LlvmCfgUpdateKind.Insert, left, right), new(LlvmCfgUpdateKind.Insert, left, right),
                new(LlvmCfgUpdateKind.Delete, entry, right), new(LlvmCfgUpdateKind.Insert, left, left)]);
            Compare(tree); Compare(post);
            Program.Require(!updater.HasPendingUpdates && metrics["domtree.updater.permissive.duplicates_skipped"] == 1
                && metrics["domtree.updater.permissive.cfg_noops_skipped"] == 1, "Permissive source filtering changed.");
            ++checks;
            Replace(left, new LlvmUncondBrInstruction(exit));
            updater.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, left, right)]);
            Compare(tree); Compare(post);
        }

        void LazyDeletion()
        {
            var (function, entry, left, right, exit) = Diamond("updater-lazy");
            var tree = new LlvmDominatorTree(function);
            var post = new LlvmDominatorTree(function, isPostDominator: true);
            using var updater = new LlvmDomTreeUpdater(tree, post, LlvmDomTreeUpdateStrategy.Lazy);
            Replace(entry, new LlvmUncondBrInstruction(right));
            Replace(left, new LlvmUnreachableInstruction(function.Context));
            updater.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, entry, left), new(LlvmCfgUpdateKind.Delete, left, exit)]);
            int callbacks = 0;
            updater.CallbackDeleteBlock(left, block =>
            {
                Program.Require(block.Parent is null && block.Instructions.Count == 0, "Lazy callback ran before actual block erasure.");
                ++callbacks;
            });
            updater.CallbackDeleteBlock(left, _ => ++callbacks);
            Program.Require(updater.IsBlockPendingDeletion(left) && left.Parent == function && updater.HasPendingTreeUpdates
                && updater.HasPendingPostTreeUpdates, "Lazy source queue was flushed eagerly.");
            updater.GetTree();
            Compare(tree);
            Program.Require(left.Parent == function && callbacks == 0 && !updater.HasPendingTreeUpdates
                && updater.HasPendingPostTreeUpdates, "One-tree acquisition erased blocks needed by the other tree.");
            updater.GetPostTree();
            Program.Require(left.Parent is null && callbacks == 2 && !updater.HasPendingDeletedBlocks
                && !updater.HasPendingUpdates, "Lazy source deletion/callbacks did not retire after both cursors advanced.");
            Compare(tree); Compare(post);
            checks += 3;
        }

        void CriticalSplits()
        {
            foreach (var strategy in new[] { LlvmDomTreeUpdateStrategy.Eager, LlvmDomTreeUpdateStrategy.Lazy })
            {
                var (function, entry, left, right, exit) = Diamond("updater-critical");
                var otherExit = new LlvmBasicBlock(function.Context, "other.exit", function);
                otherExit.Append(new LlvmReturnInstruction(function.Context));
                Replace(left, new LlvmCondBrInstruction(function.Arguments[0], exit, otherExit));
                Replace(right, new LlvmCondBrInstruction(function.Arguments[0], exit, otherExit));
                var tree = new LlvmDominatorTree(function);
                var post = new LlvmDominatorTree(function, isPostDominator: true);
                using var updater = new LlvmDomTreeUpdater(tree, post, strategy);
                var splitLeft = new LlvmBasicBlock(function.Context, "left.split", function);
                splitLeft.Append(new LlvmUncondBrInstruction(exit));
                Replace(left, new LlvmCondBrInstruction(function.Arguments[0], splitLeft, otherExit));
                updater.SplitCriticalEdge(left, exit, splitLeft);
                var splitRight = new LlvmBasicBlock(function.Context, "right.split", function);
                splitRight.Append(new LlvmUncondBrInstruction(exit));
                Replace(right, new LlvmCondBrInstruction(function.Arguments[0], splitRight, otherExit));
                updater.SplitCriticalEdge(right, exit, splitRight);
                updater.Flush();
                Compare(tree); Compare(post);
                Replace(entry, new LlvmUncondBrInstruction(left));
                updater.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, entry, right)]);
                updater.Flush();
                Compare(tree); Compare(post);
            }
        }

        void Recalculate()
        {
            var (function, entry, left, right, exit) = Diamond("updater-recalculate");
            var tree = new LlvmDominatorTree(function);
            var post = new LlvmDominatorTree(function, isPostDominator: true);
            using var updater = new LlvmDomTreeUpdater(tree, post, LlvmDomTreeUpdateStrategy.Lazy);
            Replace(entry, new LlvmUncondBrInstruction(right));
            Replace(left, new LlvmUnreachableInstruction(function.Context));
            updater.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, entry, left), new(LlvmCfgUpdateKind.Delete, left, exit)]);
            updater.DeleteBlock(left);
            updater.Recalculate(function);
            Program.Require(left.Parent is null && !updater.HasPendingUpdates, "Recalculate did not retire source lazy work.");
            Compare(tree); Compare(post);
            var (replacement, _, _, _, _) = Diamond("updater-replacement-function");
            updater.Recalculate(replacement);
            Program.Require(ReferenceEquals(tree.Function, replacement) && ReferenceEquals(post.Function, replacement), "Explicit source recalculation did not replace function ownership.");
            Compare(tree); Compare(post);
            checks += 2;
        }

        void NoTreesDeletion()
        {
            var context = new LlvmContext();
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage);
            var dead = new LlvmBasicBlock(context, "dead", function);
            dead.Append(new LlvmReturnInstruction(context));
            using var updater = new LlvmDomTreeUpdater(LlvmDomTreeUpdateStrategy.Lazy);
            updater.DeleteBlock(dead);
            Program.Require(updater.HasPendingDeletedBlocks && dead.Parent == function, "No-tree lazy deletion was not queued.");
            updater.Flush();
            Program.Require(dead.Parent is null && !updater.HasPendingDeletedBlocks, "No-tree flush did not perform actual deletion.");
            checks += 2;
        }
    }

    private static (LlvmFunction, LlvmBasicBlock, LlvmBasicBlock, LlvmBasicBlock, LlvmBasicBlock) Diamond(string name)
    {
        var context = new LlvmContext();
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [LlvmIntegerType.Get(context, 1)]),
            LlvmLinkage.InternalLinkage, name);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var left = new LlvmBasicBlock(context, "left", function);
        var right = new LlvmBasicBlock(context, "right", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
        left.Append(new LlvmUncondBrInstruction(exit));
        right.Append(new LlvmUncondBrInstruction(exit));
        exit.Append(new LlvmReturnInstruction(context));
        return (function, entry, left, right, exit);
    }

    private static void Replace(LlvmBasicBlock block, LlvmInstruction terminator)
    {
        block.GetTerminator().EraseFromParent();
        block.Append(terminator);
    }
}
