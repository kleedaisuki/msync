using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class IntegerCompareProperties
{
    internal static int Run()
    {
        int checks = 0;
        foreach (uint width in new uint[] { 4, 65, 129 })
        {
            LlvmContext context = new();
            LlvmModule module = new("integer-compare", context);
            var type = LlvmIntegerType.Get(context, width);
            var boolean = LlvmType.GetInt1Ty(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(boolean, [type, boolean]),
                LlvmLinkage.InternalLinkage, "compare", module);
            var x = function.Arguments[0]; var condition = function.Arguments[1];
            var zero = LlvmConstantInt.Get(type, 0); var one = LlvmConstantInt.Get(type, 1);
            var two = LlvmConstantInt.Get(type, 2);
            var wide = LlvmIntegerType.Get(context, width + 1);
            var extended = new LlvmCastInstruction(LlvmOpcode.ZExt, x, wide);
            var truncated = new LlvmCastInstruction(LlvmOpcode.Trunc, extended, type);
            Program.Require(LlvmInstructionSimplify.SimplifyCast(truncated.Opcode, truncated.Operand, type, module.DataLayout) == x,
                "Truncating a zero extension to its original type must return the original SSA value.");
            var high = LlvmConstantInt.Get(context, LlvmAPInt.GetSignedMinValue(width + 1));
            Program.Require(IsBool(LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpUlt, extended, high, module.DataLayout), true),
                "A zero extension lies below the first unrepresentable unsigned constant.");
            var sum = new LlvmBinaryOperator(LlvmOpcode.Add, x, one);
            Program.Require(IsBool(LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpNe, sum, x, module.DataLayout), true),
                "Adding a nonzero modular constant changes the integer.");
            var selected = new LlvmSelectInstruction(condition, one, two);
            Program.Require(IsBool(LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpUgt, selected, zero, module.DataLayout), true),
                "Both nonzero select arms must thread to the same comparison result.");
            Program.Require(LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpNe, condition,
                LlvmConstantInt.GetFalse(context), module.DataLayout) == condition, "Boolean not-equal false returns its input.");
            var entry = new LlvmBasicBlock(context, "entry", function);
            var yes = new LlvmBasicBlock(context, "yes", function);
            var no = new LlvmBasicBlock(context, "no", function);
            var compare = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUlt, x, two);
            entry.Append(compare); entry.Append(new LlvmCondBrInstruction(compare, yes, no));
            var use = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUle, x, two);
            yes.Append(use); yes.Append(new LlvmReturnInstruction(context, use));
            no.Append(new LlvmReturnInstruction(context, LlvmConstantInt.GetFalse(context)));
            Program.Require(IsBool(LlvmInstructionSimplify.SimplifyICmp(use.Predicate, use.Left, use.Right,
                module.DataLayout, use), true), "The immediate predecessor's strict bound implies its non-strict bound.");
            Program.Require(IsBool(LlvmInstructionSimplify.SimplifyICmp(compare.Predicate, x, two,
                module.DataLayout, no.GetTerminator()), false), "The false successor inverts the predecessor condition.");
            checks += 7;
        }
        return checks;
    }

    private static bool IsBool(LlvmValue? value, bool expected) => value is LlvmConstantInt c && c.IsOne == expected && c.IsZero != expected;
}
