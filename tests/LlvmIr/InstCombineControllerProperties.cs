using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineControllerProperties
{
    internal static int Run()
    {
        WorklistOrder();
        CanonicalVisitAndAbi();
        DefUsePropagation();
        return 7;
    }
    private static void WorklistOrder()
    {
        var (module, function, block) = CreateFunction("worklist");
        var one = LlvmConstantInt.Get((LlvmIntegerType)function.ReturnType, 1);
        var first = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], one);
        var second = new LlvmBinaryOperator(LlvmOpcode.Sub, function.Arguments[0], one);
        block.Append(first); block.Append(second); block.Append(new LlvmReturnInstruction(module.Context, second));
        var worklist = new LlvmInstructionWorklist();
        worklist.Add(first); worklist.Add(second); worklist.Add(first);
        while (worklist.PopDeferred() is { } instruction) worklist.Push(instruction);
        Program.Require(worklist.RemoveOne() == first && worklist.RemoveOne() == second && worklist.IsEmpty,
            "Deferred uniqueness and reverse transfer must preserve source insertion-order visitation.");
        worklist.Push(first); worklist.Push(second); worklist.Remove(first);
        Program.Require(worklist.RemoveOne() == second && worklist.RemoveOne() is null && worklist.IsEmpty,
            "Removing a queued instruction must preserve other stack positions and skip the source tombstone.");
        worklist.Zap();
    }
    private static void CanonicalVisitAndAbi()
    {
        var (_, function, block) = CreateFunction("double-add");
        var add = new LlvmBinaryOperator(LlvmOpcode.Add, function.Arguments[0], function.Arguments[0]) { LogicalType = 17 };
        block.Append(add);
        var returned = new LlvmReturnInstruction(function.Context, add);
        returned.GetOperandUse(0).LogicalType = 17;
        block.Append(returned);
        Program.Require(Combine(function), "The actual add visitor must canonicalize a variable x+x, not just fold constants.");
        Program.Require(returned.GetOperand(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt { IsOne: true } } shift &&
            shift.Left == function.Arguments[0], "The source add canonicalization must produce the real shift instruction.");
        Program.Require(returned.GetOperand(0)!.LogicalType == 17 && returned.GetOperandUse(0).LogicalType == 17 && add.Parent is null,
            "A detached replacement preserves the producer's unset logical ABI view and existing use annotation while erasing the original.");
    }
    private static void DefUsePropagation()
    {
        var (_, function, block) = CreateFunction("propagation");
        var integer = (LlvmIntegerType)function.ReturnType;
        var multiply = new LlvmBinaryOperator(LlvmOpcode.Mul, function.Arguments[0], LlvmConstantInt.Get(integer, 0));
        var add = new LlvmBinaryOperator(LlvmOpcode.Add, multiply, LlvmConstantInt.Get(integer, 1));
        block.Append(multiply); block.Append(add);
        var returned = new LlvmReturnInstruction(function.Context, add);
        block.Append(returned);
        Program.Require(Combine(function), "The source worklist must propagate a visitor fold through its actual users.");
        Program.Require(returned.GetOperand(0) is LlvmConstantInt { IsOne: true } &&
            !block.Instructions.OfType<LlvmBinaryOperator>().Any(), "The source visitor/decrement/worklist cleanup must eliminate the complete arithmetic chain.");
    }
    private static bool Combine(LlvmFunction function)
    {
        var tree = new LlvmDominatorTree(function);
        using var assumptions = new LlvmAssumptionCache(function);
        return LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
            new LlvmTargetLibraryInfo(function.Parent!), new LlvmTargetTransformInfo(function.Parent!.DataLayout), tree);
    }
    private static (LlvmModule Module, LlvmFunction Function, LlvmBasicBlock Block) CreateFunction(string name)
    {
        var context = new LlvmContext();
        var module = new LlvmModule("instcombine-controller", context);
        var integer = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer]), LlvmLinkage.InternalLinkage, name, module);
        return (module, function, new LlvmBasicBlock(context, "entry", function));
    }
}
