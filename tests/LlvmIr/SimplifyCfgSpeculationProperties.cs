using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SimplifyCfgSpeculationProperties
{
    internal static int Run()
    {
        Diamond();
        PhiCountBoundary(3, true);
        PhiCountBoundary(4, false);
        OneExpensiveInstruction(true);
        OneExpensiveInstruction(false);
        ConditionalStore();
        AssumptionConsumption();
        AssumedLoadSpeculation();
        return 8;
    }
    private static void Diamond()
    {
        LlvmContext context = new();
        LlvmModule module = new("simplifycfg-speculation", context);
        var type = LlvmIntegerType.Get(context, 32);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [LlvmType.GetInt1Ty(context), type]),
            LlvmLinkage.InternalLinkage, "diamond", module);
        LlvmBasicBlock entry = new(context, "entry", function), whenTrue = new(context, "true", function),
            whenFalse = new(context, "false", function), merge = new(context, "merge", function);
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], whenTrue, whenFalse));
        var one = LlvmConstantInt.Get(type, 1);
        var add = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[1], one);
        var sub = new LlvmBinaryOperator(LlvmOpcode.Sub, function.Arguments[1], one);
        whenTrue.Append(add); whenTrue.Append(new LlvmUncondBrInstruction(merge));
        whenFalse.Append(sub); whenFalse.Append(new LlvmUncondBrInstruction(merge));
        var phi = new LlvmPhiInstruction(type);
        phi.AddIncoming(add, whenTrue); phi.AddIncoming(sub, whenFalse);
        merge.Append(phi); var returned = new LlvmReturnInstruction(context, phi); merge.Append(returned);
        var tree = new LlvmDominatorTree(function);
        using var updater = new LlvmDomTreeUpdater(tree, null, LlvmDomTreeUpdateStrategy.Eager);
        Program.Require(LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout), updater),
            "A cheap arithmetic diamond must fold through the source two-entry PHI path.");
        var select = entry.Instructions.OfType<LlvmSelectInstruction>().Single();
        Program.Require(ReferenceEquals(add.Parent, entry) && ReferenceEquals(sub.Parent, entry) &&
            ReferenceEquals(select.TrueValue, add) && ReferenceEquals(select.FalseValue, sub) &&
            ReferenceEquals(returned.ReturnValue, select) && phi.Parent is null,
            "The PHI rewrite must preserve branch orientation, hoist actual arithmetic and rewrite the real return use.");
        Program.Require(entry.GetTerminator() is LlvmUncondBrInstruction direct && ReferenceEquals(direct.Destination, merge) &&
            tree.Dominates(entry, merge), "Flattening must update the real CFG and its eager dominator updater.");
    }
    private static void AssumptionConsumption()
    {
        LlvmContext context = new();
        LlvmModule module = new("simplifycfg-assumption", context);
        var type = LlvmIntegerType.Get(context, 32);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [LlvmType.GetInt1Ty(context), type, type]),
            LlvmLinkage.InternalLinkage, "assumed-nonzero", module);
        LlvmBasicBlock entry = new(context, "entry", function), thenBlock = new(context, "then", function),
            merge = new(context, "merge", function);
        var zero = LlvmConstantInt.Get(type, 0);
        var nonzero = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, function.Arguments[2], zero);
        entry.Append(nonzero);
        entry.Append(LlvmIntrinsic.CreateAssumption(nonzero, module));
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], thenBlock, merge));
        var divide = new LlvmBinaryOperator(LlvmOpcode.UDiv, function.Arguments[1], function.Arguments[2]);
        thenBlock.Append(divide); thenBlock.Append(new LlvmUncondBrInstruction(merge));
        var phi = new LlvmPhiInstruction(type);
        phi.AddIncoming(divide, thenBlock); phi.AddIncoming(zero, entry);
        merge.Append(phi); merge.Append(new LlvmReturnInstruction(context, phi));
        Program.Require(!LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout)),
            "A missing query assumption cache must not fabricate the divisor's nonzero fact.");
        using var assumptions = new LlvmAssumptionCache(function);
        Program.Require(!LlvmKnownBitsAnalysis.IsKnownNonZero(function.Arguments[2], context: entry.GetTerminator()) &&
            LlvmKnownBitsAnalysis.IsKnownNonZero(function.Arguments[2], context: entry.GetTerminator(), assumptions: assumptions),
            "The actual assumption cache must supply its nonzero fact to the source integer query.");
        Program.Require(!LlvmValueTracking.IsSafeToSpeculativelyExecute(divide, entry.GetTerminator(), assumptions: assumptions) &&
            !LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout), options: new() { AC = assumptions }) &&
            ReferenceEquals(divide.Parent, thenBlock),
            "The pinned source speculation switch accepts only constant integer divisors, even when AC proves a variable nonzero.");
    }
    private static void AssumedLoadSpeculation()
    {
        LlvmContext context = new();
        LlvmModule module = new("simplifycfg-assumed-load", context);
        var type = LlvmIntegerType.Get(context, 32);
        var pointerType = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [LlvmType.GetInt1Ty(context), pointerType]),
            LlvmLinkage.InternalLinkage, "assumed-readable", module);
        LlvmBasicBlock entry = new(context, "entry", function), thenBlock = new(context, "then", function),
            merge = new(context, "merge", function);
        var bytes = LlvmConstantInt.Get(LlvmIntegerType.Get(context, 64), 4);
        var truth = LlvmConstantInt.Get(LlvmType.GetInt1Ty(context), 1);
        var assume = LlvmIntrinsic.CreateAssumption(truth, module,
            [new("dereferenceable", [function.Arguments[1], bytes]), new("align", [function.Arguments[1], bytes])]);
        entry.Append(assume); entry.Append(new LlvmCondBrInstruction(function.Arguments[0], thenBlock, merge));
        var load = new LlvmLoadInstruction(type, function.Arguments[1], new LlvmAlign(4));
        thenBlock.Append(load); thenBlock.Append(new LlvmUncondBrInstruction(merge));
        var phi = new LlvmPhiInstruction(type);
        phi.AddIncoming(load, thenBlock); phi.AddIncoming(LlvmConstantInt.Get(type, 0), entry);
        merge.Append(phi); merge.Append(new LlvmReturnInstruction(context, phi));
        Program.Require(!LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout)),
            "Without AC, an arbitrary pointer argument must not acquire readable aligned storage facts.");
        using var assumptions = new LlvmAssumptionCache(function);
        Program.Require(LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout),
            options: new() { AC = assumptions }) && ReferenceEquals(load.Parent, entry),
            "The source load-speculation branch must consume real dereferenceable/alignment bundles through SimplifyCFG's cache.");
    }
    private static void ConditionalStore()
    {
        LlvmContext context = new();
        LlvmModule module = new("simplifycfg-store", context);
        var type = LlvmIntegerType.Get(context, 32);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [LlvmType.GetInt1Ty(context)]),
            LlvmLinkage.InternalLinkage, "conditional-store", module);
        LlvmBasicBlock entry = new(context, "entry", function), thenBlock = new(context, "then", function),
            end = new(context, "end", function);
        var one = LlvmConstantInt.Get(type, 1);
        var two = LlvmConstantInt.Get(type, 2);
        var pointer = new LlvmAllocaInstruction(type, one);
        entry.Append(pointer); entry.Append(new LlvmStoreInstruction(one, pointer));
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], thenBlock, end));
        var conditional = new LlvmStoreInstruction(two, pointer);
        thenBlock.Append(conditional); thenBlock.Append(new LlvmUncondBrInstruction(end));
        var load = new LlvmLoadInstruction(type, pointer);
        end.Append(load); end.Append(new LlvmReturnInstruction(context, load));
        Program.Require(LlvmSimplifyCfg.SimplifyCFG(entry, new(module.DataLayout)),
            "A conditional same-pointer store after a simple unconditional store must use the actual source speculation path.");
        Program.Require(ReferenceEquals(conditional.Parent, entry) && conditional.ValueOperand is LlvmSelectInstruction selected &&
            ReferenceEquals(selected.Condition, function.Arguments[0]) && ReferenceEquals(selected.TrueValue, two) &&
            ReferenceEquals(selected.FalseValue, one),
            "Store speculation must preserve the previous stored value on the non-taken edge rather than speculate an unconditional new value.");
    }
    private static void OneExpensiveInstruction(bool enabled)
    {
        LlvmContext context = new();
        LlvmModule module = new("simplifycfg-expensive", context);
        var type = LlvmIntegerType.Get(context, 32);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [LlvmType.GetInt1Ty(context), type]),
            LlvmLinkage.InternalLinkage, "one-expensive", module);
        LlvmBasicBlock entry = new(context, "entry", function), thenBlock = new(context, "then", function),
            merge = new(context, "merge", function);
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], thenBlock, merge));
        var divide = new LlvmBinaryOperator(LlvmOpcode.UDiv, function.Arguments[1], LlvmConstantInt.Get(type, 3));
        thenBlock.Append(divide); thenBlock.Append(new LlvmUncondBrInstruction(merge));
        var phi = new LlvmPhiInstruction(type);
        phi.AddIncoming(divide, thenBlock); phi.AddIncoming(LlvmConstantInt.Get(type, 0), entry);
        merge.Append(phi); merge.Append(new LlvmReturnInstruction(context, phi));
        var tuning = new LlvmSimplifyCfgTuning { TwoEntryPhiNodeFoldingThreshold = 1, SpeculateOneExpensiveInst = enabled };
        bool changed = LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout), tuning: tuning);
        Program.Require(changed == enabled && ReferenceEquals(divide.Parent, enabled ? entry : thenBlock),
            "The source single-expensive-instruction exception must depend on its actual tuning flag, not ignore target cost.");
    }
    private static void PhiCountBoundary(int count, bool shouldFold)
    {
        LlvmContext context = new();
        LlvmModule module = new("simplifycfg-phi-count", context);
        var type = LlvmIntegerType.Get(context, 32);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [LlvmType.GetInt1Ty(context)]),
            LlvmLinkage.InternalLinkage, "phi-count", module);
        LlvmBasicBlock entry = new(context, "entry", function), left = new(context, "left", function),
            right = new(context, "right", function), merge = new(context, "merge", function);
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
        left.Append(new LlvmUncondBrInstruction(merge)); right.Append(new LlvmUncondBrInstruction(merge));
        List<LlvmPhiInstruction> phis = [];
        for (int index = 0; index < count; ++index)
        {
            var phi = new LlvmPhiInstruction(type);
            phi.AddIncoming(LlvmConstantInt.Get(type, (ulong)(index * 2)), left);
            phi.AddIncoming(LlvmConstantInt.Get(type, (ulong)(index * 2 + 1)), right);
            merge.Append(phi); phis.Add(phi);
        }
        LlvmValue result = phis[0];
        foreach (var phi in phis.Skip(1))
        { var add = new LlvmBinaryOperator(LlvmOpcode.Add, result, phi); merge.Append(add); result = add; }
        merge.Append(new LlvmReturnInstruction(context, result));
        bool changed = LlvmSimplifyCfg.SimplifyCFG(merge, new(module.DataLayout));
        Program.Require(changed == shouldFold && phis.All(phi => (phi.Parent is null) == shouldFold),
            "The source pre-increment NumPhis > 2 check permits three PHIs and rejects the fourth.");
    }
}
