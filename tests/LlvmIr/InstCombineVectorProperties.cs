using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineVectorProperties
{
    internal static int Run()
    {
        int checks = 0;
        GepBroadcastDemand();
        ReversePair();
        IdenticalShuffles();
        SelectShuffles();
        UnshuffleConstants(false);
        UnshuffleConstants(true);
        VpReversePair();
        SplicePair(LlvmIntrinsicKind.VectorSpliceLeft);
        SplicePair(LlvmIntrinsicKind.VectorSpliceRight);
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }

        void GepBroadcastDemand()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("demanded-vector-gep", context);
            var pointer = LlvmPointerType.Get(context);
            var pointers = LlvmVectorType.Get(pointer, 4, false);
            var indices = LlvmVectorType.Get(LlvmType.GetInt64Ty(context), 4, false);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(pointers, [pointer, indices]), LlvmLinkage.InternalLinkage, "broadcast-gep", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            var insert = new LlvmInsertElementInstruction(LlvmUndefValue.Get(pointers), function.Arguments[0], LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0));
            var splat = new LlvmShuffleVectorInstruction(insert, LlvmUndefValue.Get(pointers), [0, 0, 0, 0]);
            var gep = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(context), splat, [function.Arguments[1]]);
            var returned = new LlvmReturnInstruction(context, gep);
            block.Append(insert); block.Append(splat); block.Append(gep); block.Append(returned);
            Combine(function);
            Require(returned.ReturnValue is LlvmGetElementPtrInstruction result && result.PointerOperand == function.Arguments[0] && result.GetIndex(0) == function.Arguments[1],
                "Demanded vector GEP operands and actual splat scalarization retain the original dynamic index lanes.");
        }

        void ReversePair()
        {
            var f = new Fixture("reverse-pair");
            var first = f.Call(LlvmIntrinsicKind.VectorReverse, [f.Arg(0)]);
            var second = f.Call(LlvmIntrinsicKind.VectorReverse, [f.Arg(1)]);
            f.Return(f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, first, second)));
            f.Combine();
            Require(f.Result is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } call &&
                kind == LlvmIntrinsicKind.VectorReverse && call.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } sum &&
                sum.Left == f.Arg(0) && sum.Right == f.Arg(1), "Vector reverse moves after the source binop through the actual InstCombine controller.");
        }

        void IdenticalShuffles()
        {
            var f = new Fixture("identical-shuffles");
            int[] mask = [1, 0, 3, 2];
            var first = f.Add(new LlvmShuffleVectorInstruction(f.Arg(0), mask));
            var second = f.Add(new LlvmShuffleVectorInstruction(f.Arg(1), mask));
            f.Return(f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, first, second)));
            f.Combine();
            Require(f.Result is LlvmShuffleVectorInstruction shuffle && shuffle.ShuffleMask.SequenceEqual(mask) &&
                shuffle.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } sum && sum.Left == f.Arg(0) && sum.Right == f.Arg(1),
                "Two identical operand shuffles become one shuffle of the actual binop.");
        }

        void SelectShuffles()
        {
            var f = new Fixture("select-shuffles");
            int[] mask = [0, 5, 6, 3];
            var first = f.Add(new LlvmShuffleVectorInstruction(f.Arg(0), f.Arg(1), mask));
            var second = f.Add(new LlvmShuffleVectorInstruction(f.Arg(1), f.Arg(0), mask));
            f.Return(f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, first, second)));
            f.Combine();
            Require(f.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } sum && sum.Left == f.Arg(0) && sum.Right == f.Arg(1),
                "Commuted select shuffles cancel without manufacturing a new lane order.");
        }

        void UnshuffleConstants(bool conflicting)
        {
            var f = new Fixture(conflicting ? "conflicting-unshuffle" : "constant-unshuffle");
            var shuffled = f.Add(new LlvmShuffleVectorInstruction(f.Arg(0), [0, 0, 2, 2]));
            LlvmConstant constant = LlvmConstantVector.Get(new ulong[] { 1, conflicting ? 2UL : 1UL, 2, 2 }
                .Select(value => (LlvmConstant)LlvmConstantInt.Get(f.Element, value)).ToArray());
            f.Return(f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, shuffled, constant)));
            f.Combine();
            if (conflicting)
                Require(f.Result is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmShuffleVectorInstruction },
                    "Conflicting many-to-one constant lanes do not pass through the shuffle.");
            else
                Require(f.Result is LlvmShuffleVectorInstruction { Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstant moved } } &&
                    moved.GetAggregateElement(0) is LlvmConstantInt { Value.IsOne: true } &&
                    moved.GetAggregateElement(2) is LlvmConstantInt { Value: var bits } && bits.GetZExtValue() == 2,
                    "Compatible repeated shuffle lanes unshuffle constants to their source positions.");
        }

        void VpReversePair()
        {
            var f = new Fixture("vp-reverse-pair", true);
            var all = LlvmConstantVector.GetSplat(f.Vector.ElementCount, LlvmConstantInt.GetTrue(f.Context));
            var first = f.Call(LlvmIntrinsicKind.ExperimentalVpReverse, [f.Arg(0), all, f.Arg(2)]);
            var second = f.Call(LlvmIntrinsicKind.ExperimentalVpReverse, [f.Arg(1), all, f.Arg(2)]);
            f.Return(f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, first, second)));
            f.Combine();
            Require(f.Result is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var kind } call &&
                kind == LlvmIntrinsicKind.ExperimentalVpReverse && call.GetArgument(2) == f.Arg(2) &&
                call.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Add },
                "VP reverse retains the actual dynamic EVL when moving past the binop.");
        }

        void SplicePair(LlvmIntrinsicKind kind)
        {
            var f = new Fixture(kind == LlvmIntrinsicKind.VectorSpliceLeft ? "splice-left" : "splice-right", true);
            var poison = LlvmPoisonValue.Get(f.Vector);
            var first = f.Call(kind, [f.Arg(0), poison, f.Arg(2)]);
            var second = f.Call(kind, [f.Arg(1), poison, f.Arg(2)]);
            f.Return(f.Add(new LlvmBinaryOperator(LlvmOpcode.Add, first, second)));
            f.Combine();
            Require(f.Result is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor?.Kind == kind &&
                call.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } && call.GetArgument(1) is LlvmPoisonValue && call.GetArgument(2) == f.Arg(2),
                "Both splice directions preserve the source offset and poison operand when moving after a binop.");
        }
    }

    private static void Combine(LlvmFunction function)
    {
        var tree = new LlvmDominatorTree(function);
        using var assumptions = new LlvmAssumptionCache(function);
        LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
            new LlvmTargetLibraryInfo(function.Parent!), new LlvmTargetTransformInfo(function.Parent!.DataLayout), tree);
    }

    private sealed class Fixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmIntegerType Element;
        internal readonly LlvmVectorType Vector;
        private readonly LlvmModule module;
        private readonly LlvmFunction function;
        private readonly LlvmBasicBlock block;
        private LlvmReturnInstruction returned = null!;
        internal Fixture(string name, bool scalarArgument = false)
        {
            module = new("instcombine-vectors", Context);
            Element = LlvmType.GetInt8Ty(Context);
            Vector = LlvmVectorType.Get(Element, 4, false);
            LlvmType[] arguments = scalarArgument ? [Vector, Vector, LlvmType.GetInt32Ty(Context)] : [Vector, Vector];
            function = LlvmFunction.Create(LlvmFunctionType.Get(Vector, arguments), LlvmLinkage.InternalLinkage, name, module);
            block = new(Context, "entry", function);
        }
        internal LlvmValue Arg(int index) => function.Arguments[index];
        internal LlvmValue Result => returned.ReturnValue!;
        internal T Add<T>(T instruction) where T : LlvmInstruction { block.Append(instruction); return instruction; }
        internal LlvmCallInstruction Call(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments) => Add(LlvmIntrinsic.CreateIntrinsic(kind, arguments, Vector, module));
        internal void Return(LlvmValue value) => block.Append(returned = new(Context, value));
        internal void Combine() => InstCombineVectorProperties.Combine(function);
    }
}
