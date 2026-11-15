using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class MinMaxValueFactsProperties
{
    internal static int Run()
    {
        int checks = CheckCarriers();
        LlvmContext context = new(); LlvmModule module = new("minmax-facts", context);
        var type = LlvmType.GetInt8Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "facts", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var x = function.Arguments[0]; var one = LlvmConstantInt.Get(type, 1);
        var negative = LlvmConstantInt.Get(context, new LlvmAPInt(8, unchecked((ulong)-7L), isSigned: true));
        LlvmCallInstruction MinMax(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right) => LlvmIntrinsic.CreateBinary(kind, left, right, module);
        var umax = MinMax(LlvmIntrinsicKind.UMax, x, one);
        var smax = MinMax(LlvmIntrinsicKind.SMax, x, one);
        var smin = MinMax(LlvmIntrinsicKind.SMin, x, negative);
        Require(LlvmKnownBitsAnalysis.IsKnownNonZero(umax) && LlvmKnownBitsAnalysis.IsKnownNonZero(smax) && LlvmKnownBitsAnalysis.IsKnownNonZero(smin),
            "The represented max-positive/min-negative intrinsic source cases infer nonzero results.");
        var power = MinMax(LlvmIntrinsicKind.UMin, one, LlvmConstantInt.Get(type, 8));
        Require(LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(power), "Min/max of two powers of two remains a power of two.");
        var clamp = MinMax(LlvmIntrinsicKind.SMin, MinMax(LlvmIntrinsicKind.SMax, x, negative), LlvmConstantInt.Get(type, 5));
        Require(LlvmKnownBitsAnalysis.ComputeNumSignBits(clamp) == 5, "The source signed intrinsic clamp bounds provide five sign bits for [-7,5].");
        var unsignedClamp = MinMax(LlvmIntrinsicKind.UMin, x, LlvmConstantInt.Get(type, 7));
        Require(LlvmKnownBitsAnalysis.Compute(unsignedClamp).CountMinLeadingZeros() == 5, "Unsigned min with seven bounds the five high bits of a dynamic operand.");
        entry.Append(umax); entry.Append(new LlvmReturnInstruction(context, umax));
        Require(LlvmValueTracking.IsSafeToSpeculativelyExecute(umax) && LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(umax),
            "Actual intrinsic Speculatable/NoUnwind/WillReturn descriptor facts enable the shared source queries.");
        foreach (bool minimum in new[] { true, false })
        {
            var loopFunction = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, minimum ? "minimum" : "maximum", module);
            var initial = new LlvmBasicBlock(context, "initial", loopFunction); var loop = new LlvmBasicBlock(context, "loop", loopFunction);
            initial.Append(new LlvmUncondBrInstruction(loop));
            var phi = new LlvmPhiInstruction(type);
            var update = MinMax(minimum ? LlvmIntrinsicKind.UMin : LlvmIntrinsicKind.UMax, phi, loopFunction.Arguments[0]);
            phi.AddIncoming(LlvmConstantInt.Get(type, minimum ? 15UL : 240UL), initial); phi.AddIncoming(update, loop);
            loop.Append(phi); loop.Append(update); loop.Append(new LlvmUncondBrInstruction(loop));
            var known = LlvmKnownBitsAnalysis.Compute(phi);
            Require(minimum ? known.CountMinLeadingZeros() == 4 : known.CountMinLeadingOnes() == 4,
                "Unsigned min/max PHI recurrences preserve the source initial leading-zero/one bound on the proper incoming edges.");
        }
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); checks++; }
    }

    private static int CheckCarriers()
    {
        int checks = 0;
        for (uint width = 1; width <= 3; width++)
        {
            uint limit = 1U << (int)width;
            var states = new List<LlvmKnownBits>();
            for (uint zero = 0; zero < limit; zero++)
            for (uint one = 0; one < limit; one++)
                if ((zero & one) == 0) states.Add(new(new LlvmAPInt(width, zero), new LlvmAPInt(width, one)));
            foreach (var left in states)
            foreach (var right in states)
            {
                var umin = LlvmKnownBits.UMin(left, right); var umax = LlvmKnownBits.UMax(left, right);
                var smin = LlvmKnownBits.SMin(left, right); var smax = LlvmKnownBits.SMax(left, right);
                for (uint a = 0; a < limit; a++)
                for (uint b = 0; b < limit; b++)
                {
                    if (!Matches(left, a) || !Matches(right, b)) continue;
                    int sa = a < limit / 2 ? (int)a : (int)a - (int)limit;
                    int sb = b < limit / 2 ? (int)b : (int)b - (int)limit;
                    Program.Require(Matches(umin, Math.Min(a, b)) && Matches(umax, Math.Max(a, b)) &&
                        Matches(smin, sa < sb ? a : b) && Matches(smax, sa > sb ? a : b),
                        $"i{width} min/max KnownBits must contain every matching concrete operand pair {a},{b}.");
                    checks++;
                }
            }
        }
        return checks;
    }
    private static bool Matches(LlvmKnownBits bits, uint value) => (value & bits.Zero.GetZExtValue()) == 0 && (value & bits.One.GetZExtValue()) == bits.One.GetZExtValue();
}
