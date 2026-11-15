using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineFCmpChecks
{
    internal static int Run()
    {
        FractionalIntegerEquality();
        IntegerPredicateAdjustment();
        ReciprocalSign();
        SubtractionAndNegation();
        BitcastZeroDenormalGate();
        IntrinsicZeroComparisons();
        FloorAndExtension();
        TruncationMidpoint();
        FloatingConstantCasts();
        DenormalConstantFolding();
        return 10;
    }

    private static void FractionalIntegerEquality()
    {
        Fixture fixture = Create("fractional-equality", context => [LlvmType.GetInt32Ty(context)]);
        LlvmCastInstruction cast = new(LlvmOpcode.SIToFP, fixture.Arguments[0], LlvmType.GetDoubleTy(fixture.Context));
        fixture.Block.Append(cast);
        LlvmFCmpInstruction comparison = new(LlvmCmpPredicate.FCmpOeq, cast, FP(fixture, 0x3ff8000000000000));
        Finish(fixture, comparison);
        Require(ReturnValue(fixture) is LlvmConstantInt { IsZero: true }, "integer conversion cannot equal fractional constant");
    }

    private static void IntegerPredicateAdjustment()
    {
        Fixture fixture = Create("integer-predicate", context => [LlvmType.GetInt32Ty(context)]);
        LlvmValue argument = fixture.Arguments[0];
        LlvmCastInstruction cast = new(LlvmOpcode.SIToFP, argument, LlvmType.GetDoubleTy(fixture.Context));
        fixture.Block.Append(cast);
        LlvmConstantFP bound = FP(fixture, 0x401199999999999a);
        Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOlt, cast, bound));
        Require(ReturnValue(fixture) is LlvmICmpInstruction, "floating comparison is replaced by an integer predicate");
        LlvmICmpInstruction result = (LlvmICmpInstruction)ReturnValue(fixture);
        for (long value = -16; value <= 16; ++value)
        {
            LlvmConstantInt integer = LlvmConstantInt.Get((LlvmIntegerType)argument.Type, unchecked((ulong)value), isSigned: true);
            LlvmConstant left = result.Left == argument ? integer : (LlvmConstant)result.Left;
            LlvmConstant right = result.Right == argument ? integer : (LlvmConstant)result.Right;
            LlvmConstantInt folded = (LlvmConstantInt)LlvmConstantFolding.ConstantFoldCompareInstOperands(result.Predicate, left, right, fixture.Module.DataLayout, fixture.Function)!;
            bool expected = LlvmAPFloat.FromAPInt(integer.Value, true).Value.Compare(bound.Value) == LlvmFloatCompare.LessThan;
            Require(folded.IsOne == expected, "fractional bound preserves sampled integer truth values");
        }
    }

    private static void ReciprocalSign()
    {
        Fixture fixture = Create("reciprocal-sign", context => [LlvmType.GetDoubleTy(context)]);
        LlvmBinaryOperator divide = new(LlvmOpcode.FDiv, FP(fixture, 0x3ff0000000000000), fixture.Arguments[0]) { FastMathFlags = LlvmFastMathFlags.NoInfs };
        fixture.Block.Append(divide);
        Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOlt, divide, FP(fixture, 0)) { FastMathFlags = LlvmFastMathFlags.NoInfs });
        Require(ReturnValue(fixture) is LlvmFCmpInstruction result && result.Left == fixture.Arguments[0], "ninf reciprocal comparison becomes denominator sign test");
    }

    private static void SubtractionAndNegation()
    {
        Fixture fixture = Create("subtract-zero", context => [LlvmType.GetDoubleTy(context), LlvmType.GetDoubleTy(context)]);
        LlvmBinaryOperator subtract = new(LlvmOpcode.FSub, fixture.Arguments[0], fixture.Arguments[1])
        { FastMathFlags = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs };
        fixture.Block.Append(subtract);
        Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOeq, subtract, FP(fixture, 0)));
        Require(ReturnValue(fixture) is LlvmFCmpInstruction result &&
            ((result.Left == fixture.Arguments[0] && result.Right == fixture.Arguments[1]) || (result.Left == fixture.Arguments[1] && result.Right == fixture.Arguments[0])),
            "IEEE subtraction-zero comparison uses original operands");
        Fixture negative = Create("negated-pair", context => [LlvmType.GetDoubleTy(context), LlvmType.GetDoubleTy(context)]);
        LlvmUnaryOperator a = new(negative.Arguments[0]), b = new(negative.Arguments[1]);
        negative.Block.Append(a); negative.Block.Append(b);
        Finish(negative, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOlt, a, b));
        Require(ReturnValue(negative) is LlvmFCmpInstruction negated && negated.Predicate == LlvmCmpPredicate.FCmpOgt &&
            negated.Left == negative.Arguments[0] && negated.Right == negative.Arguments[1], "negated pair swaps the floating predicate");
    }

    private static void BitcastZeroDenormalGate()
    {
        foreach (bool flushing in new[] { false, true })
        {
            Fixture fixture = Create("bitcast-zero", context => [LlvmType.GetInt64Ty(context)]);
            if (flushing) fixture.Function.DenormalFPEnvironment = new(LlvmDenormalModeInfo.PreserveSign, LlvmDenormalModeInfo.PreserveSign);
            LlvmCastInstruction bits = new(LlvmOpcode.BitCast, fixture.Arguments[0], LlvmType.GetDoubleTy(fixture.Context));
            fixture.Block.Append(bits);
            Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOeq, bits, FP(fixture, 0)));
            Require(flushing ? ReturnValue(fixture) is LlvmFCmpInstruction : ReturnValue(fixture) is LlvmICmpInstruction,
                "integer magnitude-zero rewrite follows actual function input denormal mode");
        }
    }

    private static void IntrinsicZeroComparisons()
    {
        Fixture fixture = Create("fabs-zero", context => [LlvmType.GetDoubleTy(context)]);
        LlvmCallInstruction absolute = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.FAbs, [fixture.Arguments[0]], fixture.Module);
        fixture.Block.Append(absolute);
        Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOle, absolute, FP(fixture, 0)));
        Require(ReturnValue(fixture) is LlvmFCmpInstruction result && result.Left == fixture.Arguments[0] && result.Predicate == LlvmCmpPredicate.FCmpOeq,
            "fabs less-or-equal zero becomes equality");
        Fixture sqrt = Create("sqrt-zero", context => [LlvmType.GetDoubleTy(context)]);
        LlvmCallInstruction squareRoot = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.Sqrt, [sqrt.Arguments[0]], sqrt.Module);
        sqrt.Block.Append(squareRoot);
        Finish(sqrt, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpUeq, squareRoot, FP(sqrt, 0)) { FastMathFlags = LlvmFastMathFlags.NoInfs });
        Require(ReturnValue(sqrt) is LlvmFCmpInstruction square && square.Left == sqrt.Arguments[0] && square.Predicate == LlvmCmpPredicate.FCmpUle &&
            (square.FastMathFlags & LlvmFastMathFlags.NoInfs) == 0, "sqrt predicate rewrite clears unsupported ninf");
    }

    private static void FloorAndExtension()
    {
        Fixture fixture = Create("floor-self", context => [LlvmType.GetDoubleTy(context)]);
        LlvmCallInstruction floor = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.Floor, [fixture.Arguments[0]], fixture.Module);
        fixture.Block.Append(floor);
        Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOeq, floor, fixture.Arguments[0]));
        Require(fixture.Block.Instructions.OfType<LlvmCallInstruction>().Any(call => call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Trunc),
            "commutative floor-self comparison canonicalizes to trunc intrinsic");
        Fixture extension = Create("extension-impossible", context => [LlvmType.GetFloatTy(context)]);
        LlvmCastInstruction extended = new(LlvmOpcode.FPExt, extension.Arguments[0], LlvmType.GetDoubleTy(extension.Context));
        extension.Block.Append(extended);
        Finish(extension, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOeq, extended, FP(extension, 0x3fb999999999999a)));
        Require(ReturnValue(extension) is LlvmConstantInt { IsZero: true }, "fpext input cannot equal a lossy higher-precision constant");
    }

    private static void TruncationMidpoint()
    {
        Fixture fixture = Create("trunc-midpoint", context => [LlvmType.GetDoubleTy(context)]);
        LlvmType single = LlvmType.GetFloatTy(fixture.Context);
        LlvmCastInstruction truncation = new(LlvmOpcode.FPTrunc, fixture.Arguments[0], single);
        fixture.Block.Append(truncation);
        LlvmConstantFP one = LlvmConstantFP.Get(single, LlvmAPFloat.GetOne(LlvmAPFloatSemantics.IEEEsingle));
        Finish(fixture, new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOlt, truncation, one));
        Require(ReturnValue(fixture) is LlvmFCmpInstruction result && result.Left == fixture.Arguments[0] &&
            result.Right is LlvmConstantFP midpoint && midpoint.Value.BitcastToAPInt().GetZExtValue() == 0x3feffffff0000000,
            "fptrunc comparison uses exact nearest-even midpoint in the wider format");
    }

    private static void FloatingConstantCasts()
    {
        LlvmContext context = new(); LlvmDataLayout layout = new();
        LlvmType half = LlvmType.GetHalfTy(context), bfloat = LlvmType.GetBFloatTy(context);
        LlvmConstantFP one = LlvmConstantFP.Get(half, LlvmAPFloat.GetOne(LlvmAPFloatSemantics.IEEEhalf));
        LlvmConstantFP raw = (LlvmConstantFP)LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.BitCast, one, bfloat, layout)!;
        Require(raw.Value.Semantics == LlvmAPFloatSemantics.BFloat && raw.Value.BitcastToAPInt().GetZExtValue() == 0x3c00, "half-to-bfloat bitcast preserves bits, not numeric value");
        LlvmConstantInt integer = LlvmConstantInt.Get(LlvmType.GetInt16Ty(context), 2049);
        LlvmConstantFP rounded = (LlvmConstantFP)LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.UIToFP, integer, half, layout)!;
        Require(rounded.Value.BitcastToAPInt().GetZExtValue() == 0x6800, "integer-to-half cast rounds a tie to even");
    }

    private static void DenormalConstantFolding()
    {
        Fixture fixture = Create("denormal-folding", context => [LlvmType.GetDoubleTy(context)]);
        LlvmDenormalModeInfo preserve = LlvmDenormalModeInfo.PreserveSign;
        fixture.Function.DenormalFPEnvironment = new(preserve, LlvmDenormalModeInfo.PositiveZero);
        LlvmType single = LlvmType.GetFloatTy(fixture.Context);
        LlvmConstantFP one = LlvmConstantFP.Get(single, LlvmAPFloat.GetOne(LlvmAPFloatSemantics.IEEEsingle));
        Require(LlvmConstantFolding.ConstantFoldCompareInstOperands(LlvmCmpPredicate.FCmpOeq, one, one, fixture.Module.DataLayout) is LlvmConstantInt { IsOne: true },
            "normal single constants are not misclassified by a binary64 exponent mask");
        LlvmConstantFP negativeTiny = LlvmConstantFP.Get(single, new LlvmAPFloat(LlvmAPFloatSemantics.IEEEsingle, new LlvmAPInt(32, 0x80000001)));
        LlvmConstantFP zero = LlvmConstantFP.Get(single, LlvmAPFloat.GetZero(LlvmAPFloatSemantics.IEEEsingle));
        Require(LlvmConstantFolding.FlushFPConstant(negativeTiny, null, false) is null, "unknown environment keeps dynamic subnormal folding unresolved");
        Require(LlvmConstantFolding.FlushFPConstant(negativeTiny, fixture.Function, false) is LlvmConstantFP { IsZero: true, IsNegative: false },
            "function f32 override flushes negative subnormal to positive zero");
        Require(LlvmConstantFolding.ConstantFoldCompareInstOperands(LlvmCmpPredicate.FCmpOeq, negativeTiny, zero, fixture.Module.DataLayout, fixture.Function) is LlvmConstantInt { IsOne: true },
            "constant compare reads actual input denormal mode");
        LlvmConstantFP doubleTiny = FP(fixture, 0x8000000000000001);
        Require(LlvmConstantFolding.FlushFPConstant(doubleTiny, fixture.Function, false) is LlvmConstantFP { IsZero: true, IsNegative: true },
            "non-f32 formats retain the source default preserve-sign mode");
        fixture.Block.Append(new LlvmReturnInstruction(fixture.Context, LlvmConstantInt.GetFalse(fixture.Context)));
    }

    private sealed record Fixture(LlvmContext Context, LlvmModule Module, LlvmFunction Function, LlvmBasicBlock Block)
    { internal IReadOnlyList<LlvmArgument> Arguments => Function.Arguments; }
    private static Fixture Create(string name, Func<LlvmContext, LlvmType[]> parameters)
    {
        LlvmContext context = new(); LlvmModule module = new(name, context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetInt1Ty(context), parameters(context)), LlvmLinkage.InternalLinkage, name, module);
        return new(context, module, function, new LlvmBasicBlock(context, "entry", function));
    }
    private static LlvmConstantFP FP(Fixture fixture, ulong bits) => LlvmConstantFP.Get(fixture.Context, LlvmAPFloat.FromBits(bits));
    private static void Finish(Fixture fixture, LlvmInstruction result)
    {
        fixture.Block.Append(result); fixture.Block.Append(new LlvmReturnInstruction(fixture.Context, result));
        using LlvmAssumptionCache assumptions = new(fixture.Function);
        var tree = new LlvmDominatorTree(fixture.Function);
        LlvmInstCombine.Run(fixture.Function, new LlvmAAResults(fixture.Function, tree: tree), assumptions, new(fixture.Module), new(fixture.Module.DataLayout), tree);
    }
    private static LlvmValue ReturnValue(Fixture fixture) => fixture.Function.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmReturnInstruction>().Single().ReturnValue!;
    private static void Require(bool condition, string cause)
    { if (!condition) throw new InvalidOperationException($"InstCombine FCmp: {cause}"); }
}
