using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SroaDriverChecks
{
    internal static int Run()
    {
        DeadAllocas();
        HeapAllocationsRemainHeapAllocations();
        return 2;
    }

    private static void DeadAllocas()
    {
        var context = new LlvmContext();
        var module = new LlvmModule("sroa-dead-worklist", context);
        var i32 = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i32), LlvmLinkage.InternalLinkage, "dead", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var builder = new LlvmTransformBuilder(entry);
        var dead = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i32, 1));
        entry.Append(dead);
        builder.CreateRet(LlvmConstantInt.Get(i32, 17));
        var result = LlvmSroa.Run(function, new(function));
        Program.Require(result.Changed && !result.CFGChanged && dead.Parent is null && entry.Instructions.Count == 1,
            "SROA must drain the dead alloca worklist without rewriting the represented CFG or return.");
    }

    private static void HeapAllocationsRemainHeapAllocations()
    {
        var context = new LlvmContext();
        var module = new LlvmModule("sroa-heap-ownership", context);
        var i64 = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetPointerTy(context)),
            LlvmLinkage.InternalLinkage, "heap", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var heap = new LlvmHeapAllocInst(LlvmConstantInt.Get(i64, 8), new(8));
        entry.Append(heap);
        entry.Append(new LlvmReturnInstruction(context, heap));
        var result = LlvmSroa.Run(function, new(function));
        Program.Require(!result.Changed && !result.CFGChanged && ReferenceEquals(heap.Parent, entry) &&
            ReferenceEquals(((LlvmReturnInstruction)entry.GetTerminator()).ReturnValue, heap),
            "SROA must not reinterpret project HeapAlloc or its escaping identity as an alloca.");
    }
}
