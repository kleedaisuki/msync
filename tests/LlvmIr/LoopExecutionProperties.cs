using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class LoopExecutionProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("loop-execution", context);
        var type = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "execution", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var header = new LlvmBasicBlock(context, "header", function);
        var latch = new LlvmBasicBlock(context, "latch", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        var zero = LlvmConstantInt.Get(type, 0); var one = LlvmConstantInt.Get(type, 1);
        var slot = new LlvmAllocaInstruction(type, one);
        entry.Append(slot); entry.Append(new LlvmUncondBrInstruction(header));
        var before = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], one);
        var callee = LlvmFunction.Create(LlvmFunctionType.Get(type, []), LlvmLinkage.ExternalLinkage, "unknown", module);
        var call = new LlvmCallInstruction(callee.FunctionType, callee, []);
        var after = new LlvmBinaryOperator(LlvmOpcode.Add, before, one);
        header.Append(before); header.Append(call); header.Append(after); header.Append(new LlvmUncondBrInstruction(latch));
        var next = new LlvmBinaryOperator(LlvmOpcode.Add, after, one);
        latch.Append(next); latch.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetTrue(context), header, exit));
        exit.Append(new LlvmReturnInstruction(context, zero));
        var tree = new LlvmDominatorTree(function);
        var loops = new LlvmLoopInfo(function, tree);
        var loop = loops.GetLoopFor(header)!;
        var info = new LlvmICFLoopSafetyInfo(loop);
        Program.Require(info.IsGuaranteedToExecute(before, tree), "The header prefix is executed before the call's implicit exit.");
        Program.Require(!info.IsGuaranteedToExecute(after, tree), "An unannotated call does not imply execution of the next instruction.");
        Program.Require(info.DoesNotWriteMemoryBefore(call), "The first potential writer has no prior memory write in the header.");
        Program.Require(!info.DoesNotWriteMemoryBefore(after), "A call is a preceding memory writer.");
        Program.Require(!info.IsGuaranteedToExecute(next, tree), "A predecessor implicit exit can bypass the loop latch.");
        info.RemoveInstruction(call); call.EraseFromParent();
        Program.Require(info.IsGuaranteedToExecute(after, tree), "Removing the first implicit exit invalidates the cached header prefix.");
        Program.Require(info.DoesNotWriteMemoryBefore(after), "Removing the first writer invalidates the cached memory prefix.");
        var updated = new LlvmICFLoopSafetyInfo(loop);
        Program.Require(updated.IsGuaranteedToExecute(next, tree), "Without a predecessor exit, all header paths reach the latch.");
        var store = new LlvmStoreInstruction(one, slot);
        info.InsertInstructionTo(store, header); header.InsertBefore(store, after);
        Program.Require(!info.DoesNotWriteMemoryBefore(after), "Inserting a store invalidates the previously empty writer cache.");
        Program.Require(info.IsGuaranteedToExecute(after, tree), "An ordinary store is not an implicit control-flow exit.");
        return 10;
    }
}
