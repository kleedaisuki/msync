using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class LocalCfgProperties
{
    internal static int Run()
    {
        int checks = 0;
        ConstantBranches();
        Switches();
        EmptyBlocks();
        CriticalEdges();
        BlockSplits();
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }

        void ConstantBranches()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("local-branches", context);
            var i1 = LlvmType.GetInt1Ty(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i1, [i1]), LlvmLinkage.InternalLinkage, "duplicate", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var exit = new LlvmBasicBlock(context, "exit", function);
            var condition = new LlvmBinaryOperator(LlvmOpcode.Xor, function.Arguments[0], LlvmConstantInt.GetTrue(context));
            entry.Append(condition);
            entry.Append(new LlvmCondBrInstruction(condition, exit, exit));
            var phi = new LlvmPhiInstruction(i1, 2);
            phi.AddIncoming(LlvmConstantInt.GetFalse(context), entry);
            phi.AddIncoming(LlvmConstantInt.GetFalse(context), entry);
            exit.Append(phi); exit.Append(new LlvmReturnInstruction(context, phi));
            var tree = new LlvmDominatorTree(function);
            using var updater = new LlvmDomTreeUpdater(tree);
            Require(LlvmLocalCfg.ConstantFoldTerminator(entry, true, updater), "Duplicate branch did not fold.");
            Require(entry.GetTerminator() is LlvmUncondBrInstruction && condition.Parent is null && phi.Parent is null,
                "Duplicate branch did not remove its extra PHI edge/dead condition.");
            CompareTrees(function, tree);

            var constantFunction = LlvmFunction.Create(LlvmFunctionType.Get(i1), LlvmLinkage.InternalLinkage, "constant", module);
            var root = new LlvmBasicBlock(context, "root", constantFunction);
            var left = new LlvmBasicBlock(context, "left", constantFunction);
            var right = new LlvmBasicBlock(context, "right", constantFunction);
            root.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetTrue(context), left, right));
            left.Append(new LlvmReturnInstruction(context, LlvmConstantInt.GetTrue(context)));
            right.Append(new LlvmReturnInstruction(context, LlvmConstantInt.GetFalse(context)));
            var dominators = new LlvmDominatorTree(constantFunction);
            var postDominators = new LlvmDominatorTree(constantFunction, isPostDominator: true);
            using var both = new LlvmDomTreeUpdater(dominators, postDominators, LlvmDomTreeUpdateStrategy.Eager);
            Require(LlvmLocalCfg.ConstantFoldTerminator(root, updater: both), "Constant branch did not fold.");
            CompareTrees(constantFunction, dominators); CompareTrees(constantFunction, postDominators);
        }

        void Switches()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("local-switches", context);
            var i8 = LlvmType.GetInt8Ty(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8]), LlvmLinkage.InternalLinkage, "single-case", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var yes = new LlvmBasicBlock(context, "yes", function);
            var no = new LlvmBasicBlock(context, "no", function);
            var branch = new LlvmSwitchInstruction(function.Arguments[0], no);
            branch.AddCase(LlvmConstantInt.Get(i8, 7), yes);
            branch.AddCase(LlvmConstantInt.Get(i8, 8), no);
            entry.Append(branch);
            yes.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(i8, 1)));
            no.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(i8, 2)));
            Require(LlvmLocalCfg.ConstantFoldTerminator(entry), "Default duplicate switch case did not fold.");
            Require(entry.GetTerminator() is LlvmCondBrInstruction conditional && conditional.Condition is LlvmICmpInstruction compare &&
                ReferenceEquals(compare.Left, function.Arguments[0]) && ((LlvmConstantInt)compare.Right).Value.GetZExtValue() == 7,
                "Single switch case did not become its source equality branch.");

            var constantFunction = LlvmFunction.Create(LlvmFunctionType.Get(i8), LlvmLinkage.InternalLinkage, "constant-switch", module);
            var root = new LlvmBasicBlock(context, "root", constantFunction);
            var exit = new LlvmBasicBlock(context, "exit", constantFunction);
            var fallback = new LlvmBasicBlock(context, "fallback", constantFunction);
            var constantSwitch = new LlvmSwitchInstruction(LlvmConstantInt.Get(i8, 3), fallback);
            constantSwitch.AddCase(LlvmConstantInt.Get(i8, 2), exit);
            constantSwitch.AddCase(LlvmConstantInt.Get(i8, 3), exit);
            root.Append(constantSwitch);
            var phi = new LlvmPhiInstruction(i8, 2);
            phi.AddIncoming(LlvmConstantInt.Get(i8, 9), root); phi.AddIncoming(LlvmConstantInt.Get(i8, 9), root);
            exit.Append(phi); exit.Append(new LlvmReturnInstruction(context, phi));
            fallback.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(i8, 0)));
            var tree = new LlvmDominatorTree(constantFunction);
            using var updater = new LlvmDomTreeUpdater(tree);
            Require(LlvmLocalCfg.ConstantFoldTerminator(root, true, updater), "Constant switch did not fold.");
            Require(root.GetTerminator() is LlvmUncondBrInstruction jump && ReferenceEquals(jump.Destination, exit) && phi.Parent is null,
                "Constant switch lost chosen destination or duplicate-edge PHI cleanup.");
            CompareTrees(constantFunction, tree);
        }

        void EmptyBlocks()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("local-empty-blocks", context);
            var i1 = LlvmType.GetInt1Ty(context); var i8 = LlvmType.GetInt8Ty(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i1, i1]), LlvmLinkage.InternalLinkage, "redirect", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var common = new LlvmBasicBlock(context, "common", function);
            var other = new LlvmBasicBlock(context, "other", function);
            var block = new LlvmBasicBlock(context, "empty", function);
            var exit = new LlvmBasicBlock(context, "exit", function);
            entry.Append(new LlvmCondBrInstruction(function.Arguments[0], common, other));
            common.Append(new LlvmCondBrInstruction(function.Arguments[1], block, exit));
            other.Append(new LlvmUncondBrInstruction(block));
            var inner = new LlvmPhiInstruction(i8, 2);
            inner.AddIncoming(LlvmConstantInt.Get(i8, 3), common); inner.AddIncoming(LlvmConstantInt.Get(i8, 4), other);
            block.Append(inner); block.Append(new LlvmUncondBrInstruction(exit));
            var result = new LlvmPhiInstruction(i8, 2);
            result.AddIncoming(inner, block); result.AddIncoming(LlvmConstantInt.Get(i8, 5), common);
            exit.Append(result); exit.Append(new LlvmReturnInstruction(context, result));
            var tree = new LlvmDominatorTree(function);
            using var updater = new LlvmDomTreeUpdater(tree);
            Require(LlvmLocalCfg.TryToSimplifyUncondBranchFromEmptyBlock(block, updater), "Conflicting common predecessor prevented source partial redirection.");
            Require(block.Parent is not null && inner.Parent is null && ReferenceEquals(other.GetTerminator().GetSuccessor(0), exit) &&
                ReferenceEquals(common.GetTerminator().GetSuccessor(0), block) && result.IncomingCount == 3,
                "Partial empty-block redirection failed to retain the conflicting common path.");
            Require(((LlvmConstantInt)result.GetIncomingValue(result.GetBasicBlockIndex(block))).Value.GetZExtValue() == 3 &&
                ((LlvmConstantInt)result.GetIncomingValue(result.GetBasicBlockIndex(other))).Value.GetZExtValue() == 4,
                "Empty-block PHI redirection changed edge values.");
            CompareTrees(function, tree);

            var simple = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i1]), LlvmLinkage.InternalLinkage, "remove", module);
            var root = new LlvmBasicBlock(context, "root", simple);
            var left = new LlvmBasicBlock(context, "left", simple);
            var right = new LlvmBasicBlock(context, "right", simple);
            var empty = new LlvmBasicBlock(context, "empty", simple);
            var done = new LlvmBasicBlock(context, "done", simple);
            root.Append(new LlvmCondBrInstruction(simple.Arguments[0], left, right));
            left.Append(new LlvmUncondBrInstruction(empty)); right.Append(new LlvmUncondBrInstruction(empty));
            var local = new LlvmPhiInstruction(i8, 2);
            local.AddIncoming(LlvmConstantInt.Get(i8, 1), left); local.AddIncoming(LlvmConstantInt.Get(i8, 2), right);
            empty.Append(local); empty.Append(new LlvmUncondBrInstruction(done));
            var output = new LlvmPhiInstruction(i8, 1); output.AddIncoming(local, empty);
            done.Append(output); done.Append(new LlvmReturnInstruction(context, output));
            var dom = new LlvmDominatorTree(simple);
            using var lazy = new LlvmDomTreeUpdater(dom, LlvmDomTreeUpdateStrategy.Lazy);
            Require(LlvmLocalCfg.TryToSimplifyUncondBranchFromEmptyBlock(empty, lazy), "Killable empty block did not fold.");
            lazy.Flush();
            Require(empty.Parent is null && ReferenceEquals(local.Parent, done) && output.IncomingCount == 2,
                "Removing an empty block failed PHI relocation or lazy physical deletion.");
            CompareTrees(simple, dom);
        }

        void CriticalEdges()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("critical-edges", context);
            var i1 = LlvmType.GetInt1Ty(context); var i8 = LlvmType.GetInt8Ty(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8]), LlvmLinkage.InternalLinkage, "duplicate", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var other = new LlvmBasicBlock(context, "other", function);
            var exit = new LlvmBasicBlock(context, "exit", function);
            var branch = new LlvmSwitchInstruction(function.Arguments[0], other);
            branch.AddCase(LlvmConstantInt.Get(i8, 1), exit); branch.AddCase(LlvmConstantInt.Get(i8, 2), exit);
            entry.Append(branch); other.Append(new LlvmUncondBrInstruction(exit));
            var phi = new LlvmPhiInstruction(i8, 3);
            phi.AddIncoming(LlvmConstantInt.Get(i8, 7), entry); phi.AddIncoming(LlvmConstantInt.Get(i8, 7), entry);
            phi.AddIncoming(LlvmConstantInt.Get(i8, 8), other);
            exit.Append(phi); exit.Append(new LlvmReturnInstruction(context, phi));
            var tree = new LlvmDominatorTree(function);
            var postTree = new LlvmDominatorTree(function, isPostDominator: true);
            var split = LlvmBasicBlockUtils.SplitCriticalEdge(branch, 1,
                new LlvmCriticalEdgeSplittingOptions(tree, postTree: postTree).SetMergeIdenticalEdges());
            Require(split is not null && ReferenceEquals(branch.GetCaseDestination(0), split) && ReferenceEquals(branch.GetCaseDestination(1), split),
                "Critical-edge splitting did not merge identical switch destinations.");
            Require(phi.IncomingCount == 2 && phi.GetBasicBlockIndex(split!) >= 0 && phi.GetBasicBlockIndex(entry) < 0,
                "Critical-edge splitting did not repair exact PHI edge multiplicity.");
            CompareTrees(function, tree); CompareTrees(function, postTree);

            var loopFunction = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i1, i1]), LlvmLinkage.InternalLinkage, "loop-exits", module);
            var root = new LlvmBasicBlock(context, "root", loopFunction);
            var header = new LlvmBasicBlock(context, "header", loopFunction);
            var body = new LlvmBasicBlock(context, "body", loopFunction);
            var done = new LlvmBasicBlock(context, "done", loopFunction);
            root.Append(new LlvmUncondBrInstruction(header));
            header.Append(new LlvmCondBrInstruction(loopFunction.Arguments[0], body, done));
            body.Append(new LlvmCondBrInstruction(loopFunction.Arguments[1], header, done));
            var output = new LlvmPhiInstruction(i8, 2);
            output.AddIncoming(LlvmConstantInt.Get(i8, 3), header); output.AddIncoming(LlvmConstantInt.Get(i8, 4), body);
            done.Append(output); done.Append(new LlvmReturnInstruction(context, output));
            var dominators = new LlvmDominatorTree(loopFunction);
            var loops = new LlvmLoopInfo(loopFunction, dominators);
            var loop = loops.GetLoopFor(header)!;
            var exitSplit = LlvmBasicBlockUtils.SplitCriticalEdge(header.GetTerminator(), 1,
                new LlvmCriticalEdgeSplittingOptions(dominators, loops).SetPreserveLCSSA());
            Require(exitSplit is not null && exitSplit.FirstInstruction is LlvmPhiInstruction && !loop.Contains(exitSplit),
                "Critical loop exit did not receive its LCSSA PHI outside the loop.");
            Require(output.IncomingCount == 2 && Enumerable.Range(0, output.IncomingCount).All(index =>
                output.GetIncomingValue(index) is LlvmPhiInstruction incoming && ReferenceEquals(incoming.Parent, output.GetIncomingBlock(index))),
                "Other loop exits were not resplit and closed in LCSSA form.");
            CompareTrees(loopFunction, dominators);
        }

        void BlockSplits()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("block-split", context);
            var i8 = LlvmType.GetInt8Ty(context); var i1 = LlvmType.GetInt1Ty(context);
            var pointer = LlvmPointerType.Get(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [pointer, i1]), LlvmLinkage.InternalLinkage, "memory", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var other = new LlvmBasicBlock(context, "other", function);
            var exit = new LlvmBasicBlock(context, "exit", function);
            var store = new LlvmStoreInstruction(LlvmConstantInt.Get(i8, 7), function.Arguments[0], new(1));
            var load = new LlvmLoadInstruction(i8, function.Arguments[0], new(1));
            entry.Append(store); entry.Append(load);
            entry.Append(new LlvmCondBrInstruction(function.Arguments[1], exit, other));
            other.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(i8, 8), function.Arguments[0], new(1)));
            other.Append(new LlvmUncondBrInstruction(exit));
            var phi = new LlvmPhiInstruction(i8, 2);
            phi.AddIncoming(load, entry); phi.AddIncoming(LlvmConstantInt.Get(i8, 8), other);
            exit.Append(phi); exit.Append(new LlvmReturnInstruction(context, phi));
            var tree = new LlvmDominatorTree(function);
            var postTree = new LlvmDominatorTree(function, isPostDominator: true);
            var alias = new LlvmAAResults(function, tree: tree);
            var memory = new LlvmMemorySsa(function, alias, tree);
            var memoryUpdater = new LlvmMemorySsaUpdater(memory);
            using var updater = new LlvmDomTreeUpdater(tree, postTree, LlvmDomTreeUpdateStrategy.Eager);
            var split = LlvmBasicBlockUtils.SplitBlock(entry, load, updater, memoryUpdater: memoryUpdater);
            Require(ReferenceEquals(load.Parent, split) && ReferenceEquals(store.Parent, entry) &&
                phi.GetBasicBlockIndex(entry) < 0 && phi.GetBasicBlockIndex(split) >= 0,
                "Basic-block split lost instruction ownership or successor PHI edge identity.");
            Require(ReferenceEquals(memory.GetMemoryAccess(load)!.Block, split) &&
                ReferenceEquals(memory.GetMemoryAccess(store)!.Block, entry),
                "Basic-block split left MemorySSA accesses in the original block.");
            Require(memory.GetMemoryAccess(exit)!.GetBasicBlockIndex(split) >= 0 &&
                memory.GetMemoryAccess(exit)!.GetBasicBlockIndex(entry) < 0,
                "Basic-block split did not revector successor MemoryPhi edges.");
            CompareTrees(function, tree); CompareTrees(function, postTree);

            var loopFunction = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i1]), LlvmLinkage.InternalLinkage, "loop-body", module);
            var root = new LlvmBasicBlock(context, "root", loopFunction);
            var header = new LlvmBasicBlock(context, "header", loopFunction);
            var body = new LlvmBasicBlock(context, "body", loopFunction);
            var done = new LlvmBasicBlock(context, "done", loopFunction);
            root.Append(new LlvmUncondBrInstruction(header));
            var induction = new LlvmPhiInstruction(i8, 2);
            induction.AddIncoming(LlvmConstantInt.Get(i8, 0), root);
            var next = new LlvmBinaryOperator(LlvmOpcode.Add, induction, LlvmConstantInt.Get(i8, 1));
            induction.AddIncoming(next, body);
            header.Append(induction); header.Append(new LlvmCondBrInstruction(loopFunction.Arguments[0], body, done));
            body.Append(next); body.Append(new LlvmUncondBrInstruction(header));
            done.Append(new LlvmReturnInstruction(context, induction));
            var dominators = new LlvmDominatorTree(loopFunction);
            var loops = new LlvmLoopInfo(loopFunction, dominators);
            var loop = loops.GetLoopFor(body)!;
            var bodySplit = LlvmBasicBlockUtils.SplitBlock(body, next, dominators, loops);
            Require(loop.Contains(bodySplit) && ReferenceEquals(loop.GetLoopLatch(), bodySplit) &&
                induction.GetBasicBlockIndex(bodySplit) >= 0 && induction.GetBasicBlockIndex(body) < 0,
                "Direct-DT block split did not preserve loop membership/latch and backedge PHIs.");
            CompareTrees(loopFunction, dominators);
        }

        void CompareTrees(LlvmFunction function, LlvmDominatorTree actual)
        {
            var expected = new LlvmDominatorTree(function, isPostDominator: actual.IsPostDominator);
            foreach (var block in function.BasicBlocks)
            {
                Require(actual.GetNode(block)?.Level == expected.GetNode(block)?.Level &&
                    ReferenceEquals(actual.GetNode(block)?.IDom?.Block, expected.GetNode(block)?.IDom?.Block),
                    $"Local CFG update changed {(actual.IsPostDominator ? "post" : "")}dominance at {block.Name}.");
            }
        }
    }
}
