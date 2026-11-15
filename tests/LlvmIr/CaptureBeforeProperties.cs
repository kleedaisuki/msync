using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CaptureBeforeProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("capture-before", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [LlvmType.GetInt1Ty(context)]),
            LlvmLinkage.InternalLinkage, "capture_before", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var header = new LlvmBasicBlock(context, "header", function);
        var latch = new LlvmBasicBlock(context, "latch", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        var dead = new LlvmBasicBlock(context, "dead", function);
        var one = LlvmConstantInt.Get(integer, 1);
        var slot = new LlvmAllocaInstruction(pointer, one);
        var captured = new LlvmAllocaInstruction(integer, one);
        var local = new LlvmAllocaInstruction(integer, one);
        var deadOnly = new LlvmAllocaInstruction(integer, one);
        var before = new LlvmLoadInstruction(integer, captured);
        var escape = new LlvmStoreInstruction(captured, slot);
        var after = new LlvmLoadInstruction(integer, captured);
        entry.Append(slot); entry.Append(captured); entry.Append(local); entry.Append(deadOnly);
        entry.Append(before); entry.Append(escape); entry.Append(after); entry.Append(new LlvmUncondBrInstruction(header));
        var bodyLoad = new LlvmLoadInstruction(integer, local);
        header.Append(bodyLoad); header.Append(new LlvmCondBrInstruction(function.Arguments[0], latch, exit));
        latch.Append(new LlvmStoreInstruction(captured, slot)); latch.Append(new LlvmUncondBrInstruction(header));
        exit.Append(new LlvmReturnInstruction(context, one));
        dead.Append(new LlvmStoreInstruction(deadOnly, slot)); dead.Append(new LlvmReturnInstruction(context, one));
        var tree = new LlvmDominatorTree(function);
        var loops = new LlvmLoopInfo(function, tree);
        var loop = loops.GetLoopFor(header)!;
        Program.Require(!LlvmCaptureTracking.PointerMayBeCapturedBefore(captured, true, before, tree),
            "Later entry-block captures cannot reach an earlier entry-block instruction.");
        Program.Require(LlvmCaptureTracking.PointerMayBeCapturedBefore(captured, true, after, tree),
            "Earlier entry-block captures reach the later load.");
        Program.Require(!LlvmCaptureTracking.PointerMayBeCapturedBefore(captured, true, escape, tree, includeInstruction: false) &&
            LlvmCaptureTracking.PointerMayBeCapturedBefore(captured, true, escape, tree, includeInstruction: true),
            "IncludeI controls whether the queried instruction itself captures.");
        Program.Require(!LlvmCaptureTracking.PointerMayBeCapturedBefore(deadOnly, true, after, tree),
            "Captures in unreachable blocks are pruned only after finding a capture candidate.");
        Program.Require(!LlvmLicmObjectFacts.IsNotCapturedBeforeOrInLoop(captured, loop, tree),
            "A capture on the loop backedge is reachable before the next header terminator.");
        Program.Require(LlvmLicmObjectFacts.IsThreadLocalObject(local, loop, tree),
            "An identified function-local object without capture before or inside the loop is thread-local.");
        Program.Require(LlvmLicmObjectFacts.IsNotVisibleOnUnwindInLoop(captured, loop, tree),
            "The source alloca rule does not depend on whether the object was captured.");
        Program.Require(LlvmLicmObjectFacts.IsWritableObject(local, out bool explicitlyOnly) && !explicitlyOnly,
            "An alloca supplies writable object provenance without an explicit-dereferenceability restriction.");
        var global = new LlvmGlobalVariable(module, integer, false, LlvmLinkage.InternalLinkage, one, "data");
        Program.Require(!LlvmLicmObjectFacts.IsWritableObject(global, out _),
            "Physical writability of a global is not the source writable-provenance rule.");
        Program.Require(module.GetThreadModel() == LlvmThreadModel.Posix &&
            !LlvmLicmObjectFacts.IsThreadLocalObject(global, loop, tree),
            "An absent module thread-model flag uses the pinned target default, not a host guess.");
        module.SetThreadModel(LlvmThreadModel.Single);
        Program.Require(LlvmLicmObjectFacts.IsThreadLocalObject(global, loop, tree),
            "The explicit single-thread module model supplies the source fallback.");
        var returning = LlvmFunction.Create(LlvmFunctionType.Get(pointer, []), LlvmLinkage.InternalLinkage, "returning", module);
        var returningEntry = new LlvmBasicBlock(context, "entry", returning);
        var returned = new LlvmAllocaInstruction(integer, one);
        var ret = new LlvmReturnInstruction(context, returned);
        returningEntry.Append(returned); returningEntry.Append(ret);
        var returningTree = new LlvmDominatorTree(returning);
        Program.Require(!LlvmCaptureTracking.PointerMayBeCapturedBefore(returned, false, ret, returningTree, includeInstruction: true) &&
            LlvmCaptureTracking.PointerMayBeCapturedBefore(returned, true, ret, returningTree, includeInstruction: true),
            "The context-sensitive tracker preserves the return-capture policy.");
        return 12;
    }
}
