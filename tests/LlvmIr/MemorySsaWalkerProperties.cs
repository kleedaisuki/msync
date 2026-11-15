using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class MemorySsaWalkerProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("memoryssa-walker", context);
        var number = LlvmIntegerType.Get(context, 32);
        var boolean = LlvmType.GetInt1Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(number, [boolean]), LlvmLinkage.InternalLinkage, "independent-field-diamond", module);
        LlvmBasicBlock entry = new(context, "entry", function), left = new(context, "left", function),
            right = new(context, "right", function), merge = new(context, "merge", function);
        var one = LlvmConstantInt.Get(number, 1);
        var a = new LlvmAllocaInstruction(number, one);
        var b = new LlvmAllocaInstruction(number, one);
        var initialA = new LlvmStoreInstruction(one, a);
        entry.Append(a); entry.Append(b); entry.Append(initialA);
        entry.Append(new LlvmStoreInstruction(one, b));
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
        left.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(number, 2), b));
        left.Append(new LlvmUncondBrInstruction(merge));
        right.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(number, 3), b));
        right.Append(new LlvmUncondBrInstruction(merge));
        var loadA = new LlvmLoadInstruction(number, a);
        merge.Append(loadA); merge.Append(new LlvmReturnInstruction(context, loadA));
        var tree = new LlvmDominatorTree(function);
        var aa = new LlvmAAResults(function, tree: tree);
        using var graph = new LlvmMemorySsa(function, aa, tree);
        var walker = graph.GetWalker();
        var access = graph.GetMemoryAccess(loadA)!;
        var initialAccess = graph.GetMemoryAccess(initialA)!;
        Program.Require(graph.GetMemoryAccess(merge) is not null, "A diamond with branch memory definitions must have a real MemoryPhi.");
        Dictionary<string, ulong> metrics = [];
        Program.Require(ReferenceEquals(walker.GetClobberingMemoryAccess(access, metrics), initialAccess),
            "Independent allocation stores on both paths must not clobber the merged load's allocation.");
        Program.Require(metrics.GetValueOrDefault("memoryssa.walker.phi_paths") == 2 &&
            metrics.GetValueOrDefault("memoryssa.walker.aa_queries") != 0,
            "The diamond result must come from actual PHI paths and AA queries, not a direct-def shortcut.");
        uint budget = 1;
        var batch = new LlvmBatchAAResults(aa);
        Program.Require(ReferenceEquals(walker.GetClobberingMemoryAccess(access, batch, ref budget, metrics), initialAccess),
            "A cached optimized use must keep its source result even when a later query has a smaller walk budget.");
        walker.InvalidateInfo(access);
        Program.Require(!access.IsOptimized, "Source cache invalidation must clear the access's optimized ID.");
        budget = 1;
        var phi = graph.GetMemoryAccess(merge)!;
        Program.Require(ReferenceEquals(walker.GetClobberingMemoryAccess(phi, LlvmMemoryLocation.Get(loadA), batch, ref budget, metrics), phi) && budget == 0,
            "An explicit location query with exhausted AA walk budget must preserve the blocking MemoryPhi.");
        return 5;
    }
}
