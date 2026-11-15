using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SpeculationProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        LlvmModule module = new("speculation", context);
        var i32 = LlvmType.GetInt32Ty(context); var i64 = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i32, [i32]), LlvmLinkage.InternalLinkage, "main", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var x = function.Arguments[0];
        var zero = LlvmConstantInt.Get(i32, 0); var one = LlvmConstantInt.Get(i32, 1);
        var minusOne = LlvmConstantInt.Get(context, LlvmAPInt.GetAllOnes(32));
        var minimum = LlvmConstantInt.Get(context, LlvmAPInt.GetSignedMinValue(32));
        var add = new LlvmBinaryOperator(LlvmOpcode.Add, x, one) { HasNoSignedWrap = true };
        block.Append(add);
        Check(add, true, "No-wrap poison flags do not prevent source speculation.");
        Check(new LlvmBinaryOperator(LlvmOpcode.Shl, x, x), true, "Source speculation does not require a defined shift amount.");
        Check(new LlvmBinaryOperator(LlvmOpcode.UDiv, x, zero), false, "Unsigned division by zero is not speculatable.");
        Check(new LlvmBinaryOperator(LlvmOpcode.UDiv, x, one), true, "Unsigned division by constant one is speculatable.");
        Check(new LlvmBinaryOperator(LlvmOpcode.UDiv, x, new LlvmBinaryOperator(LlvmOpcode.Or, x, one)), false,
            "The source divisor matcher requires a constant, not a known-nonzero replacement algorithm.");
        Check(new LlvmBinaryOperator(LlvmOpcode.SDiv, x, minusOne), false, "Unknown numerator may be signed minimum when divisor is minus one.");
        Check(new LlvmBinaryOperator(LlvmOpcode.SRem, minimum, minusOne), false, "Signed remainder has the source signed-overflow restriction too.");
        Check(new LlvmBinaryOperator(LlvmOpcode.SDiv, one, minusOne), true, "Constant numerator excludes signed-min overflow.");
        Check(new LlvmBinaryOperator(LlvmOpcode.SDiv, minimum, LlvmConstantInt.Get(i32, 2)), true, "A nonzero, non-minus-one divisor is speculatable.");
        Require(LlvmValueTracking.IsSafeToSpeculativelyExecuteWithOpcode(LlvmOpcode.Add, new LlvmBinaryOperator(LlvmOpcode.UDiv, x, zero)),
            "The virtual-opcode entry queries the replacement opcode, not the original division.");
        var alloca = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i32, 4), alignment: new LlvmAlign(4));
        block.Append(alloca);
        var load = new LlvmLoadInstruction(i32, alloca, new LlvmAlign(4));
        block.Append(load);
        Check(load, true, "An unordered load from a sufficiently sized, aligned alloca follows source local byte facts.");
        Require(!LlvmValueTracking.IsSafeToSpeculativelyExecuteWithVariableReplaced(load), "A variable-replaced load cannot use its original pointer facts.");
        var within = new LlvmGetElementPtrInstruction(i32, alloca, [LlvmConstantInt.Get(i64, 1)]);
        var outside = new LlvmGetElementPtrInstruction(i32, alloca, [LlvmConstantInt.Get(i64, 4)]);
        var misaligned = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(context), alloca, [LlvmConstantInt.Get(i64, 1)]);
        CheckLoad(within, true, "GEP load offset plus access size stays within the alloca.");
        CheckLoad(outside, false, "GEP offset at the object end does not provide bytes for a load.");
        CheckLoad(misaligned, false, "A byte displacement not divisible by load alignment fails the source local alignment query.");
        var volatileLoad = new LlvmLoadInstruction(i32, alloca, new LlvmAlign(4), isVolatile: true);
        block.Append(volatileLoad); Check(volatileLoad, false, "Volatile loads suppress source speculation.");
        var atomicLoad = new LlvmLoadInstruction(i32, alloca, new LlvmAlign(4), ordering: LlvmAtomicOrdering.Monotonic);
        block.Append(atomicLoad); Check(atomicLoad, false, "Ordered atomic loads suppress source speculation.");
        var call = new LlvmCallInstruction(function.FunctionType, function, [one]);
        Check(call, false, "Represented calls have no Function::Speculatable attribute.");
        var heap = new LlvmHeapAllocInst(LlvmConstantInt.Get(i64, 16), new LlvmAlign(8));
        Check(heap, false, "The explicit heap-allocation extension is not a pure speculatable LLVM opcode.");
        var inert = new LlvmFileInInst(LlvmType.GetPointerTy(context), alloca, LlvmFileInFormat.Lines);
        var read = new LlvmFileInInst(LlvmType.GetPointerTy(context), alloca, LlvmFileInFormat.Text);
        Check(inert, false, "An inert Lines sequence is still a fresh ordinary Range object; unused removability does not authorize hoisting its identity.");
        Check(read, false, "Text input performs a file read and is not speculatable.");
        var ret = new LlvmReturnInstruction(context, one); block.Append(ret);
        Require(LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(add) &&
            !LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(ret) &&
            !LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(call), "Transfer queries use source successor/maythrow/willreturn behavior.");
        Require(!LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(add, alloca, 1) &&
            LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(add, alloca, 2), "The source range transfer scan uses its predecrement cutoff.");
        Require(!LlvmValueTracking.MayHaveNonDefUseDependency(add) && LlvmValueTracking.MayHaveNonDefUseDependency(load),
            "The shared non-def-use query distinguishes pure arithmetic from a memory read.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); checks++; }
        void Check(LlvmInstruction instruction, bool expected, string message) => Require(
            LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction) == expected, message);
        void CheckLoad(LlvmGetElementPtrInstruction pointer, bool expected, string message)
        {
            block.Append(pointer);
            var candidate = new LlvmLoadInstruction(i32, pointer, new LlvmAlign(4));
            block.Append(candidate); Check(candidate, expected, message);
        }
    }
}
