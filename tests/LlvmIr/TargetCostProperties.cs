using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class TargetCostProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        LlvmModule module = new("generic-target-scalar-costs", context);
        LlvmDataLayout native = new("n8:16:32:64");
        var metrics = new Dictionary<string, ulong>();
        LlvmTargetTransformInfo target = new(native, metrics);
        LlvmIntegerType i1 = LlvmType.GetInt1Ty(context), i32 = LlvmType.GetInt32Ty(context), i64 = LlvmType.GetInt64Ty(context);
        LlvmType number = LlvmType.GetDoubleTy(context);
        LlvmPointerType pointer = LlvmPointerType.Get(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(number, [number, number, i1]),
            LlvmLinkage.InternalLinkage, "costs", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var other = new LlvmBasicBlock(context, "other", function);
        LlvmValue left = function.Arguments[0], right = function.Arguments[1], condition = function.Arguments[2];
        foreach (LlvmTargetCostKind kind in Enum.GetValues<LlvmTargetCostKind>())
        {
            foreach (LlvmOpcode opcode in new[] { LlvmOpcode.FAdd, LlvmOpcode.FSub, LlvmOpcode.FMul })
                Require(target.GetInstructionCost(new LlvmBinaryOperator(opcode, left, right), kind) ==
                    new LlvmInstructionCost(kind == LlvmTargetCostKind.Latency ? 3 : 1), "FP arithmetic uses the source cost-kind distinction.");
            foreach (LlvmOpcode opcode in new[] { LlvmOpcode.FDiv, LlvmOpcode.FRem })
                Require(target.GetInstructionCost(new LlvmBinaryOperator(opcode, left, right), kind) == LlvmInstructionCost.Expensive,
                    "Division and remainder retain the source expensive cost for every cost kind.");
            Require(target.GetCFInstrCost(LlvmOpcode.PHI, kind) ==
                (kind == LlvmTargetCostKind.ReciprocalThroughput ? LlvmInstructionCost.Basic : LlvmInstructionCost.Free),
                "Only PHI throughput costs a register in the generic model.");
            Require(target.GetInsertExtractValueCost(LlvmOpcode.InsertValue, kind) ==
                (kind == LlvmTargetCostKind.ReciprocalThroughput ? LlvmInstructionCost.Free : LlvmInstructionCost.Basic),
                "Aggregate insertion preserves the source cost-kind split.");
            Require(target.GetInsertExtractValueCost(LlvmOpcode.ExtractValue, kind).IsFree,
                "Aggregate extraction is free in the generic source model.");
        }
        Require(target.GetInstructionCost(new LlvmSelectInstruction(condition, left, right), LlvmTargetCostKind.SizeAndLatency) == LlvmInstructionCost.Basic,
            "Ordinary select dispatches to comparison/select cost.");
        Require(target.GetInstructionCost(new LlvmSelectInstruction(condition, condition, LlvmConstantInt.GetFalse(context)),
            LlvmTargetCostKind.SizeAndLatency) == LlvmInstructionCost.Basic && metrics.GetValueOrDefault("tti.arithmetic.queries") == 21,
            "Logical select dispatches to arithmetic cost, rather than an independent invented price.");
        var constantSize = LlvmConstantInt.Get(i32, 1);
        var fixedAlloca = new LlvmAllocaInstruction(number, constantSize);
        var nonEntryAlloca = new LlvmAllocaInstruction(number, constantSize);
        entry.Append(fixedAlloca); other.Append(nonEntryAlloca);
        Require(target.GetInstructionCost(fixedAlloca, LlvmTargetCostKind.CodeSize).IsFree
            && !target.GetInstructionCost(nonEntryAlloca, LlvmTargetCostKind.CodeSize).IsFree,
            "Only static entry-block allocas have zero source cost.");
        Require(target.GetCastInstrCost(LlvmOpcode.Trunc, i32, i64, LlvmTargetCostKind.CodeSize).IsFree,
            "Truncation to a legal native integer has the source free cost.");
        Require(!new LlvmTargetTransformInfo(new()).GetCastInstrCost(LlvmOpcode.Trunc, i32, i64, LlvmTargetCostKind.CodeSize).IsFree,
            "An empty DataLayout has no guessed host legal integer widths.");
        Require(target.GetCastInstrCost(LlvmOpcode.IntToPtr, pointer, i32, LlvmTargetCostKind.CodeSize).IsFree
            && target.GetCastInstrCost(LlvmOpcode.PtrToInt, i64, pointer, LlvmTargetCostKind.CodeSize).IsFree
            && !target.GetCastInstrCost(LlvmOpcode.PtrToInt, i32, pointer, LlvmTargetCostKind.CodeSize).IsFree,
            "Pointer casts follow source native-width and pointer-size comparisons.");
        Require(native.IsLegalInteger(32) && !native.IsLegalInteger(31) && native.FitsInLegalInteger(31)
            && !native.FitsInLegalInteger(65) && !new LlvmDataLayout().FitsInLegalInteger(1),
            "Exact width legality and fitting into a legal width are distinct source queries.");
        Require(native.Equals(new LlvmDataLayout("n8:16:32:64")) && !native.Equals(new LlvmDataLayout("n32:64")),
            "DataLayout equality includes native integer widths.");
        LlvmFunction external = LlvmFunction.Create(LlvmFunctionType.Get(number, [number, number]),
            LlvmLinkage.ExternalLinkage, "ordinary", module);
        Require(target.GetInstructionCost(new LlvmCallInstruction(external.FunctionType, external, [left, right]),
            LlvmTargetCostKind.SizeAndLatency) == new LlvmInstructionCost(3), "Ordinary direct call cost counts formal parameters plus one.");
        external.SetName("sin");
        Require(!target.IsLoweredToCall(external), "The generic source symbol heuristic is preserved as a cost query.");
        external.Linkage = LlvmLinkage.InternalLinkage;
        Require(target.IsLoweredToCall(external), "The source local-linkage rule precedes external-symbol cost heuristics.");
        Require(!target.HasBranchDivergence(function) && !target.HasConditionalLoadStoreForType(number, false)
            && target.ShouldBuildLookupTables() && !target.IsTypeLegal(i32)
            && target.GetMinimumLookupTableEntryBitWidth() == 8,
            "Generic target capabilities retain actual upstream defaults.");
        Require(target.GetPredictableBranchThreshold() == new LlvmBranchProbability(99, 100)
            && target.GetBranchMispredictPenalty().IsFree, "Branch queries use the source threshold and default penalty.");
        return checks;

        void Require(bool value, string message) { Program.Require(value, message); ++checks; }
    }
}
