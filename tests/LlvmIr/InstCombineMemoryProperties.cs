using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineMemoryProperties
{
    internal static int Run()
    {
        var context = new LlvmContext();
        var module = new LlvmModule("instcombine-memory", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        LlvmFunction Function(string name, LlvmType result, params LlvmType[] arguments) =>
            LlvmFunction.Create(LlvmFunctionType.Get(result, arguments), LlvmLinkage.InternalLinkage, name, module);
        void Combine(LlvmFunction function)
        {
            var tree = new LlvmDominatorTree(function);
            using var assumptions = new LlvmAssumptionCache(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
                new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
        }
        int checks = 0;
        var forwarding = Function("forward", integer, pointer, integer);
        var entry = new LlvmBasicBlock(context, "entry", forwarding);
        var store = new LlvmStoreInstruction(forwarding.Arguments[1], forwarding.Arguments[0]);
        var load = new LlvmLoadInstruction(integer, forwarding.Arguments[0]);
        var result = new LlvmReturnInstruction(context, load);
        entry.Append(store); entry.Append(load); entry.Append(result);
        Combine(forwarding);
        Program.Require(load.Parent is null && result.ReturnValue == forwarding.Arguments[1],
            "InstCombine must forward the available stored value through the real source local scan.");
        ++checks;

        var effects = Function("volatile", integer, pointer, integer);
        entry = new(context, "entry", effects);
        store = new(effects.Arguments[1], effects.Arguments[0], isVolatile: true);
        load = new(integer, effects.Arguments[0], isVolatile: true);
        entry.Append(store); entry.Append(load); entry.Append(new LlvmReturnInstruction(context, load));
        Combine(effects);
        Program.Require(store.Parent == entry && load.Parent == entry && store.IsVolatile && load.IsVolatile,
            "InstCombine must retain both volatile memory effects.");
        ++checks;

        var deadStore = Function("local-dse", integer, pointer, integer);
        entry = new(context, "entry", deadStore);
        var first = new LlvmStoreInstruction(LlvmConstantInt.Get(integer, 7), deadStore.Arguments[0]);
        store = new(deadStore.Arguments[1], deadStore.Arguments[0]);
        entry.Append(first); entry.Append(store); entry.Append(new LlvmReturnInstruction(context, deadStore.Arguments[1]));
        Combine(deadStore);
        Program.Require(first.Parent is null && store.Parent == entry,
            "InstCombine must remove the overwritten store without erasing the final visible write.");
        ++checks;

        var padded = Function("padded-undef", integer, pointer);
        entry = new(context, "entry", padded);
        var i12 = LlvmIntegerType.Get(context, 12);
        store = new(LlvmUndefValue.Get(i12), padded.Arguments[0]);
        entry.Append(store); entry.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(integer, 0)));
        Combine(padded);
        Program.Require(store.Parent == entry,
            "A non-byte-sized undef store must retain the source padding write.");
        ++checks;

        var copied = Function("constant-memory-copy", integer);
        entry = new(context, "entry", copied);
        var global = new LlvmGlobalVariable(module, integer, true, LlvmLinkage.InternalLinkage,
            LlvmConstantInt.Get(integer, 23), "source-constant");
        var local = new LlvmAllocaInstruction(integer, LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1), alignment: new(8));
        var transfer = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.MemCpy,
            [local, global, LlvmConstantInt.Get(integer, 8), LlvmConstantInt.GetFalse(context)], module);
        transfer.SetParamAlign(0, new(8)); transfer.SetParamAlign(1, new(8));
        load = new(integer, local, new(8));
        result = new(context, load);
        entry.Append(local); entry.Append(transfer); entry.Append(load); entry.Append(result);
        Combine(copied);
        Program.Require(result.ReturnValue is LlvmConstantInt { Value: var copiedValue } && copiedValue == new LlvmAPInt(64, 23),
            "An alloca copied only from constant memory must expose the constant value through the source copy-elision algorithm.");
        ++checks;

        var heap = Function("zero-heap-identity", pointer);
        entry = new(context, "entry", heap);
        var allocation = new LlvmHeapAllocInst(LlvmConstantInt.Get(integer, 0), new(8));
        entry.Append(allocation); entry.Append(new LlvmReturnInstruction(context, allocation));
        Combine(heap);
        Program.Require(allocation.Parent == entry,
            "Zero-byte HeapAlloc is not an alloca and must retain its successful-result identity.");
        ++checks;
        return checks;
    }
}
