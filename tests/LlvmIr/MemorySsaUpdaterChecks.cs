using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class MemorySsaUpdaterChecks
{
    internal static int Run()
    {
        Diamond();
        LoopInsertion();
        CfgUpdates();
        return 12;
    }

    private static void Diamond()
    {
        LlvmContext context = new();
        var number = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(number, [LlvmType.GetInt1Ty(context)]), LlvmLinkage.InternalLinkage,
            parent: new LlvmModule("memoryssa-updater-diamond", context));
        LlvmBasicBlock entry = new(context, "entry", function), left = new(context, "left", function),
            right = new(context, "right", function), merge = new(context, "merge", function);
        var one = LlvmConstantInt.Get(number, 1);
        var address = new LlvmAllocaInstruction(number, one);
        var initial = new LlvmStoreInstruction(one, address);
        entry.Append(address); entry.Append(initial); entry.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
        var leftStore = new LlvmStoreInstruction(LlvmConstantInt.Get(number, 2), address);
        var rightStore = new LlvmStoreInstruction(LlvmConstantInt.Get(number, 3), address);
        left.Append(leftStore); left.Append(new LlvmUncondBrInstruction(merge));
        right.Append(rightStore); right.Append(new LlvmUncondBrInstruction(merge));
        var load = new LlvmLoadInstruction(number, address);
        merge.Append(load); merge.Append(new LlvmReturnInstruction(context, load));
        var tree = new LlvmDominatorTree(function);
        var aa = new LlvmAAResults(function, tree: tree);
        using var graph = new LlvmMemorySsa(function, aa, tree);
        var updater = new LlvmMemorySsaUpdater(graph);
        Program.Require(graph.GetMemoryAccess(merge) is not null, "Initial diamond needs a real memory PHI.");
        var leftAccess = graph.GetMemoryAccess(leftStore)!;
        leftStore.RemoveFromParent(); entry.InsertBefore(leftStore, entry.GetTerminator());
        updater.MoveToPlace(leftAccess, entry, LlvmMemorySsaInsertionPlace.BeforeTerminator);
        CompareToFresh(graph, aa, tree, load);
        updater.RemoveMemoryAccess(rightStore, true); rightStore.EraseFromParent();
        Program.Require(graph.GetMemoryAccess(merge) is null, "Removing the last branch def must eliminate its trivial PHI.");
        CompareToFresh(graph, aa, tree, load);
        var inserted = new LlvmStoreInstruction(LlvmConstantInt.Get(number, 4), address);
        merge.InsertBefore(inserted, load);
        var newDef = (LlvmMemoryDef)updater.CreateMemoryAccessBefore(inserted, null, graph.GetMemoryAccess(load)!);
        updater.InsertDef(newDef, true);
        CompareToFresh(graph, aa, tree, load);
        var loadAccess = graph.GetMemoryAccess(load)!;
        load.RemoveFromParent(); entry.InsertBefore(load, entry.GetTerminator());
        updater.MoveAfter(loadAccess, leftAccess);
        CompareToFresh(graph, aa, tree, load);
    }

    private static void LoopInsertion()
    {
        LlvmContext context = new();
        var number = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(number, [LlvmType.GetInt1Ty(context)]), LlvmLinkage.InternalLinkage,
            parent: new LlvmModule("memoryssa-updater-loop", context));
        LlvmBasicBlock entry = new(context, "entry", function), header = new(context, "header", function),
            body = new(context, "body", function), exit = new(context, "exit", function);
        var one = LlvmConstantInt.Get(number, 1);
        var address = new LlvmAllocaInstruction(number, one);
        entry.Append(address); entry.Append(new LlvmStoreInstruction(one, address)); entry.Append(new LlvmUncondBrInstruction(header));
        header.Append(new LlvmCondBrInstruction(function.Arguments[0], body, exit));
        var load = new LlvmLoadInstruction(number, address);
        body.Append(load); body.Append(new LlvmUncondBrInstruction(header));
        exit.Append(new LlvmReturnInstruction(context, one));
        var tree = new LlvmDominatorTree(function);
        var aa = new LlvmAAResults(function, tree: tree);
        using var graph = new LlvmMemorySsa(function, aa, tree);
        var updater = new LlvmMemorySsaUpdater(graph);
        var inserted = new LlvmStoreInstruction(LlvmConstantInt.Get(number, 2), address);
        body.InsertBefore(inserted, body.GetTerminator());
        var definition = (LlvmMemoryDef)updater.CreateMemoryAccessInBB(inserted, null, body, LlvmMemorySsaInsertionPlace.End)!;
        updater.InsertDef(definition, true);
        Program.Require(graph.GetMemoryAccess(header) is { } phi && ReferenceEquals(phi.GetIncomingValueForBlock(body), definition),
            "First loop-body def insertion must create and wire a genuine loop memory PHI.");
        CompareToFresh(graph, aa, tree, load);
    }

    private static void CompareToFresh(LlvmMemorySsa graph, LlvmAAResults aa, LlvmDominatorTree tree, LlvmLoadInstruction load)
    {
        using var rebuilt = new LlvmMemorySsa(tree.Function, aa, tree);
        var actual = graph.GetWalker().GetClobberingMemoryAccess(graph.GetMemoryAccess(load)!);
        var expected = rebuilt.GetWalker().GetClobberingMemoryAccess(rebuilt.GetMemoryAccess(load)!);
        bool same = actual is LlvmMemoryPhi actualPhi && expected is LlvmMemoryPhi expectedPhi
            ? ReferenceEquals(actualPhi.Block, expectedPhi.Block)
            : actual is LlvmMemoryUseOrDef actualDef && expected is LlvmMemoryUseOrDef expectedDef
                && ReferenceEquals(actualDef.MemoryInstruction, expectedDef.MemoryInstruction);
        Program.Require(same, "Incremental memory graph clobber must match fresh construction for the changed real CFG.");
    }

    private static void CfgUpdates()
    {
        LlvmContext context = new();
        var number = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(number, [LlvmType.GetInt1Ty(context)]), LlvmLinkage.InternalLinkage,
            parent: new LlvmModule("memoryssa-updater-cfg", context));
        LlvmBasicBlock entry = new(context, "entry", function), left = new(context, "left", function),
            right = new(context, "right", function), sink = new(context, "sink", function), exit = new(context, "exit", function);
        var one = LlvmConstantInt.Get(number, 1);
        var address = new LlvmAllocaInstruction(number, one);
        entry.Append(address); entry.Append(new LlvmStoreInstruction(one, address));
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
        left.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(number, 2), address)); left.Append(new LlvmUncondBrInstruction(exit));
        right.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(number, 3), address)); right.Append(new LlvmUncondBrInstruction(sink));
        var load = new LlvmLoadInstruction(number, address);
        sink.Append(load); sink.Append(new LlvmReturnInstruction(context, load));
        exit.Append(new LlvmReturnInstruction(context, one));
        var tree = new LlvmDominatorTree(function);
        var aa = new LlvmAAResults(function, tree: tree);
        using var graph = new LlvmMemorySsa(function, aa, tree);
        var updater = new LlvmMemorySsaUpdater(graph);
        graph.GetWalker().GetClobberingMemoryAccess(graph.GetMemoryAccess(load)!);

        left.GetTerminator().EraseFromParent();
        left.Append(new LlvmCondBrInstruction(function.Arguments[0], exit, sink));
        updater.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, left, sink)], tree, updateDtFirst: true);
        CompareToFresh(graph, aa, tree, load);

        right.GetTerminator().EraseFromParent(); right.Append(new LlvmUncondBrInstruction(exit));
        LlvmCfgUpdate[] mixed = [new(LlvmCfgUpdateKind.Delete, right, sink), new(LlvmCfgUpdateKind.Insert, right, exit)];
        updater.ApplyUpdates(mixed, tree, updateDtFirst: true);
        CompareToFresh(graph, aa, tree, load);

        right.GetTerminator().EraseFromParent(); right.Append(new LlvmUncondBrInstruction(sink));
        LlvmCfgUpdate[] reverse = [new(LlvmCfgUpdateKind.Delete, right, exit), new(LlvmCfgUpdateKind.Insert, right, sink)];
        tree.ApplyUpdates(reverse);
        updater.ApplyUpdates(reverse, tree);
        CompareToFresh(graph, aa, tree, load);

        left.GetTerminator().EraseFromParent(); left.Append(new LlvmUncondBrInstruction(exit));
        updater.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, left, sink)], tree, updateDtFirst: true);
        CompareToFresh(graph, aa, tree, load);
    }
}
