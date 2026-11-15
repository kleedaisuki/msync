using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineICmpConstantProperties
{
    internal static long Run()
    {
        long checks = NumericProperties();
        LlvmCmpPredicate[] unsignedPredicates = [LlvmCmpPredicate.ICmpEq, LlvmCmpPredicate.ICmpNe, LlvmCmpPredicate.ICmpUlt, LlvmCmpPredicate.ICmpUgt];
        LlvmCmpPredicate[] signedPredicates = [LlvmCmpPredicate.ICmpEq, LlvmCmpPredicate.ICmpNe, LlvmCmpPredicate.ICmpSlt, LlvmCmpPredicate.ICmpSgt];
        foreach (bool signed in new[] { false, true })
            foreach (ulong divisor in signed ? new ulong[] { 3, 251, 128 } : new ulong[] { 3, 5, 128 })
                foreach (ulong bound in new ulong[] { 0, 1, 127, 128, 255 })
                    foreach (var predicate in signed ? signedPredicates : unsignedPredicates)
                    {
                        Fixture fixture = new();
                        var divide = fixture.Add(new LlvmBinaryOperator(signed ? LlvmOpcode.SDiv : LlvmOpcode.UDiv, fixture.Input, fixture.C(divisor)));
                        fixture.Seal(fixture.Add(new LlvmICmpInstruction(predicate, divide, fixture.C(bound))));
                        fixture.Combine();
                        for (ulong input = 0; input < 256; ++input)
                        {
                            LlvmAPInt bits = new(8, input), factor = new(8, divisor), rhs = new(8, bound);
                            LlvmAPInt expected = signed ? bits.SDiv(factor) : bits.UDiv(factor);
                            Program.Require(fixture.Evaluate(input) == Compare(predicate, expected, rhs),
                                $"InstCombine division interval i8 signed={signed} divisor={divisor} predicate={predicate} bound={bound} input={input}");
                            ++checks;
                        }
                    }
        return checks;
    }

    private static long NumericProperties()
    {
        long checks = 0;
        for (ulong a = 0; a < 256; ++a)
            for (ulong b = 1; b < 256; ++b)
            {
                var left = new LlvmAPInt(8, a); var right = new LlvmAPInt(8, b);
                Program.Require(LlvmAPInt.GreatestCommonDivisor(left, right).GetZExtValue() == Gcd(a, b), "Stein GCD preserves all i8 pairs");
                Program.Require(LlvmAPInt.RoundingUDiv(left, right, LlvmAPIntRounding.Up).GetZExtValue() == (a + b - 1) / b, "Unsigned ceil division");
                long sa = a < 128 ? (long)a : (long)a - 256, sb = b < 128 ? (long)b : (long)b - 256;
                Program.Require(LlvmAPInt.GreatestCommonDivisor(left, right, isSigned: true).GetZExtValue() == Gcd((ulong)Math.Abs(sa), (ulong)Math.Abs(sb)), "Signed Stein GCD uses APInt absolute values");
                if (sa == -128 && sb == -1) continue;
                long q = sa / sb, r = sa % sb;
                long floor = r != 0 && (sa < 0) != (sb < 0) ? q - 1 : q;
                long ceil = r != 0 && (sa < 0) == (sb < 0) ? q + 1 : q;
                Program.Require(LlvmAPInt.RoundingSDiv(left, right, LlvmAPIntRounding.Down) == new LlvmAPInt(8, unchecked((ulong)floor), implicitTrunc: true), "Signed floor division");
                Program.Require(LlvmAPInt.RoundingSDiv(left, right, LlvmAPIntRounding.Up) == new LlvmAPInt(8, unchecked((ulong)ceil), implicitTrunc: true), "Signed ceil division");
                checks += 5;
            }
        for (ulong value = 0; value < 256; ++value)
        {
            var bits = new LlvmAPInt(8, value);
            Program.Require(LlvmAPInt.GreatestCommonDivisor(bits, LlvmAPInt.GetZero(8)) == bits, "GCD zero divisor fast path");
            ++checks;
        }
        foreach (uint width in new uint[] { 8, 64, 128, 257 })
        {
            LlvmAPInt root = LlvmAPInt.GetOneBitSet(width, width / 3) + new LlvmAPInt(width, 3);
            LlvmAPInt square = root * root, one = new(width, 1);
            Program.Require(square.SqrtFloor() == root && (square - one).SqrtFloor() == root - one && (square + one).SqrtFloor() == root,
                $"sqrtFloor table/hardware/Babylonian boundaries i{width}");
            checks += 3;
        }
        return checks;
        static ulong Gcd(ulong a, ulong b) { while (b != 0) (a, b) = (b, a % b); return a; }
    }

    private static bool Compare(LlvmCmpPredicate predicate, LlvmAPInt a, LlvmAPInt b) => predicate switch
    {
        LlvmCmpPredicate.ICmpEq => a == b, LlvmCmpPredicate.ICmpNe => a != b,
        LlvmCmpPredicate.ICmpUlt => a.Ult(b), LlvmCmpPredicate.ICmpUle => a.Ule(b), LlvmCmpPredicate.ICmpUgt => a.Ugt(b), LlvmCmpPredicate.ICmpUge => a.Uge(b),
        LlvmCmpPredicate.ICmpSlt => a.Slt(b), LlvmCmpPredicate.ICmpSle => a.Sle(b), LlvmCmpPredicate.ICmpSgt => a.Sgt(b), LlvmCmpPredicate.ICmpSge => a.Sge(b),
        _ => throw new InvalidOperationException($"Unexpected comparison {predicate}")
    };

    private sealed class Fixture
    {
        private readonly LlvmContext context = new();
        private readonly LlvmModule module;
        private readonly LlvmFunction function;
        private readonly LlvmBasicBlock block;
        private LlvmReturnInstruction result = null!;
        internal Fixture()
        {
            module = new("icmp-constants", context);
            var type = LlvmType.GetIntNTy(context, 8);
            function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetInt1Ty(context), [type]), LlvmLinkage.InternalLinkage, "icmp", module);
            block = new(context, "entry", function);
        }
        internal LlvmValue Input => function.Arguments[0];
        internal LlvmConstantInt C(ulong value) => LlvmConstantInt.Get((LlvmIntegerType)Input.Type, value);
        internal T Add<T>(T instruction) where T : LlvmInstruction { block.Append(instruction); return instruction; }
        internal void Seal(LlvmValue value) { result = new(context, value); block.Append(result); }
        internal void Combine()
        {
            using var assumptions = new LlvmAssumptionCache(function);
            var tree = new LlvmDominatorTree(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions, new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
        }
        internal bool Evaluate(ulong input)
        {
            Dictionary<LlvmValue, LlvmAPInt> values = new(ReferenceEqualityComparer.Instance) { [Input] = new(8, input) };
            return !Eval(result.GetOperand(0)!).IsZero;
            LlvmAPInt Eval(LlvmValue value)
            {
                if (values.TryGetValue(value, out var cached)) return cached;
                LlvmAPInt computed = value switch
                {
                    LlvmConstantInt integer => integer.Value,
                    LlvmBinaryOperator binary => Binary(binary.Opcode, Eval(binary.Left), Eval(binary.Right)),
                    LlvmICmpInstruction compare => new(1, Compare(compare.Predicate, Eval(compare.Left), Eval(compare.Right)) ? 1UL : 0UL),
                    LlvmCastInstruction cast => cast.Opcode == LlvmOpcode.SExt ? Eval(cast.Operand).SExtOrTrunc(cast.Type.GetIntegerBitWidth()) : Eval(cast.Operand).ZExtOrTrunc(cast.Type.GetIntegerBitWidth()),
                    LlvmSelectInstruction select => Eval(Eval(select.Condition).IsZero ? select.FalseValue : select.TrueValue),
                    _ => throw new InvalidOperationException($"Unexpected scalar ICmp result {value.GetType().Name}")
                };
                values.Add(value, computed); return computed;
            }
        }
        private static LlvmAPInt Binary(LlvmOpcode opcode, LlvmAPInt a, LlvmAPInt b) => opcode switch
        {
            LlvmOpcode.Add => a + b, LlvmOpcode.Sub => a - b, LlvmOpcode.Mul => a * b,
            LlvmOpcode.UDiv => a.UDiv(b), LlvmOpcode.SDiv => a.SDiv(b), LlvmOpcode.URem => a.URem(b), LlvmOpcode.SRem => a.SRem(b),
            LlvmOpcode.And => a & b, LlvmOpcode.Or => a | b, LlvmOpcode.Xor => a ^ b,
            LlvmOpcode.Shl => a.Shl(b), LlvmOpcode.LShr => a.LShr(b), LlvmOpcode.AShr => a.AShr(b),
            _ => throw new InvalidOperationException($"Unexpected scalar ICmp operation {opcode}")
        };
    }
}
