using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class DominatorMutationProperties
{
    internal static int Run()
    {
        int comparisons = 0;
        var context = new LlvmContext();
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "mutable-domtree");
        var entry = new LlvmBasicBlock(context, "entry", function);
        var left = new LlvmBasicBlock(context, "left", function);
        var right = new LlvmBasicBlock(context, "right", function);
        var join = new LlvmBasicBlock(context, "join", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        entry.Append(new LlvmCondBrInstruction(LlvmUndefValue.Get(LlvmIntegerType.Get(context, 1)), left, right));
        left.Append(new LlvmUncondBrInstruction(join));
        right.Append(new LlvmUncondBrInstruction(join));
        join.Append(new LlvmUncondBrInstruction(exit));
        exit.Append(new LlvmReturnInstruction(context));
        var tree = new LlvmDominatorTree(function);
        var metrics = new Dictionary<string, ulong>();
        tree.UpdateDFSNumbers();

        var split = new LlvmBasicBlock(context, "split", function, join);
        split.Append(new LlvmUncondBrInstruction(join));
        left.GetTerminator().SetSuccessor(0, split);
        right.GetTerminator().SetSuccessor(0, split);
        tree.SplitBlock(split, metrics);
        Compare();
        Program.Require(ReferenceEquals(tree.GetNode(join)!.IDom, tree.GetNode(split)), "SplitBlock did not reparent its dominated successor.");

        right.GetTerminator().SetSuccessor(0, left);
        tree.ChangeImmediateDominator(split, left, metrics);
        Compare();
        Program.Require(tree.GetNode(exit)!.Level == 4, "Changing IDom did not propagate descendant levels.");

        var newRoot = new LlvmBasicBlock(context, "new-root", function, entry);
        newRoot.Append(new LlvmUncondBrInstruction(entry));
        tree.SetNewRoot(newRoot, metrics);
        Compare();
        Program.Require(tree.GetNode(exit)!.Level == 5, "SetNewRoot did not update the old root subtree.");

        var leaf = new LlvmBasicBlock(context, "new-leaf", function);
        leaf.Append(new LlvmReturnInstruction(context));
        exit.GetTerminator().EraseFromParent();
        exit.Append(new LlvmUncondBrInstruction(leaf));
        tree.AddNewBlock(leaf, exit, metrics);
        Compare();
        exit.GetTerminator().EraseFromParent();
        exit.Append(new LlvmReturnInstruction(context));
        tree.EraseNode(leaf, metrics);
        leaf.RemoveFromParent();
        Compare();
        Program.Require(metrics.GetValueOrDefault("domtree.mutation.levels.updated") > 0
            && metrics.GetValueOrDefault("domtree.mutation.children.unlink_steps") > 0,
            "Dominator mutation counters lost actual level/unlink work.");
        return comparisons;

        void Compare()
        {
            var expected = new LlvmDominatorTree(function);
            foreach (var a in function.BasicBlocks)
            {
                Program.Require(tree.GetNode(a)?.Level == expected.GetNode(a)?.Level, $"Mutated dominator level differs at {a.Name}.");
                Program.Require(ReferenceEquals(tree.GetNode(a)?.IDom?.Block, expected.GetNode(a)?.IDom?.Block), $"Mutated IDom differs at {a.Name}.");
                foreach (var b in function.BasicBlocks)
                {
                    Program.Require(tree.Dominates(a, b) == expected.Dominates(a, b), $"Mutated dominance differs at {a.Name}/{b.Name}.");
                    comparisons++;
                }
            }
            tree.UpdateDFSNumbers();
        }
    }
}
