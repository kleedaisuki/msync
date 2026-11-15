using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class HeapAllocationFactsProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("heap-allocation-facts", context);
        var i64 = LlvmType.GetInt64Ty(context);
        var pointerType = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [pointerType]), LlvmLinkage.InternalLinkage, "main", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var header = new LlvmBasicBlock(context, "header", function);
        var body = new LlvmBasicBlock(context, "body", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        var heap = new LlvmHeapAllocInst(LlvmConstantInt.Get(i64, 24), new(16));
        var second = new LlvmHeapAllocInst(LlvmConstantInt.Get(i64, 24), new(16));
        var frame = new LlvmAllocaInstruction(i64, LlvmConstantInt.Get(i64, 1));
        var pair = LlvmStructType.Get(context, [i64, i64]);
        var field = new LlvmGetElementPtrInstruction(pair, heap, [LlvmConstantInt.Get(i64, 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1)], LlvmGepNoWrapFlags.InBounds);
        var outside = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(context), heap, [LlvmConstantInt.Get(i64, 24)]);
        entry.Append(heap); entry.Append(second); entry.Append(frame); entry.Append(field); entry.Append(outside);
        entry.Append(new LlvmUncondBrInstruction(header));
        header.Append(new LlvmCondBrInstruction(LlvmUndefValue.Get(LlvmIntegerType.Get(context, 1)), body, exit));
        var load = new LlvmLoadInstruction(i64, field, new(8));
        body.Append(load); body.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(i64, 7), field, new(8)));
        body.Append(new LlvmUncondBrInstruction(header)); exit.Append(new LlvmReturnInstruction(context));
        var tree = new LlvmDominatorTree(function);
        var loops = new LlvmLoopInfo(function, tree);
        var loop = loops.GetLoopFor(header)!;
        var metrics = new Dictionary<string, ulong>();
        var aa = new LlvmAAResults(function, module.DataLayout, tree, metrics);
        int checks = 0;
        Require(aa.Alias(heap, second) == LlvmAliasKind.NoAlias && aa.Alias(heap, frame) == LlvmAliasKind.NoAlias
            && aa.Alias(heap, function.Arguments[0]) == LlvmAliasKind.NoAlias, "Fresh heap identity was lost against another allocation or pre-existing argument.");
        Require(LlvmObjectSize.GetBaseObjectSize(heap, module.DataLayout, metrics: metrics)?.GetFixedValue() == 24
            && LlvmObjectSize.GetBaseObjectSize(heap, module.DataLayout, roundToAlign: true, metrics: metrics)?.GetFixedValue() == 32,
            "Heap base size did not use the actual ByteSize/alignment operands.");
        Require(LlvmValueTracking.GetPointerDereferenceableBytes(heap, module.DataLayout, out bool nullable, out bool freed, metrics) == 24
            && !nullable && !freed, "Successful heap allocation lacks its non-null/live extent contract.");
        Require(LlvmValueTracking.GetPointerAlignment(heap, module.DataLayout, metrics).Value == 16
            && LlvmKnownBitsAnalysis.Compute(heap, metrics: metrics).CountMinTrailingZeros() == 4,
            "Heap alignment did not reach pointer and KnownBits queries.");
        Require(LlvmKnownBitsAnalysis.IsKnownNonZero(heap, metrics: metrics)
            && LlvmValueTracking.IsGuaranteedNotToBePoison(heap, metrics: metrics)
            && LlvmValueTracking.IsGuaranteedNotToBeUndef(heap, metrics: metrics), "A successful allocation pointer was treated as nullable/undefined.");
        Require(LlvmValueTracking.IsDereferenceableAndAlignedPointer(field, i64, new(8), module.DataLayout, metrics: metrics)
            && !LlvmValueTracking.IsDereferenceableAndAlignedPointer(outside, i64, new(8), module.DataLayout, metrics: metrics),
            "Field byte facts lost the allocation extent or invented out-of-object bytes.");
        Require(LlvmLicmObjectFacts.IsWritableObject(heap, out bool explicitOnly, metrics) && !explicitOnly,
            "An allocation's explicitly writable storage was classified as unknown.");
        Require(LlvmLicmObjectFacts.IsNotVisibleOnUnwind(heap, out bool needsCapture, metrics) && needsCapture
            && LlvmLicmObjectFacts.IsNotVisibleOnUnwindInLoop(heap, loop, tree, metrics), "Fresh heap unwind visibility bypassed or lost the source capture condition.");
        Require(LlvmLicmObjectFacts.IsThreadLocalObject(heap, loop, tree, metrics), "An uncaptured fresh allocation lost function-local classification.");
        Require(aa.GetMemoryEffects(heap).OnlyAccessesInaccessibleMem && aa.GetModRefInfo(heap).IsModSet()
            && aa.GetModRefInfo(heap, new LlvmMemoryLocation(field, 8UL)).IsNoModRef()
            && aa.GetModRefInfo(heap, load).IsNoModRef() && aa.GetModRefInfo(load, heap).IsNoModRef(),
            "Allocator-state effects became an accessible field clobber or disappeared entirely.");
        Require(heap.MayReadFromMemory() && heap.MayWriteToMemory() && heap.MayThrow()
            && !LlvmValueTracking.IsSafeToSpeculativelyExecute(heap, metrics: metrics), "Successful-result facts changed allocation effects or allowed speculation.");
        Require(metrics.GetValueOrDefault("object_size.base.heap") > 0
            && metrics.GetValueOrDefault("aa.heap.accessible_location_no_modref") > 0
            && metrics.GetValueOrDefault("licm.object.writable_heap") > 0, "Heap semantic decisions lost their telemetry.");
        return checks;
        void Require(bool result, string message) { Program.Require(result, message); ++checks; }
    }
}
