using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class IntegerValueFactsProperties
{
    internal static int Run()
    {
        int cases = CheckAbsoluteValue() + PointerTrackingProperties.Run() + SpeculationProperties.Run() + OverflowProperties.Run() + MinMaxValueFactsProperties.Run();
        foreach (uint width in new uint[] { 2, 8, 65 })
        {
            LlvmContext context = new();
            LlvmModule module = new("integer-value-facts", context);
            var type = LlvmIntegerType.Get(context, width);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, type]), LlvmLinkage.InternalLinkage, "facts", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var yes = new LlvmBasicBlock(context, "nonzero", function);
            var no = new LlvmBasicBlock(context, "zero", function);
            var x = function.Arguments[0]; var y = function.Arguments[1];
            var zero = LlvmConstantInt.Get(type, 0); var one = LlvmConstantInt.Get(type, 1);
            var odd = new LlvmBinaryOperator(LlvmOpcode.Or, x, one);
            var oddProduct = new LlvmBinaryOperator(LlvmOpcode.Mul, odd, new LlvmBinaryOperator(LlvmOpcode.Or, y, one));
            var power = new LlvmBinaryOperator(LlvmOpcode.Shl, one, x);
            var negated = new LlvmBinaryOperator(LlvmOpcode.Sub, zero, x);
            var lowBit = new LlvmBinaryOperator(LlvmOpcode.And, x, negated);
            var exact = new LlvmBinaryOperator(LlvmOpcode.LShr, odd, y) { IsExact = true };
            var extended = new LlvmCastInstruction(LlvmOpcode.ZExt, odd, LlvmIntegerType.Get(context, width + 1));
            var comparison = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, x, zero);
            var select = new LlvmSelectInstruction(comparison, x, one);
            entry.Append(odd); entry.Append(power); entry.Append(negated); entry.Append(lowBit); entry.Append(exact); entry.Append(extended); entry.Append(comparison);
            entry.Append(new LlvmCondBrInstruction(comparison, yes, no));
            var result = new LlvmBinaryOperator(LlvmOpcode.Add, x, one);
            yes.Append(result); yes.Append(new LlvmReturnInstruction(context, result));
            no.Append(new LlvmReturnInstruction(context, one));
            var tree = new LlvmDominatorTree(function);
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(oddProduct), "Odd factors have a nonzero product without no-wrap flags.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(exact), "Exact right shift preserves nonzero input.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(extended), "Zero extension preserves nonzero input.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(select), "Select condition excludes zero in its true arm.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(x, context: result, tree: tree), "A dominating nonzero edge supplies the nonzero fact.");
            Program.Require(!LlvmKnownBitsAnalysis.IsKnownNonZero(x, context: no.GetTerminator(), tree: tree), "The opposite edge cannot use the nonzero fact.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(power), "One shifted by a defined amount is a power of two.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(lowBit, orZero: true), "X & -X is a power of two or zero.");
            Program.Require(!LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(lowBit), "X & -X may still be zero.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(one, depth: 6), "Constant query precedes the recursive depth cutoff.");
            var recurrenceFunction = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "recurrence", module);
            var initial = new LlvmBasicBlock(context, "initial", recurrenceFunction);
            var loop = new LlvmBasicBlock(context, "loop", recurrenceFunction);
            initial.Append(new LlvmUncondBrInstruction(loop));
            var phi = new LlvmPhiInstruction(type);
            var step = new LlvmBinaryOperator(LlvmOpcode.Shl, phi, recurrenceFunction.Arguments[0]) { HasNoUnsignedWrap = true };
            phi.AddIncoming(one, initial); phi.AddIncoming(step, loop);
            loop.Append(phi); loop.Append(step); loop.Append(new LlvmUncondBrInstruction(loop));
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonZero(phi), "No-wrap left-shift recurrence preserves a nonzero start.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(phi), "No-wrap left-shift recurrence preserves a power-of-two start.");
            Program.Require(!LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(phi, useInstructionInfo: false), "The IIQ-disabled power query must not consume no-wrap recurrence flags.");
            cases += 13;
        }
        return cases;
    }

    private static int CheckAbsoluteValue()
    {
        int cases = 0;
        for (uint width = 1; width <= 4; width++)
        {
            uint limit = 1U << (int)width, mask = limit - 1;
            for (uint zero = 0; zero < limit; zero++)
            for (uint one = 0; one < limit; one++)
            {
                if ((zero & one) != 0) continue;
                var facts = new LlvmKnownBits(new LlvmAPInt(width, zero), new LlvmAPInt(width, one));
                foreach (bool minPoison in new[] { false, true })
                {
                    var absolute = facts.Abs(minPoison);
                    for (uint value = 0; value < limit; value++)
                    {
                        if ((value & zero) != 0 || (value & one) != one || (minPoison && value == limit / 2)) continue;
                        int signed = value < limit / 2 ? (int)value : (int)value - (int)limit;
                        uint expected = unchecked((uint)Math.Abs(signed)) & mask;
                        Program.Require((expected & absolute.Zero.GetZExtValue()) == 0 && (expected & absolute.One.GetZExtValue()) == absolute.One.GetZExtValue(),
                            "KnownBits absolute value must describe every represented defined input.");
                        cases++;
                    }
                }
            }
        }
        return cases;
    }
}
