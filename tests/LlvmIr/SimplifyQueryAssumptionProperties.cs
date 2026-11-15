using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SimplifyQueryAssumptionProperties
{
    internal static int Run()
    {
        var context = new LlvmContext();
        var module = new LlvmModule("simplify-query-assumptions", context);
        var integer = LlvmType.GetInt64Ty(context);
        var boolean = LlvmType.GetInt1Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(boolean, [integer]),
            LlvmLinkage.InternalLinkage, "assumed-comparison", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var seven = LlvmConstantInt.Get(integer, 7);
        var premise = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, function.Arguments[0], seven);
        block.Append(premise);
        block.Append(LlvmIntrinsic.CreateAssumption(premise, module));
        var comparison = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, function.Arguments[0], seven);
        block.Append(comparison);
        block.Append(new LlvmReturnInstruction(context, comparison));
        using var cache = new LlvmAssumptionCache(function);
        Program.Require(LlvmInstructionSimplify.SimplifyInstruction(comparison) is null,
            "An unprovided assumption cache is not implicitly reconstructed by simplification.");
        Program.Require(LlvmInstructionSimplify.SimplifyInstruction(comparison, assumptions: cache) is LlvmConstantInt { IsOne: true },
            "The supplied source assumption cache reaches actual integer known-bits comparison simplification.");
        Program.Require(LlvmInstructionSimplify.SimplifyICmp(comparison.Predicate, comparison.Left, comparison.Right,
            module.DataLayout, assumptions: cache) is null,
            "An explicitly absent query context must not be replaced by an arbitrary same-function instruction.");
        return 3;
    }
}
