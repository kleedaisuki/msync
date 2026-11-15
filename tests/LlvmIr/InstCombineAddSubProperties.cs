using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineAddSubProperties
{
    internal static int Run()
    {
        int checks = 0;
        IntegerProperties();
        FloatingJourneys();
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
        void IntegerProperties()
        {
            var f = new Fixture(false, 2);
            var a = f.Arg(0); var b = f.Arg(1);
            var square = f.Bin(LlvmOpcode.Mul, a, a);
            var twice = f.Bin(LlvmOpcode.Shl, a, f.Int(1));
            var cross = f.Bin(LlvmOpcode.Mul, f.Bin(LlvmOpcode.Add, twice, b), b);
            f.Seal(f.Bin(LlvmOpcode.Add, square, cross));
            f.Combine();
            for (ulong x = 0; x < 32; ++x)
                for (ulong y = 0; y < 32; ++y)
                    Require(f.EvaluateInteger(x, y) == new LlvmAPInt(8, (x + y) * (x + y), implicitTrunc: true),
                        "Real InstCombine preserves the modular square-sum identity across independent inputs.");
            var g = new Fixture(false, 1);
            var quotient = g.Bin(LlvmOpcode.UDiv, g.Arg(0), g.Int(3));
            var remainder = g.Bin(LlvmOpcode.URem, g.Arg(0), g.Int(3));
            g.Seal(g.Bin(LlvmOpcode.Add, g.Bin(LlvmOpcode.Mul, quotient, g.Int(3)), remainder));
            g.Combine();
            for (ulong x = 0; x < 256; ++x)
                Require(g.EvaluateInteger(x) == new LlvmAPInt(8, x), "Quotient/remainder reconstruction survives the real source worklist.");
        }
        void FloatingJourneys()
        {
            var f = new Fixture(true, 2);
            var negative = f.Add(new LlvmUnaryOperator(f.Arg(0)));
            f.Seal(f.Bin(LlvmOpcode.FAdd, negative, f.Arg(1)));
            f.Combine();
            Require(f.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } subtraction &&
                ReferenceEquals(subtraction.Left, f.Arg(1)) && ReferenceEquals(subtraction.Right, f.Arg(0)),
                "FAdd canonicalizes a negated operand to subtraction without inventing fast-math flags.");
            var g = new Fixture(true, 2);
            var product = g.Bin(LlvmOpcode.FMul, g.Arg(0), g.FP(2));
            product.FastMathFlags = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.AllowContract;
            var negation = g.Add(new LlvmUnaryOperator(product)
                { FastMathFlags = LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.AllowContract });
            g.Seal(negation); g.Combine();
            Require(g.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, Right: LlvmConstantFP constant } multiplication && constant.IsNegative &&
                (multiplication.FastMathFlags & LlvmFastMathFlags.NoInfs) == 0 &&
                (multiplication.FastMathFlags & (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.AllowContract)) ==
                (LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.AllowContract),
                "FNeg constant absorption unions value flags, intersects rewrite flags, and separately intersects ninf.");
        }
    }
    private sealed class Fixture
    {
        private readonly LlvmContext context = new();
        private readonly LlvmModule module;
        private readonly LlvmFunction function;
        private readonly LlvmBasicBlock block;
        private readonly LlvmType type;
        private LlvmReturnInstruction result = null!;
        internal Fixture(bool floating, int arguments)
        {
            module = new("instcombine-addsub", context);
            type = floating ? LlvmType.GetDoubleTy(context) : LlvmType.GetInt8Ty(context);
            function = LlvmFunction.Create(LlvmFunctionType.Get(type, Enumerable.Repeat(type, arguments).ToArray()), LlvmLinkage.InternalLinkage, "arithmetic", module);
            block = new(context, "entry", function);
        }
        internal LlvmValue Arg(int index) => function.Arguments[index];
        internal LlvmConstantInt Int(ulong value) => LlvmConstantInt.Get((LlvmIntegerType)type, value);
        internal LlvmConstantFP FP(double value) => LlvmConstantFP.Get(type, value);
        internal T Add<T>(T instruction) where T : LlvmInstruction { block.Append(instruction); return instruction; }
        internal LlvmBinaryOperator Bin(LlvmOpcode opcode, LlvmValue left, LlvmValue right) => Add(new LlvmBinaryOperator(opcode, left, right));
        internal LlvmValue Result => result.GetOperand(0)!;
        internal void Seal(LlvmValue value) { result = Add(new LlvmReturnInstruction(context, value)); }
        internal void Combine()
        {
            var tree = new LlvmDominatorTree(function);
            using var assumptions = new LlvmAssumptionCache(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, module.DataLayout, tree), assumptions,
                new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
        }
        internal LlvmAPInt EvaluateInteger(params ulong[] arguments)
        {
            Dictionary<LlvmValue, LlvmAPInt> values = new(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < arguments.Length; ++i) values[Arg(i)] = new(8, arguments[i]);
            return Evaluate(Result);
            LlvmAPInt Evaluate(LlvmValue value)
            {
                if (values.TryGetValue(value, out var cached)) return cached;
                if (value is LlvmConstantInt constant) return constant.Value;
                // All fixture arguments and evaluated arithmetic are concrete defined values.
                if (value is LlvmFreezeInstruction freeze) return Evaluate(freeze.Operand);
                if (value is LlvmCastInstruction cast) return cast.Opcode == LlvmOpcode.SExt ? Evaluate(cast.Operand).SExtOrTrunc(cast.Type.GetScalarSizeInBits()) : Evaluate(cast.Operand).ZExtOrTrunc(cast.Type.GetScalarSizeInBits());
                if (value is not LlvmBinaryOperator binary) throw new InvalidOperationException($"Unexpected AddSub result {value.GetType().Name}");
                var left = Evaluate(binary.Left); var right = Evaluate(binary.Right);
                var result = binary.Opcode switch
                {
                    LlvmOpcode.Add => left + right, LlvmOpcode.Sub => left - right, LlvmOpcode.Mul => left * right,
                    LlvmOpcode.And => left & right, LlvmOpcode.Or => left | right, LlvmOpcode.Xor => left ^ right,
                    LlvmOpcode.Shl => left.Shl(right), LlvmOpcode.LShr => left.LShr(right), LlvmOpcode.AShr => left.AShr(right),
                    LlvmOpcode.UDiv => left.UDiv(right), LlvmOpcode.URem => left.URem(right),
                    _ => throw new InvalidOperationException($"Unexpected AddSub opcode {binary.Opcode}")
                };
                values.Add(value, result); return result;
            }
        }
    }
}
