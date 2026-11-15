using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineFloatingCallProperties
{
    internal static int Run()
    {
        int checks = 0;
        PowerAndFma();
        MagnitudesAndRounding();
        ScalingAndPairs();
        Classes();
        return checks;

        void Require(bool condition, string message)
        { Program.Require(condition, message); ++checks; }

        void PowerAndFma()
        {
            var square = new Graph();
            square.Seal(square.Call(LlvmIntrinsicKind.PowI, [square.Arg(0), square.Int(2)]));
            square.Combine();
            Require(square.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul } mul && mul.Left == square.Arg(0) && mul.Right == square.Arg(0),
                "The real intrinsic call visitor lowers powi(x,2) to x*x without fast-math permission.");
            var reciprocal = new Graph();
            reciprocal.Seal(reciprocal.Call(LlvmIntrinsicKind.PowI, [reciprocal.Arg(0), reciprocal.Int(-1)]));
            reciprocal.Combine();
            Require(reciprocal.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, Left: LlvmConstantFP one } div && one.Value.BitwiseIsEqual(new LlvmAPFloat(1)) && div.Right == reciprocal.Arg(0),
                "powi(x,-1) becomes a real reciprocal operation, not an approximate pow call.");
            var negativeZero = new Graph();
            negativeZero.Seal(negativeZero.Call(LlvmIntrinsicKind.FMA, [negativeZero.Arg(0), negativeZero.Arg(1), negativeZero.FP(-0.0)]));
            negativeZero.Combine();
            Require(negativeZero.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul },
                "FMA's negative-zero addend permits multiplication without nsz.");
            var positiveZero = new Graph();
            positiveZero.Seal(positiveZero.Call(LlvmIntrinsicKind.FMA, [positiveZero.Arg(0), positiveZero.Arg(1), positiveZero.FP(0)]));
            positiveZero.Combine();
            Require(positiveZero.Result is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var retained } && retained == LlvmIntrinsicKind.FMA,
                "FMA's positive-zero addend must remain without nsz because signed-zero behavior differs.");
            var relaxedZero = new Graph();
            relaxedZero.Seal(relaxedZero.Call(LlvmIntrinsicKind.FMA, [relaxedZero.Arg(0), relaxedZero.Arg(1), relaxedZero.FP(0)], LlvmFastMathFlags.NoSignedZeros));
            relaxedZero.Combine();
            Require(relaxedZero.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, FastMathFlags: LlvmFastMathFlags.NoSignedZeros },
                "FMA's positive zero folds with the source nsz flag preserved on multiplication.");
            var doubleNeg = new Graph();
            var x = doubleNeg.Add(new LlvmUnaryOperator(doubleNeg.Arg(0)));
            var y = doubleNeg.Add(new LlvmUnaryOperator(doubleNeg.Arg(1)));
            doubleNeg.Seal(doubleNeg.Call(LlvmIntrinsicKind.FMA, [x, y, doubleNeg.Arg(2)]));
            doubleNeg.Combine();
            Require(doubleNeg.Result is LlvmCallInstruction call && call.GetArgument(0) == doubleNeg.Arg(0) && call.GetArgument(1) == doubleNeg.Arg(1),
                "Two FMA input negations cancel through the actual call worklist.");
        }

        void MagnitudesAndRounding()
        {
            var copy = new Graph();
            copy.Seal(copy.Call(LlvmIntrinsicKind.CopySign, [copy.Arg(0), copy.FP(-1)]));
            copy.Combine();
            Require(copy.Result is LlvmUnaryOperator neg && neg.Operand is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var abs } && abs == LlvmIntrinsicKind.FAbs,
                "Known negative copysign input produces fneg(fabs(magnitude)) using existing sign facts.");
            var fabs = new Graph();
            var opposite = fabs.Add(new LlvmUnaryOperator(fabs.Arg(0)));
            fabs.Seal(fabs.Call(LlvmIntrinsicKind.FAbs, [opposite]));
            fabs.Combine();
            Require(fabs.Result is LlvmCallInstruction absolute && absolute.GetArgument(0) == fabs.Arg(0) && absolute.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.FAbs,
                "fabs removes an input negation without changing its own absolute-value operation.");
            var narrow = new Graph();
            var single = LlvmType.GetFloatTy(narrow.Context);
            var truncated = narrow.Add(new LlvmCastInstruction(LlvmOpcode.FPTrunc, narrow.Arg(0), single));
            var extended = narrow.Add(new LlvmCastInstruction(LlvmOpcode.FPExt, truncated, narrow.Type));
            narrow.Seal(narrow.Call(LlvmIntrinsicKind.Floor, [extended]));
            narrow.Combine();
            Require(narrow.Result is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt, Operand: LlvmCallInstruction floor } && floor.Type == single && floor.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Floor,
                "A one-use FP extension allows the floor intrinsic itself to execute in the narrower source format.");
            var maximum = new Graph();
            var negative = maximum.Add(new LlvmUnaryOperator(maximum.Arg(0)));
            maximum.Seal(maximum.Call(LlvmIntrinsicKind.Maximum, [maximum.Arg(0), negative]));
            maximum.Combine();
            Require(maximum.Result is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } && kind == LlvmIntrinsicKind.FAbs,
                "max(x,-x) canonicalizes to fabs with the actual source floating min/max family.");
        }

        void ScalingAndPairs()
        {
            var scale = new Graph();
            scale.Seal(scale.Call(LlvmIntrinsicKind.Ldexp, [scale.Arg(0), scale.Int(2)]));
            scale.Combine();
            Require(scale.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, Right: LlvmConstantFP factor } && factor.Value.BitwiseIsEqual(new LlvmAPFloat(4)),
                "A representable ldexp constant exponent becomes the exact power-of-two multiply.");
            var trig = new Graph();
            var sin = trig.Call(LlvmIntrinsicKind.Sin, [trig.Arg(0)]);
            var cos = trig.Call(LlvmIntrinsicKind.Cos, [trig.Arg(0)]);
            trig.Seal(trig.Add(new LlvmBinaryOperator(LlvmOpcode.FAdd, sin, cos)));
            trig.Combine();
            var pairs = trig.Function.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmCallInstruction>()
                .Where(call => call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.SinCos).ToArray();
            Require(pairs.Length == 1 && pairs[0].Type is LlvmStructType { Elements.Count: 2 },
                "The actual shared-argument sin/cos fold creates one aggregate sincos call and removes the matching scalar call.");
            Require(!trig.Function.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmCallInstruction>()
                .Any(call => call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Sin || call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Cos),
                "Both sine and cosine uses are rewritten to the shared source aggregate, not left as duplicate calls.");
            var normalized = new Graph(exponent: true);
            var first = normalized.Call(LlvmIntrinsicKind.Frexp, [normalized.Arg(0)]);
            var fraction = normalized.Add(new LlvmExtractValueInstruction(first, [0]));
            var second = normalized.Call(LlvmIntrinsicKind.Frexp, [fraction]);
            normalized.Seal(normalized.Add(new LlvmExtractValueInstruction(second, [1])));
            normalized.Combine();
            Require(normalized.Result is LlvmConstantInt { IsZero: true },
                "The fraction of frexp is already normalized, so a second frexp returns exponent zero.");
        }

        void Classes()
        {
            var classify = new Graph(predicate: true);
            classify.Seal(classify.Call(LlvmIntrinsicKind.IsFPClass,
                [classify.Arg(0), classify.Int((int)LlvmFPClassTest.NaN)]));
            classify.Combine();
            Require(classify.Result is LlvmFCmpInstruction { Predicate: LlvmCmpPredicate.FCmpUno },
                "A non-strict NaN classification becomes the source unordered floating comparison.");
            var strict = new Graph(predicate: true);
            strict.Function.StrictFP = true;
            strict.Seal(strict.Call(LlvmIntrinsicKind.IsFPClass, [strict.Arg(0), strict.Int((int)LlvmFPClassTest.NaN)]));
            strict.Combine();
            Require(strict.Result is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } && kind == LlvmIntrinsicKind.IsFPClass,
                "Strict floating-point functions retain classification rather than introducing a possibly exception-raising comparison.");
        }
    }

    private sealed class Graph
    {
        internal readonly LlvmContext Context = new();
        private readonly LlvmModule module;
        internal readonly LlvmFunction Function;
        private readonly LlvmBasicBlock entry;
        internal readonly LlvmType Type;
        private LlvmReturnInstruction ret = null!;
        internal Graph(bool predicate = false, bool exponent = false)
        {
            module = new("instcombine-floating-calls", Context);
            Type = LlvmType.GetDoubleTy(Context);
            LlvmType result = predicate ? LlvmType.GetInt1Ty(Context) : exponent ? LlvmType.GetInt32Ty(Context) : Type;
            Function = LlvmFunction.Create(LlvmFunctionType.Get(result, [Type, Type, Type]), LlvmLinkage.InternalLinkage, "main", module);
            entry = new(Context, "entry", Function);
        }
        internal LlvmValue Arg(int index) => Function.Arguments[index];
        internal T Add<T>(T instruction) where T : LlvmInstruction { entry.Append(instruction); return instruction; }
        internal LlvmConstantFP FP(double value) => LlvmConstantFP.Get(Type, value);
        internal LlvmConstantInt Int(int value) => LlvmConstantInt.GetSigned(LlvmType.GetInt32Ty(Context), value);
        internal LlvmCallInstruction Call(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments, LlvmFastMathFlags flags = default)
        {
            var call = LlvmIntrinsic.CreateIntrinsic(kind, arguments, module);
            call.FastMathFlags = flags;
            return Add(call);
        }
        internal void Seal(LlvmValue value) => ret = Add(new LlvmReturnInstruction(Context, value));
        internal LlvmValue Result => ret.GetOperand(0)!;
        internal void Combine()
        {
            var dominators = new LlvmDominatorTree(Function);
            using var assumptions = new LlvmAssumptionCache(Function);
            LlvmInstCombine.Run(Function, new LlvmAAResults(Function, tree: dominators), assumptions,
                new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), dominators);
        }
    }
}
