using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineShiftProperties
{
    internal static int Run()
    {
        int checks = 0;
        foreach (LlvmOpcode outer in new[] { LlvmOpcode.Shl, LlvmOpcode.LShr, LlvmOpcode.AShr })
        {
            Fixture f = new(8, 1);
            var inner = f.Add(new LlvmBinaryOperator(outer == LlvmOpcode.Shl ? LlvmOpcode.LShr : LlvmOpcode.Shl, f.Arg(0), f.C(2)));
            var expression = f.Add(new LlvmBinaryOperator(outer, inner, f.C(4)));
            f.Seal(expression); f.Combine();
            for (ulong x = 0; x < 256; ++x)
            {
                LlvmAPInt original = new(8, x);
                LlvmAPInt expected = outer switch
                { LlvmOpcode.Shl => original.LShr(2).Shl(4), LlvmOpcode.LShr => original.Shl(2).LShr(4), _ => original.Shl(2).AShr(4) };
                Require(f.Evaluate(x) == expected, "Combined opposite-direction shifts preserve every defined i8 result.");
            }
        }
        {
            Fixture f = new(8, 2);
            var first = f.Add(new LlvmBinaryOperator(LlvmOpcode.Shl, f.Arg(0), f.C(2)));
            var second = f.Add(new LlvmBinaryOperator(LlvmOpcode.Shl, f.Arg(1), f.C(4)));
            var or = f.Add(new LlvmBinaryOperator(LlvmOpcode.Or, first, second));
            f.Seal(f.Add(new LlvmBinaryOperator(LlvmOpcode.Shl, or, f.C(2)))); f.Combine();
            for (ulong x = 0; x < 32; ++x)
                for (ulong y = 0; y < 32; ++y)
                    Require(f.Evaluate(x, y) == (new LlvmAPInt(8, x).Shl(2) | new LlvmAPInt(8, y).Shl(4)).Shl(2),
                        "Expression shift propagation preserves both data inputs through the real worklist pass.");
        }
        {
            Fixture f = new(8, 2, argumentWidth: 4);
            var left = f.Add(new LlvmCastInstruction(LlvmOpcode.ZExt, f.Arg(0), f.Type));
            var right = f.Add(new LlvmCastInstruction(LlvmOpcode.ZExt, f.Arg(1), f.Type));
            var add = f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, left, right));
            f.Seal(f.Add(new LlvmBinaryOperator(LlvmOpcode.LShr, add, f.C(4)))); f.Combine();
            for (ulong x = 0; x < 16; ++x)
                for (ulong y = 0; y < 16; ++y)
                    Require(f.Evaluate(x, y) == new LlvmAPInt(8, (x + y) >> 4),
                        "Overflow-bit narrowing preserves unsigned carry across all i4 input pairs.");
        }
        {
            Fixture f = new(8, 2);
            var difference = f.Add(new LlvmBinaryOperator(LlvmOpcode.Sub, f.C(8), f.Arg(1)));
            var high = f.Add(new LlvmBinaryOperator(LlvmOpcode.LShr, f.Arg(0), difference));
            var left = f.Add(new LlvmBinaryOperator(LlvmOpcode.Shl, high, difference));
            f.Seal(f.Add(new LlvmBinaryOperator(LlvmOpcode.AShr, left, difference))); f.Combine();
            for (ulong x = 0; x < 256; ++x)
                for (ulong bits = 1; bits < 8; ++bits)
                    Require(f.Evaluate(x, bits) == new LlvmAPInt(8, x).LShr((uint)(8 - bits)).Shl((uint)(8 - bits)).AShr((uint)(8 - bits)),
                        "Variable high-bit extraction retains sign extension for every defined shift amount.");
        }
        {
            Fixture f = new(8, 1);
            LlvmFunction declaration = LlvmIntrinsic.GetDeclaration(f.Module, LlvmIntrinsicKind.CTTZ, f.Type);
            var count = f.Add(new LlvmCallInstruction(declaration.FunctionType, declaration, [f.Arg(0), LlvmConstantInt.Get(LlvmType.GetInt1Ty(f.Context), 0)]));
            f.Seal(f.Add(new LlvmBinaryOperator(LlvmOpcode.Shl, f.C(1), count))); f.Combine();
            for (ulong x = 1; x < 256; ++x)
                Require(f.Evaluate(x) == new LlvmAPInt(8, x & unchecked(0UL - x)), "Represented cttz shift canonicalizes without changing defined low-bit extraction.");
        }
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    private sealed class Fixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmModule Module;
        internal readonly LlvmIntegerType Type;
        private readonly LlvmFunction function;
        private readonly LlvmBasicBlock block;
        private LlvmReturnInstruction result = null!;
        internal Fixture(uint width, int arguments, uint? argumentWidth = null)
        {
            Module = new("instcombine-shifts", Context); Type = LlvmType.GetIntNTy(Context, width);
            var argumentType = LlvmType.GetIntNTy(Context, argumentWidth ?? width);
            function = LlvmFunction.Create(LlvmFunctionType.Get(Type, Enumerable.Repeat<LlvmType>(argumentType, arguments).ToArray()), LlvmLinkage.InternalLinkage, "shift", Module);
            block = new(Context, "entry", function);
        }
        internal LlvmValue Arg(int index) => function.Arguments[index];
        internal LlvmConstantInt C(ulong value) => LlvmConstantInt.Get(Type, value);
        internal T Add<T>(T value) where T : LlvmInstruction { block.Append(value); return value; }
        internal void Seal(LlvmValue value) { result = new LlvmReturnInstruction(Context, value); block.Append(result); }
        internal void Combine()
        {
            using var assumptions = new LlvmAssumptionCache(function);
            var tree = new LlvmDominatorTree(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions, new LlvmTargetLibraryInfo(Module), new LlvmTargetTransformInfo(Module.DataLayout), tree);
        }
        internal LlvmAPInt Evaluate(params ulong[] arguments)
        {
            Dictionary<LlvmValue, LlvmAPInt> values = new(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < arguments.Length; ++index)
                values.Add(Arg(index), new(Arg(index).Type.GetScalarSizeInBits(), arguments[index]));
            return Eval(result.GetOperand(0)!);
            LlvmAPInt Eval(LlvmValue value)
            {
                if (values.TryGetValue(value, out LlvmAPInt cached)) return cached;
                LlvmAPInt computed = value switch
                {
                    LlvmConstantInt integer => integer.Value,
                    LlvmBinaryOperator binary => Binary(binary.Opcode, Eval(binary.Left), Eval(binary.Right)),
                    LlvmCastInstruction cast => cast.Opcode == LlvmOpcode.SExt ? Eval(cast.Operand).SExtOrTrunc(cast.Type.GetScalarSizeInBits()) : Eval(cast.Operand).ZExtOrTrunc(cast.Type.GetScalarSizeInBits()),
                    LlvmICmpInstruction compare => new(1, Compare(compare.Predicate, Eval(compare.Left), Eval(compare.Right)) ? 1UL : 0UL),
                    LlvmFreezeInstruction freeze => Eval(freeze.Operand),
                    LlvmSelectInstruction select => Eval(Eval(select.Condition).IsZero ? select.FalseValue : select.TrueValue),
                    LlvmCallBase call when call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CTTZ => new(value.Type.GetScalarSizeInBits(), Eval(call.GetArgument(0)).CountTrailingZeros()),
                    _ => throw new InvalidOperationException($"Unexpected value in scalar shift journey: {value.GetType().Name}")
                };
                values.Add(value, computed); return computed;
            }
        }
        private static LlvmAPInt Binary(LlvmOpcode opcode, LlvmAPInt left, LlvmAPInt right) => opcode switch
        {
            LlvmOpcode.Add => left + right, LlvmOpcode.Sub => left - right, LlvmOpcode.Mul => left * right,
            LlvmOpcode.And => left & right, LlvmOpcode.Or => left | right, LlvmOpcode.Xor => left ^ right,
            LlvmOpcode.Shl => left.Shl(right), LlvmOpcode.LShr => left.LShr(right), LlvmOpcode.AShr => left.AShr(right),
            _ => throw new InvalidOperationException($"Unexpected arithmetic opcode {opcode}")
        };
        private static bool Compare(LlvmCmpPredicate predicate, LlvmAPInt left, LlvmAPInt right) => predicate switch
        {
            LlvmCmpPredicate.ICmpEq => left == right, LlvmCmpPredicate.ICmpNe => left != right,
            LlvmCmpPredicate.ICmpUlt => left.Ult(right), LlvmCmpPredicate.ICmpUle => left.Ule(right),
            LlvmCmpPredicate.ICmpUgt => left.Ugt(right), LlvmCmpPredicate.ICmpUge => left.Uge(right),
            LlvmCmpPredicate.ICmpSlt => left.Slt(right), LlvmCmpPredicate.ICmpSle => left.Sle(right),
            LlvmCmpPredicate.ICmpSgt => left.Sgt(right), LlvmCmpPredicate.ICmpSge => left.Sge(right),
            _ => throw new InvalidOperationException($"Unexpected compare predicate {predicate}")
        };
    }
}
