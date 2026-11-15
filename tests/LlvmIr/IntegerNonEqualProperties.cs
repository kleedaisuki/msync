using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class IntegerNonEqualProperties
{
    internal static int Run()
    {
        int checks = 0;
        foreach (uint width in new uint[] { 8, 65, 129 })
        {
            LlvmContext context = new();
            var type = LlvmIntegerType.Get(context, width);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, LlvmType.GetInt1Ty(context)]),
                LlvmLinkage.InternalLinkage, "nonequal");
            var x = function.Arguments[0];
            var one = LlvmConstantInt.Get(type, 1);
            var two = LlvmConstantInt.Get(type, 2);
            var three = LlvmConstantInt.Get(type, 3);
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(x, new LlvmBinaryOperator(LlvmOpcode.Xor, x, one)),
                "Xor by nonzero must modify the original integer.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(new LlvmBinaryOperator(LlvmOpcode.Add, x, one),
                new LlvmBinaryOperator(LlvmOpcode.Add, x, two)), "Invertible add must compare its distinct remaining operands.");
            var odd = new LlvmBinaryOperator(LlvmOpcode.Or, x, one);
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(odd,
                new LlvmBinaryOperator(LlvmOpcode.Shl, odd, one) { HasNoUnsignedWrap = true }),
                "Nonzero no-wrap shift must differ from its nonzero input.");
            var selectA = new LlvmSelectInstruction(function.Arguments[1], one, two);
            var selectB = new LlvmSelectInstruction(function.Arguments[1], two, three);
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(selectA, selectB),
                "Same-condition selects compare corresponding arms, not cross-arm pairs.");
            var predA = new LlvmBasicBlock(context, "a", function);
            var predB = new LlvmBasicBlock(context, "b", function);
            var merge = new LlvmBasicBlock(context, "merge", function);
            predA.Append(new LlvmUncondBrInstruction(merge)); predB.Append(new LlvmUncondBrInstruction(merge));
            var phiA = new LlvmPhiInstruction(type); var phiB = new LlvmPhiInstruction(type);
            phiA.AddIncoming(one, predA); phiA.AddIncoming(two, predB);
            phiB.AddIncoming(two, predA); phiB.AddIncoming(three, predB);
            merge.Append(phiA); merge.Append(phiB); merge.Append(new LlvmReturnInstruction(context, phiA));
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(phiA, phiB),
                "Distinct constant pairs on each PHI predecessor must be handled without recursive pair work.");
            Program.Require(!LlvmKnownBitsAnalysis.IsKnownNonEqual(x, x), "A value must not be known unequal to itself.");
            checks += 6;
        }
        return checks;
    }
}
