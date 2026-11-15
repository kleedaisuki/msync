using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SimplifyCfgConditionalProperties
{
    internal static int Run()
    {
        int checks = 0;
        foreach (bool contiguous in new[] { false, true })
        {
            var (module, function, block, whenTrue, whenFalse) = CreateBranchFunction();
            var type = (LlvmIntegerType)function.Arguments[0].Type;
            var first = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, function.Arguments[0], LlvmConstantInt.Get(type, 1));
            var second = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, function.Arguments[0], LlvmConstantInt.Get(type, contiguous ? 2UL : 3UL));
            var condition = new LlvmBinaryOperator(LlvmOpcode.Or, first, second);
            block.Append(first); block.Append(second); block.Append(condition); block.Append(new LlvmCondBrInstruction(condition, whenTrue, whenFalse));
            Program.Require(LlvmSimplifyCfg.SimplifyCFG(block, new LlvmTargetTransformInfo(module.DataLayout)),
                "A scalar equality chain must use the source range/switch rewrite.");
            Program.Require(contiguous ? block.GetTerminator() is LlvmCondBrInstruction { Condition: LlvmICmpInstruction }
                : block.GetTerminator() is LlvmSwitchInstruction { CaseCount: 2 },
                "Contiguous comparisons become a range test; noncontiguous comparisons become switch cases.");
            checks += 2;
        }
        {
            var (module, function, block, whenTrue, whenFalse) = CreateBranchFunction();
            var type = (LlvmIntegerType)function.Arguments[0].Type;
            var first = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, function.Arguments[0], LlvmConstantInt.Get(type, 1));
            var second = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, function.Arguments[0], LlvmConstantInt.Get(type, 3));
            var pair = new LlvmBinaryOperator(LlvmOpcode.Or, first, second);
            var condition = new LlvmBinaryOperator(LlvmOpcode.Or, pair, function.Arguments[1]);
            block.Append(first); block.Append(second); block.Append(pair); block.Append(condition);
            block.Append(new LlvmCondBrInstruction(condition, whenTrue, whenFalse));
            Program.Require(LlvmSimplifyCfg.SimplifyCFG(block, new LlvmTargetTransformInfo(module.DataLayout)),
                "An equality chain may retain one source ExtraCase clause.");
            Program.Require(block.Instructions.OfType<LlvmFreezeInstruction>().Any(),
                "Reordering an unproven extra clause introduces the source freeze.");
            Program.Require(function.BasicBlocks.Any(bb => bb.GetTerminator() is LlvmSwitchInstruction { CaseCount: 2 }),
                "The residual equality cases remain a switch after the early clause.");
            checks += 3;
        }
        return checks;
    }

    private static (LlvmModule Module, LlvmFunction Function, LlvmBasicBlock Entry, LlvmBasicBlock True, LlvmBasicBlock False) CreateBranchFunction()
    {
        LlvmContext context = new();
        LlvmModule module = new("cfg-conditional", context);
        var type = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, LlvmType.GetInt1Ty(context)]), LlvmLinkage.InternalLinkage, "conditional", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var whenTrue = new LlvmBasicBlock(context, "true", function);
        var whenFalse = new LlvmBasicBlock(context, "false", function);
        whenTrue.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(type, 11)));
        whenFalse.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(type, 22)));
        return (module, function, entry, whenTrue, whenFalse);
    }
}
