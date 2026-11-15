using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class OptimizerComponentChecks
{
    internal static object Run()
    {
        InstructionEquivalenceChecks.Run();
        int dominatorMutations = DominatorMutationProperties.Run();
        int dynamicDominators = DynamicDominatorProperties.Run();
        int reachability = CfgReachabilityProperties.Run();
        int execution = LoopExecutionProperties.Run();
        PhiTransAddrChecks.Run();
        int earlyCse = EarlyCseChecks.Run();
        int captures = CaptureObjectSizeProperties.Run();
        int captureBefore = CaptureBeforeProperties.Run();
        int memoryWalker = MemorySsaWalkerProperties.Run();
        int memoryUpdates = MemorySsaUpdaterChecks.Run();
        int heapAllocationFacts = HeapAllocationFactsProperties.Run();
        int overflow = OverflowProperties.Run();
        int minMaxFacts = MinMaxValueFactsProperties.Run();
        int licmArithmetic = LicmArithmeticProperties.Run();
        MinMaxIntrinsicChecks.Run();
        TargetCosts();
        int instructionCosts = InstructionCostProperties.Run();
        int branchProbabilities = BranchProbabilityProperties.Run();
        return new { equivalent = true, dominatorMutations, dynamicDominators, reachability,
            execution, phiTranslation = true, earlyCse, captures, captureBefore, memoryWalker, memoryUpdates,
            heapAllocationFacts, overflow, minMaxFacts, licmArithmetic, instructionCosts, branchProbabilities };
    }

    private static void TargetCosts()
    {
        LlvmContext context = new();
        LlvmModule module = new("generic-target-cost", context);
        LlvmPointerType pointer = LlvmPointerType.Get(context);
        LlvmIntegerType i8 = LlvmType.GetInt8Ty(context), i64 = LlvmType.GetInt64Ty(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(pointer, [pointer, i64]),
            LlvmLinkage.InternalLinkage, "gep-cost", module);
        var target = new LlvmTargetTransformInfo(module.DataLayout);
        var zero = new LlvmGetElementPtrInstruction(i8, function.Arguments[0], [LlvmConstantInt.Get(i64, 0)]);
        var one = new LlvmGetElementPtrInstruction(i8, function.Arguments[0], [LlvmConstantInt.Get(i64, 1)]);
        var scaled = new LlvmGetElementPtrInstruction(i64, function.Arguments[0], [function.Arguments[1]]);
        var byteScale = new LlvmGetElementPtrInstruction(i8, function.Arguments[0], [function.Arguments[1]]);
        Program.Require(target.GetInstructionCost(zero, LlvmTargetCostKind.SizeAndLatency).IsFree &&
            !target.GetInstructionCost(one, LlvmTargetCostKind.SizeAndLatency).IsFree &&
            !target.GetInstructionCost(scaled, LlvmTargetCostKind.SizeAndLatency).IsFree &&
            target.GetInstructionCost(byteScale, LlvmTargetCostKind.SizeAndLatency).IsFree,
            "Generic LLVM addressing modes permit register/register+register, not arbitrary offsets or scales.");
    }
}
