using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class BlockLifetimeProperties
{
    internal static int Run()
    {
        int checks = 0;
        CheckDeletion();
        CheckLazyCallbackOrder();
        CheckEagerCallbackOrder();
        CheckLinksAndRenumbering();
        return checks;

        void CheckDeletion()
        {
            LlvmContext context = new();
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var header = new LlvmBasicBlock(context, "header", function);
            entry.Append(new LlvmReturnInstruction(context));
            LlvmConstantInt one = LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1);
            var first = new LlvmBinaryOperator(LlvmOpcode.Add, one, one);
            var second = new LlvmBinaryOperator(LlvmOpcode.Sub, first, one);
            first.Left = second;
            header.Append(first); header.Append(second); header.Append(new LlvmUnreachableInstruction(context));
            using var weakHeader = new LlvmWeakValueHandle(header);
            using var weakFirst = new LlvmWeakValueHandle(first);
            bool callbackSawDestruction = false;
            using var callback = new BlockProbe(header, block => callbackSawDestruction = block.Parent is null && block.Instructions.Count == 0 && weakFirst.Value is null);
            header.RemoveFromParent();
            Require(ReferenceEquals(weakHeader.Value, header) && first.Parent == header, "Detachment is not block destruction.");
            header.InsertInto(function);
            header.EraseFromParent();
            Require(weakHeader.Value is null && weakFirst.Value is null && callbackSawDestruction,
                "Explicit block deletion drops cyclic instruction references and nulls WeakVH loop headers immediately, before GC.");
            var dead = new LlvmBasicBlock(context, "dead", function);
            dead.Append(new LlvmReturnInstruction(context));
            using var weakDead = new LlvmWeakValueHandle(dead);
            LlvmBasicBlockUtils.DeleteDeadBlock(dead);
            Require(weakDead.Value is null && dead.Parent is null, "BasicBlockUtils actual deletion reaches the central Value destructor.");
        }

        void CheckLazyCallbackOrder()
        {
            LlvmContext context = new();
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage);
            var dead = new LlvmBasicBlock(context, "lazy", function);
            dead.Append(new LlvmReturnInstruction(context));
            using var updater = new LlvmDomTreeUpdater(LlvmDomTreeUpdateStrategy.Lazy);
            using var olderWeak = new LlvmWeakValueHandle(dead);
            LlvmWeakValueHandle? newerWeak = null;
            bool callbackOrdered = false;
            updater.CallbackDeleteBlock(dead, block => callbackOrdered = block.Parent is null && block.Instructions.Count == 0 &&
                ReferenceEquals(olderWeak.Value, block) && newerWeak!.Value is null);
            using (newerWeak = new LlvmWeakValueHandle(dead))
            {
                Require(ReferenceEquals(olderWeak.Value, dead), "Lazy deletion retains block identity until flush.");
                updater.Flush();
                Require(callbackOrdered && olderWeak.Value is null, "Lazy callback uses source CallbackVH ordering among newer and older WeakVH registrations.");
            }
        }

        void CheckEagerCallbackOrder()
        {
            LlvmContext context = new();
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage);
            var dead = new LlvmBasicBlock(context, "eager", function);
            dead.Append(new LlvmReturnInstruction(context));
            using var weak = new LlvmWeakValueHandle(dead);
            using var updater = new LlvmDomTreeUpdater(LlvmDomTreeUpdateStrategy.Eager);
            bool beforeDestruction = false;
            updater.CallbackDeleteBlock(dead, block => beforeDestruction = block.Parent is null && block.Instructions.Count == 1 && ReferenceEquals(weak.Value, block));
            Require(beforeDestruction && weak.Value is null, "Eager callback retains source detach/callback/destructor ordering.");
        }

        void CheckLinksAndRenumbering()
        {
            LlvmContext context = new();
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var gap = new LlvmBasicBlock(context, "gap", function);
            var body = new LlvmBasicBlock(context, "body", function);
            var exit = new LlvmBasicBlock(context, "exit", function);
            entry.Append(new LlvmUncondBrInstruction(body)); body.Append(new LlvmUncondBrInstruction(exit));
            gap.Append(new LlvmReturnInstruction(context)); exit.Append(new LlvmReturnInstruction(context));
            gap.MoveAfter(body); CheckLinks(function);
            gap.RemoveFromParent(); CheckLinks(function);
            gap.InsertInto(function, exit); CheckLinks(function);
            gap.EraseFromParent(); CheckLinks(function);
            var tree = new LlvmDominatorTree(function);
            var post = new LlvmDominatorTree(function, isPostDominator: true);
            tree.UpdateDFSNumbers(); post.UpdateDFSNumbers();
            LlvmDominatorTreeNode bodyNode = tree.GetNode(body)!;
            LlvmDominatorTreeNode exitNode = tree.GetNode(exit)!;
            LlvmDominatorTreeNode postBodyNode = post.GetNode(body)!;
            uint dfsIn = bodyNode.DFSNumIn, epoch = function.GetBlockNumberEpoch();
            var metrics = new Dictionary<string, ulong>();
            function.RenumberBlocks(metrics);
            tree.UpdateBlockNumbers(metrics); post.UpdateBlockNumbers(metrics);
            Require(function.GetMaxBlockNumber() == 3 && function.GetBlockNumberEpoch() == epoch + 1 &&
                entry.Number == 0 && body.Number == 1 && exit.Number == 2, "Renumbering compacts current ilist order and advances the source numbering epoch.");
            Require(ReferenceEquals(tree.GetNode(body), bodyNode) && ReferenceEquals(tree.GetNode(exit), exitNode) &&
                ReferenceEquals(post.GetNode(body), postBodyNode) && bodyNode.DFSNumIn == dfsIn && tree.Dominates(body, exit),
                "Block-number synchronization preserves forward/post tree nodes, edges and DFS numbers without recalculation.");
            Require(metrics.GetValueOrDefault("function.block_numbers.blocks") == 3 && metrics.GetValueOrDefault("domtree.block_numbers.updated") == 2,
                "Renumbering metrics report actual block and tree synchronization work.");
        }

        void CheckLinks(LlvmFunction function)
        {
            LlvmBasicBlock? cursor = function.BasicBlocks.Count == 0 ? null : function.BasicBlocks[0];
            for (int index = 0; index < function.BasicBlocks.Count; ++index)
            {
                LlvmBasicBlock expected = function.BasicBlocks[index];
                Require(ReferenceEquals(cursor, expected) && ReferenceEquals(expected.PreviousNode, index == 0 ? null : function.BasicBlocks[index - 1]),
                    "Canonical mutable block-list adjacency agrees with indexed view after splice, detach, insertion and erase.");
                cursor = cursor!.NextNode;
            }
            Require(cursor is null, "Block-list tail has no successor.");
        }
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    private sealed class BlockProbe(LlvmBasicBlock block, Action<LlvmBasicBlock> deleted) : LlvmCallbackValueHandle(block)
    {
        protected override void Deleted() { deleted(block); base.Deleted(); }
    }
}
