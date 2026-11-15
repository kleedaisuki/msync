using System.Numerics;
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class ScalarIntrinsicChecks
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("scalar-intrinsics", context);
        int checks = 0;
        foreach (uint width in new uint[] { 1, 2, 3, 4 })
        {
            var type = LlvmIntegerType.Get(context, width);
            int cardinality = 1 << (int)width, mask = cardinality - 1;
            for (int value = 0; value < cardinality; ++value)
            {
                var bits = LlvmConstantInt.Get(type, (ulong)value);
                CheckBits(Fold(LlvmIntrinsicKind.CTPop, type, [bits]), (ulong)BitOperations.PopCount((uint)value)); ++checks;
                foreach (bool poisonZero in new[] { false, true })
                {
                    var folded = Fold(LlvmIntrinsicKind.CTTZ, type, [bits, LlvmConstantInt.GetBool(context, poisonZero)]);
                    if (poisonZero && value == 0) Require(folded is LlvmPoisonValue, "cttz zero-poison");
                    else CheckBits(folded, (ulong)(value == 0 ? width : (uint)BitOperations.TrailingZeroCount((uint)value)));
                    ++checks;
                }
                for (int second = 0; second < cardinality; ++second)
                {
                    var right = LlvmConstantInt.Get(type, (ulong)second);
                    var comparisonType = LlvmIntegerType.Get(context, 3);
                    int signedLeft = value >= cardinality / 2 ? value - cardinality : value;
                    int signedRight = second >= cardinality / 2 ? second - cardinality : second;
                    CheckBits(Fold(LlvmIntrinsicKind.SCmp, comparisonType, [bits, right]), (ulong)(Math.Sign(signedLeft - signedRight) & 7));
                    CheckBits(Fold(LlvmIntrinsicKind.UCmp, comparisonType, [bits, right]), (ulong)(Math.Sign(value - second) & 7)); checks += 2;
                    for (int shift = 0; shift < cardinality; ++shift)
                    {
                        int reduced = shift % (int)width;
                        int expected = reduced == 0 ? value : ((value << reduced) | (second >> ((int)width - reduced))) & mask;
                        CheckBits(Fold(LlvmIntrinsicKind.FShl, type, [bits, right, LlvmConstantInt.Get(type, (ulong)shift)]), (ulong)expected); ++checks;
                    }
                }
            }
            foreach (var range in Ranges(width))
            {
                var pop = range.CTPop();
                foreach (bool poisonZero in new[] { false, true })
                {
                    var trailing = range.CTTZ(poisonZero);
                    for (int value = 0; value < cardinality; ++value)
                    {
                        if (!range.Contains(new LlvmAPInt(width, (ulong)value))) continue;
                        Require(pop.Contains(new LlvmAPInt(width, (ulong)BitOperations.PopCount((uint)value))), "ctpop interval transfer");
                        if (poisonZero && value == 0) continue;
                        uint count = value == 0 ? width : (uint)BitOperations.TrailingZeroCount((uint)value);
                        Require(trailing.Contains(new LlvmAPInt(width, count)), "cttz interval transfer"); ++checks;
                    }
                }
            }
        }
        checks += Descriptors(module);
        checks += UndefAndSimplification(module);
        checks += CreationFacts(module);
        foreach (uint width in new uint[] { 65, 127, 257 })
        {
            var type = LlvmIntegerType.Get(context, width);
            var ones = LlvmConstantInt.Get(context, LlvmAPInt.GetAllOnes(width));
            var highBit = LlvmConstantInt.Get(context, LlvmAPInt.GetOneBitSet(width, width - 1));
            var one = LlvmConstantInt.Get(type, 1);
            CheckBits(Fold(LlvmIntrinsicKind.CTPop, type, [ones]), width);
            CheckBits(Fold(LlvmIntrinsicKind.CTTZ, type, [highBit, LlvmConstantInt.GetTrue(context)]), width - 1);
            CheckBits(Fold(LlvmIntrinsicKind.FShl, type, [one, ones, one]), 3);
            CheckBits(Fold(LlvmIntrinsicKind.SCmp, LlvmType.GetInt32Ty(context), [highBit, one]), uint.MaxValue);
            checks += 4;
        }
        return checks;
    }

    private static int CreationFacts(LlvmModule module)
    {
        var i8 = LlvmType.GetInt8Ty(module.Context);
        var input = LlvmConstantInt.Get(i8, 1);
        var cttz = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTTZ,
            [input, LlvmConstantInt.GetTrue(module.Context)], module);
        Require(LlvmValueTracking.CanCreatePoison(cttz) && !LlvmValueTracking.CanCreatePoison(cttz, false), "cttz poison annotation depends on flags query");
        Require(LlvmValueTracking.CanCreateUndefOrPoison(cttz) && !LlvmValueTracking.CanCreateUndefOrPoison(cttz, false), "combined query preserves source flags semantics");
        cttz.DropPoisonGeneratingFlags();
        Require(!LlvmValueTracking.CanCreateUndefOrPoison(cttz), "cttz flag removal exposes source defined result");
        var external = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8]), LlvmLinkage.ExternalLinkage, "ordinary-undefined-result", module);
        var call = new LlvmCallInstruction(external.FunctionType, external, [input]);
        Require(LlvmValueTracking.CanCreateUndefOrPoison(call, false), "ordinary call has no invented definedness");
        call.ReturnAttributes = new() { Flags = LlvmAttributeKind.NoUndef };
        Require(!LlvmValueTracking.CanCreateUndefOrPoison(call), "actual return noundef attribute is consumed");
        call.ReturnAttributes = default;
        external.NoCreateUndefOrPoison = true;
        Require(!LlvmValueTracking.CanCreateUndefOrPoison(call, false), "actual callee nocreate attribute is consumed");
        return 6;
    }

    private static int Descriptors(LlvmModule module)
    {
        var context = module.Context;
        var i8 = LlvmIntegerType.Get(context, 8);
        var i32 = LlvmIntegerType.Get(context, 32);
        foreach (var kind in new[] { LlvmIntrinsicKind.FShl, LlvmIntrinsicKind.CTPop, LlvmIntrinsicKind.CTTZ })
        {
            var declaration = LlvmIntrinsic.GetDeclaration(module, kind, i8);
            Require(declaration.Name == kind.Name + ".i8" && declaration == LlvmIntrinsic.GetDeclaration(module, kind, i8), "integer overloaded declaration uniquing");
            Require(declaration.IntrinsicDescriptor?.Kind == kind, "intrinsic canonical identity");
        }
        foreach (var kind in new[] { LlvmIntrinsicKind.SCmp, LlvmIntrinsicKind.UCmp })
        {
            var declaration = LlvmIntrinsic.GetDeclaration(module, kind, i8, i32);
            Require(declaration.Name == kind.Name + ".i8.i32", "independently overloaded comparison signature");
            Require(declaration.ReturnType == i8 && declaration.FunctionType.Parameters.SequenceEqual(new LlvmType[] { i32, i32 }), "comparison result and argument types");
        }
        var cttz = LlvmIntrinsic.GetDescriptor(LlvmIntrinsicKind.CTTZ);
        Require(!cttz.NoCreateUndefOrPoison && cttz.IsImmediateArgument(1), "cttz original poison and immarg attributes");
        Require(LlvmIntrinsic.GetDescriptor(LlvmIntrinsicKind.CTPop).NoCreateUndefOrPoison, "ctpop original definedness attribute");
        return 12;
    }

    private static int UndefAndSimplification(LlvmModule module)
    {
        var context = module.Context;
        var i8 = LlvmIntegerType.Get(context, 8);
        var undef = LlvmUndefValue.Get(i8);
        var zero = LlvmConstantInt.Get(i8, 0);
        var one = LlvmConstantInt.Get(i8, 1);
        CheckBits(Fold(LlvmIntrinsicKind.CTPop, i8, [undef]), 0);
        CheckBits(Fold(LlvmIntrinsicKind.CTTZ, i8, [undef, LlvmConstantInt.GetFalse(context)]), 0);
        Require(Fold(LlvmIntrinsicKind.CTTZ, i8, [undef, LlvmConstantInt.GetTrue(context)]) is LlvmPoisonValue, "cttz undef with poison-zero");
        Require(Fold(LlvmIntrinsicKind.FShl, i8, [one, undef, zero]) == one, "fshl zero amount");
        Require(Fold(LlvmIntrinsicKind.FShl, i8, [undef, undef, one]) == undef, "fshl both undefined");
        Require(Fold(LlvmIntrinsicKind.FShl, i8, [one, zero, undef]) == one, "fshl undefined shift");
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8, i8]), LlvmLinkage.InternalLinkage, "simplify", module);
        var x = function.Arguments[0];
        var y = function.Arguments[1];
        var shift = new LlvmBinaryOperator(LlvmOpcode.Shl, one, x);
        Require(Simplify(LlvmIntrinsicKind.CTTZ, [shift, LlvmConstantInt.GetTrue(context)]) == x, "cttz shl one");
        var lowBit = new LlvmBinaryOperator(LlvmOpcode.And, x, one);
        Require(Simplify(LlvmIntrinsicKind.CTPop, [lowBit]) == lowBit, "ctpop low-bit identity");
        CheckBits(Simplify(LlvmIntrinsicKind.SCmp, [x, x]), 0);
        var intrinsic = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [x], module);
        var min = LlvmIntrinsic.CreateBinary(LlvmIntrinsicKind.UMin, intrinsic, y, module);
        _ = LlvmInstructionSimplify.SimplifyIntrinsic(LlvmIntrinsicKind.UMin, min, intrinsic, module.DataLayout);
        _ = LlvmConstantRangeAnalysis.Compute(intrinsic);
        return 11;

        LlvmValue? Simplify(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> args) =>
            LlvmInstructionSimplify.SimplifyIntrinsic(kind, i8, args, module.DataLayout);
    }

    private static IEnumerable<LlvmConstantRange> Ranges(uint width)
    {
        yield return LlvmConstantRange.Empty(width);
        yield return LlvmConstantRange.Full(width);
        int cardinality = 1 << (int)width;
        for (int lower = 0; lower < cardinality; ++lower)
            for (int upper = 0; upper < cardinality; ++upper)
                if (lower != upper) yield return new(new LlvmAPInt(width, (ulong)lower), new LlvmAPInt(width, (ulong)upper));
    }

    private static LlvmConstant? Fold(LlvmIntrinsicKind kind, LlvmType type, IReadOnlyList<LlvmConstant> args) =>
        LlvmConstantFolding.ConstantFoldIntrinsic(kind, args, type);
    private static void CheckBits(LlvmValue? value, ulong expected) =>
        Require(value is LlvmConstantInt integer && integer.Value.GetZExtValue() == expected, "scalar constant fold");
    private static void Require(bool result, string cause)
    {
        if (!result) throw new InvalidOperationException("Scalar LLVM intrinsic: " + cause);
    }
}
