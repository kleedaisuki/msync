using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineIntegerCastProperties
{
    internal static int Run()
    {
        int checks = 0;
        {
            Fixture f = new(8, 8, 8);
            LlvmValue x = f.Cast(LlvmOpcode.ZExt, f.Arg(0), 16), y = f.Cast(LlvmOpcode.ZExt, f.Arg(1), 16);
            f.Seal(f.Cast(LlvmOpcode.Trunc, f.Bin(LlvmOpcode.Add, x, y), 8)); f.Combine();
            // APInt's default constructor requires a fitting payload; this oracle intentionally wraps.
            for (ulong a = 0; a < 256; a += 7)
                for (ulong b = 0; b < 256; b += 11) RequireValue(f, new LlvmAPInt(8, a + b, implicitTrunc: true), "Truncation narrows the actual shared expression tree without changing wrapped sums.", a, b);
        }
        {
            Fixture f = new(16, 16, 16);
            LlvmValue sum = f.Bin(LlvmOpcode.Add, f.Cast(LlvmOpcode.Trunc, f.Arg(0), 8), f.Cast(LlvmOpcode.Trunc, f.Arg(1), 8));
            f.Seal(f.Cast(LlvmOpcode.ZExt, sum, 16)); f.Combine();
            ulong[] values = [0, 1, 127, 128, 254, 255, 256, 32767, 65535];
            foreach (ulong a in values) foreach (ulong b in values)
                RequireValue(f, new LlvmAPInt(8, a + b, implicitTrunc: true).ZExt(16), "Zero-extension evaluation retains the required high-bit clearing at carry boundaries.", a, b);
        }
        {
            Fixture f = new(16, 16);
            LlvmValue masked = f.Bin(LlvmOpcode.Or, f.Cast(LlvmOpcode.Trunc, f.Arg(0), 8), f.C(8, 3));
            f.Seal(f.Cast(LlvmOpcode.SExt, masked, 16)); f.Combine();
            for (ulong value = 0; value < 512; ++value)
                RequireValue(f, (new LlvmAPInt(8, value, implicitTrunc: true) | new LlvmAPInt(8, 3)).SExt(16), "Sign-extension preserves the narrow sign bit through truncation and bitwise operations.", value);
        }
        {
            Fixture f = new(1, 8);
            f.Seal(f.Cast(LlvmOpcode.Trunc, f.Bin(LlvmOpcode.LShr, f.C(8, 8), f.Arg(0)), 1)); f.Combine();
            for (ulong value = 0; value < 8; ++value)
                RequireValue(f, new LlvmAPInt(1, value == 3 ? 1UL : 0UL), "Boolean truncation of a shifted power of two retains all defined results.", value);
        }
        {
            Fixture f = new(16, 8, 8);
            f.Seal(f.Bin(LlvmOpcode.Or, f.Cast(LlvmOpcode.ZExt, f.Arg(0), 16), f.Cast(LlvmOpcode.ZExt, f.Arg(1), 16))); f.Combine();
            for (ulong a = 0; a < 256; a += 17) for (ulong b = 0; b < 256; b += 17)
                RequireValue(f, new LlvmAPInt(16, a | b), "Casted bitwise logic narrows both operands through the real worklist pass.", a, b);
        }
        {
            Fixture f = new(8, 8, 8, 8);
            LlvmValue x = f.Arg(0), y = f.Arg(1), z = f.Arg(2);
            LlvmValue left = f.Bin(LlvmOpcode.And, f.Not(f.Bin(LlvmOpcode.Or, x, y)), z);
            LlvmValue right = f.Bin(LlvmOpcode.And, f.Not(f.Bin(LlvmOpcode.Or, x, z)), y);
            f.Seal(f.Bin(LlvmOpcode.Or, left, right)); f.Combine();
            for (ulong a = 0; a < 16; ++a) for (ulong b = 0; b < 16; ++b) for (ulong c = 0; c < 16; ++c)
                RequireValue(f, new LlvmAPInt(8, (b ^ c) & ~a), "Complex complemented And/Or patterns preserve all three independent inputs.", a, b, c);
        }
        {
            Fixture f = new(8, 8, 8, 3);
            LlvmValue amount = f.Cast(LlvmOpcode.ZExt, f.Arg(2), 16);
            LlvmValue x = f.Cast(LlvmOpcode.ZExt, f.Arg(0), 16), y = f.Cast(LlvmOpcode.ZExt, f.Arg(1), 16);
            LlvmValue left = f.Bin(LlvmOpcode.Shl, x, amount);
            LlvmValue right = f.Bin(LlvmOpcode.LShr, y, f.Bin(LlvmOpcode.Sub, f.C(16, 8), amount));
            f.Seal(f.Cast(LlvmOpcode.Trunc, f.Bin(LlvmOpcode.Or, left, right), 8)); f.Combine();
            for (ulong a = 0; a < 256; a += 17) for (ulong b = 0; b < 256; b += 17) for (ulong bits = 0; bits < 8; ++bits)
                RequireValue(f, new LlvmAPInt(8, (a << (int)bits) | (b >> (int)(8 - bits)), implicitTrunc: true), "Narrow funnel-shift formation retains the zero-amount edge and both source operands.", a, b, bits);
        }
        return checks;
        void RequireValue(Fixture fixture, LlvmAPInt expected, string message, params ulong[] inputs)
        {
            var actual = fixture.Evaluate(inputs);
            if (actual != expected)
                Program.Require(false, $"{message} Inputs=[{string.Join(',', inputs)}], expected={expected.GetZExtValue()}, actual={actual.GetZExtValue()}.\nBefore:\n{fixture.Before}\nAfter:\n{fixture.Render()}");
            ++checks;
        }
    }
    private sealed class Fixture
    {
        private readonly LlvmContext context = new();
        private readonly LlvmModule module;
        private readonly LlvmFunction function;
        private readonly LlvmBasicBlock block;
        private LlvmReturnInstruction result = null!;
        internal string Before { get; private set; } = "";
        internal string Render() => LlvmIrWriter.Write(module);
        internal Fixture(uint resultWidth, params uint[] arguments)
        {
            module = new("instcombine-integer-casts", context);
            var types = arguments.Select(width => (LlvmType)LlvmType.GetIntNTy(context, width)).ToArray();
            function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetIntNTy(context, resultWidth), types), LlvmLinkage.InternalLinkage, "casts", module);
            block = new(context, "entry", function);
        }
        internal LlvmValue Arg(int index) => function.Arguments[index];
        internal LlvmConstantInt C(uint width, ulong value) => LlvmConstantInt.Get(LlvmType.GetIntNTy(context, width), value);
        internal LlvmValue Bin(LlvmOpcode opcode, LlvmValue left, LlvmValue right) => Add(new LlvmBinaryOperator(opcode, left, right));
        internal LlvmValue Not(LlvmValue value) => Bin(LlvmOpcode.Xor, value, LlvmConstantInt.Get(context, LlvmAPInt.GetAllOnes(value.Type.GetScalarSizeInBits())));
        internal LlvmValue Cast(LlvmOpcode opcode, LlvmValue value, uint width) => Add(new LlvmCastInstruction(opcode, value, LlvmType.GetIntNTy(context, width)));
        private T Add<T>(T instruction) where T : LlvmInstruction { block.Append(instruction); return instruction; }
        internal void Seal(LlvmValue value) { result = new LlvmReturnInstruction(context, value); block.Append(result); }
        internal void Combine()
        {
            Before = Render();
            using var assumptions = new LlvmAssumptionCache(function);
            var tree = new LlvmDominatorTree(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions, new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
        }
        internal LlvmAPInt Evaluate(params ulong[] arguments)
        {
            Dictionary<LlvmValue, LlvmAPInt> values = new(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < arguments.Length; ++index) values.Add(Arg(index), new(Arg(index).Type.GetScalarSizeInBits(), arguments[index]));
            return Eval(result.GetOperand(0)!);
            LlvmAPInt Eval(LlvmValue value)
            {
                if (values.TryGetValue(value, out var cached)) return cached;
                LlvmAPInt computed = value switch
                {
                    LlvmConstantInt integer => integer.Value,
                    LlvmBinaryOperator binary => Apply(binary.Opcode, Eval(binary.Left), Eval(binary.Right)),
                    LlvmCastInstruction cast => cast.Opcode == LlvmOpcode.SExt ? Eval(cast.Operand).SExtOrTrunc(cast.Type.GetScalarSizeInBits()) : Eval(cast.Operand).ZExtOrTrunc(cast.Type.GetScalarSizeInBits()),
                    LlvmICmpInstruction compare => new(1, Cmp(compare.Predicate, Eval(compare.Left), Eval(compare.Right)) ? 1UL : 0UL),
                    LlvmFreezeInstruction freeze => Eval(freeze.Operand),
                    LlvmSelectInstruction select => Eval(Eval(select.Condition).IsZero ? select.FalseValue : select.TrueValue),
                    LlvmCallBase call => Intrinsic(call),
                    _ => throw new InvalidOperationException($"Unexpected integer-cast journey value: {value.GetType().Name}")
                };
                values.Add(value, computed); return computed;
            }
            LlvmAPInt Intrinsic(LlvmCallBase call)
            {
                LlvmIntrinsicKind kind = call.CalledFunction!.IntrinsicDescriptor!.Kind;
                LlvmAPInt x = Eval(call.GetArgument(0)), y = Eval(call.GetArgument(1));
                uint width = x.BitWidth;
                if (kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr)
                {
                    uint amount = (uint)Eval(call.GetArgument(2)).URem(new(width, width)).GetZExtValue();
                    return kind == LlvmIntrinsicKind.FShl ? amount == 0 ? x : x.Shl(amount) | y.LShr(width - amount) : amount == 0 ? y : x.Shl(width - amount) | y.LShr(amount);
                }
                if (kind == LlvmIntrinsicKind.SMin) return x.Slt(y) ? x : y;
                if (kind == LlvmIntrinsicKind.SMax) return x.Sgt(y) ? x : y;
                if (kind == LlvmIntrinsicKind.UMin) return x.Ult(y) ? x : y;
                if (kind == LlvmIntrinsicKind.UMax) return x.Ugt(y) ? x : y;
                throw new InvalidOperationException($"Unexpected integer-cast journey intrinsic: {kind}");
            }
        }
        private static LlvmAPInt Apply(LlvmOpcode opcode, LlvmAPInt x, LlvmAPInt y) => opcode switch
        {
            LlvmOpcode.Add => x + y, LlvmOpcode.Sub => x - y, LlvmOpcode.Mul => x * y,
            LlvmOpcode.And => x & y, LlvmOpcode.Or => x | y, LlvmOpcode.Xor => x ^ y,
            LlvmOpcode.Shl => x.Shl(y), LlvmOpcode.LShr => x.LShr(y), LlvmOpcode.AShr => x.AShr(y),
            _ => throw new InvalidOperationException($"Unexpected integer-cast journey arithmetic: {opcode}")
        };
        private static bool Cmp(LlvmCmpPredicate predicate, LlvmAPInt x, LlvmAPInt y) => predicate switch
        {
            LlvmCmpPredicate.ICmpEq => x == y, LlvmCmpPredicate.ICmpNe => x != y,
            LlvmCmpPredicate.ICmpUlt => x.Ult(y), LlvmCmpPredicate.ICmpUle => x.Ule(y), LlvmCmpPredicate.ICmpUgt => x.Ugt(y), LlvmCmpPredicate.ICmpUge => x.Uge(y),
            LlvmCmpPredicate.ICmpSlt => x.Slt(y), LlvmCmpPredicate.ICmpSle => x.Sle(y), LlvmCmpPredicate.ICmpSgt => x.Sgt(y), LlvmCmpPredicate.ICmpSge => x.Sge(y),
            _ => throw new InvalidOperationException($"Unexpected integer-cast journey predicate: {predicate}")
        };
    }
}
