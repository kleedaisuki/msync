using Msync.Core.Llvm;
using Msync.Optimization.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

internal static class KnownFPClassAssumptionProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("known-fp-assumptions", context);
        LlvmType type = LlvmType.GetDoubleTy(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "positive", module);
        LlvmBasicBlock entry = new(context, "entry", function);
        LlvmValue x = function.Arguments[0];
        var zero = LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0));
        var condition = new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOgt, x, zero);
        var assume = LlvmIntrinsic.CreateAssumption(condition, module, [new("noundef", [x])]);
        var negated = new LlvmUnaryOperator(x);
        var quotient = new LlvmBinaryOperator(LlvmOpcode.FDiv, x, x);
        entry.Append(condition); entry.Append(assume); entry.Append(negated); entry.Append(quotient);
        entry.Append(new LlvmReturnInstruction(context, quotient));
        var tree = new LlvmDominatorTree(function);
        using var assumptions = new LlvmAssumptionCache(function);
        Program.Require(LlvmKnownFPClasses.Compute(x, contextInstruction: negated, tree: tree).IsUnknown,
            "A query without its source assumption cache must not fabricate the positive-class fact.");
        Program.Require(LlvmKnownFPClasses.Compute(x, contextInstruction: condition, tree: tree, assumptions: assumptions).IsUnknown,
            "An assume must not prove its own ephemeral compare true under the default source query.");
        Program.Require(!LlvmKnownFPClasses.Compute(x, contextInstruction: condition, tree: tree, assumptions: assumptions,
            allowEphemerals: true).IsUnknown, "The source explicit ephemeral permission must reach assumption-context validation.");
        var positive = LlvmKnownFPClasses.Compute(x, contextInstruction: negated, tree: tree, assumptions: assumptions);
        Program.Require(positive.GetKnownFPClasses() == (C.PosSubnormal | C.PosNormal | C.PosInfinity) && positive.SignBit == false,
            "The real valid fcmp assumption must restrict its operand to ordered-positive nonzero classes.");
        var negative = LlvmKnownFPClasses.Compute(negated, contextInstruction: negated, tree: tree, assumptions: assumptions);
        Program.Require(negative.GetKnownFPClasses() == (C.NegSubnormal | C.NegNormal | C.NegInfinity) &&
            LlvmKnownFPClasses.CannotBeNegativeZero(negated, negated, tree, assumptions: assumptions),
            "Recursive fneg queries and the public negative-zero helper must retain the same cache context.");
        Program.Require(LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(x, tree, negated, assumptions: assumptions),
            "The source final noundef bundle query must run for floating values, not stop at the integer-only dominator walk.");
        var divided = LlvmKnownFPClasses.Compute(quotient, contextInstruction: quotient, tree: tree, assumptions: assumptions);
        Program.Require(divided.IsKnownNever(C.All & ~(C.NaN | C.PosNormal)),
            "A retained source noundef fact must reach the self-division helper without substituting the PoisonOnly query.");
        return 7;
    }
}
