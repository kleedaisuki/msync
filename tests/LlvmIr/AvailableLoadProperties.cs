using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class AvailableLoadProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        var i8 = LlvmType.GetInt8Ty(context);
        var i32 = LlvmType.GetInt32Ty(context);
        var i64 = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var module = new LlvmModule("available-load", context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i32, [pointer, pointer]),
            LlvmLinkage.InternalLinkage, "load", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var first = new LlvmStoreInstruction(LlvmConstantInt.Get(i32, 42), function.Arguments[0], new(4));
        block.Append(first);
        var load = new LlvmLoadInstruction(i32, function.Arguments[0], new(4));
        block.Append(load);
        block.Append(new LlvmReturnInstruction(context, load));
        var alias = new LlvmBatchAAResults(new LlvmAAResults(function));
        Require(ReferenceEquals(LlvmLoads.FindAvailableLoadedValue(load, alias, out bool cse), first.ValueOperand) && !cse,
            "Same-address store forwards its actual SSA value.");
        Require(LlvmLoads.IsSafeToLoadUnconditionally(function.Arguments[0], i32, new(4), load),
            "A previous ordinary access makes the same load unconditional without pointer attrs.");
        first.IsVolatile = true;
        Require(!LlvmLoads.IsSafeToLoadUnconditionally(function.Arguments[0], i32, new(4), load),
            "Volatile access does not establish regular backing memory.");
        Require(ReferenceEquals(LlvmLoads.FindAvailableLoadedValue(load, alias, out _), first.ValueOperand),
            "Forwarding an already-executed volatile store does not repeat the access.");
        first.IsVolatile = false;
        var earlierLoad = new LlvmLoadInstruction(i32, function.Arguments[0], new(4));
        block.InsertBefore(earlierLoad, load);
        Require(ReferenceEquals(LlvmLoads.FindAvailableLoadedValue(load, alias, out cse), earlierLoad) && cse,
            "Previous loads are distinguished from forwarded stores.");
        load.Ordering = LlvmAtomicOrdering.Unordered;
        Require(LlvmLoads.FindAvailableLoadedValue(load, alias, out _) is null,
            "An atomic load cannot forward from non-atomic accesses.");
        earlierLoad.Ordering = LlvmAtomicOrdering.Monotonic;
        Require(ReferenceEquals(LlvmLoads.FindAvailableLoadedValue(load, alias, out _), earlierLoad),
            "An unordered load can reuse a preceding atomic access with matching access size.");
        load.Ordering = LlvmAtomicOrdering.NotAtomic;
        var clobber = new LlvmStoreInstruction(LlvmConstantInt.Get(i32, 7), function.Arguments[1], new(4));
        block.InsertBefore(clobber, load);
        Require(LlvmLoads.FindAvailableLoadedValue(load, alias, out _) is null,
            "A candidate is rejected when an intervening may-alias store modifies its location.");
        clobber.EraseFromParent();
        var memory = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.MemSet,
            [function.Arguments[0], LlvmConstantInt.Get(i8, 0xab), LlvmConstantInt.Get(i64, 4),
             LlvmConstantInt.GetFalse(context)], module);
        block.InsertBefore(memory, load);
        Require(LlvmLoads.FindAvailableLoadedValue(load, alias, out _) is LlvmConstantInt value &&
            value.Value.GetZExtValue() == 0xabababab,
            "Constant memset forwards its byte pattern, not zero or a Number conversion.");
        var local = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i64, 1), alignment: new(4));
        block.InsertBefore(local, first);
        function.SanitizeAddress = true;
        Require(!LlvmLoads.IsSafeToLoadUnconditionally(local, i32, new(4), first),
            "Source sanitizer attributes suppress mere dereferenceability-based speculative loading.");
        function.SanitizeAddress = false;
        Require(LlvmLoads.IsSafeToLoadUnconditionally(local, i32, new(4), first),
            "Without sanitizers the live alloca layout establishes dereferenceability and alignment.");
        var metrics = new Dictionary<string, ulong>();
        Require(LlvmLoads.FindAvailableLoadedValue(load, alias, out _, metrics, 0) is null &&
            metrics.GetValueOrDefault("loads.available.scan_limit") == 1,
            "Deferred BatchAA overload preserves its exact zero-budget source behavior.");
        LlvmInstruction? from = load;
        Require(LlvmLoads.FindAvailableLoadedValue(load, block, ref from, 0, alias,
            out _, out uint scanned) is LlvmConstantInt && scanned == 1 && ReferenceEquals(from, memory),
            "Iterator overload treats zero as unlimited and retains the actual candidate position.");
        return checks;

        void Require(bool condition, string message)
        {
            Program.Require(condition, message);
            ++checks;
        }
    }
}
