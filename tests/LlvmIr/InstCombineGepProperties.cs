using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineGepProperties
{
    internal static int Run()
    {
        int checks = 0;
        ConstantStructOffset();
        ExactIndexScale();
        VectorIndexAndSplatQueries();
        PreservedAlignment();
        return checks;

        void Require(bool condition, string message)
        {
            Program.Require(condition, message);
            ++checks;
        }

        void ConstantStructOffset()
        {
            Fixture f = new();
            var pair = LlvmStructType.Get(f.Context, [LlvmType.GetInt8Ty(f.Context), LlvmType.GetDoubleTy(f.Context)]);
            var address = f.Add(new LlvmGetElementPtrInstruction(pair, f.Function.Arguments[0],
                [f.C(0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(f.Context), 1)], LlvmGepNoWrapFlags.InBounds));
            f.Seal(address); f.Combine();
            Require(f.Return.GetOperand(0) is LlvmGetElementPtrInstruction canonical && canonical.SourceElementType.IsIntegerTy(8) &&
                canonical.PointerOperand == f.Function.Arguments[0] && canonical.IndexCount == 1 &&
                canonical.GetIndex(0) is LlvmConstantInt { Value: var offset } && offset.GetZExtValue() == 8,
                "The full GEP visitor canonicalizes the actual struct field byte offset through the source worklist.");
            Require(((LlvmGetElementPtrInstruction)f.Return.GetOperand(0)!).NoWrapFlags.IsInBounds,
                "Constant byte-offset canonicalization preserves the original inbounds flag.");
        }

        void ExactIndexScale()
        {
            Fixture f = new();
            var divide = f.Add(new LlvmBinaryOperator(LlvmOpcode.SDiv, f.Function.Arguments[1], f.C(4)) { IsExact = true });
            var bytes = LlvmArrayType.Get(LlvmType.GetInt8Ty(f.Context), 4);
            var address = f.Add(new LlvmGetElementPtrInstruction(bytes, f.Function.Arguments[0], [divide]));
            f.Seal(address); f.Combine();
            Require(f.Return.GetOperand(0) is LlvmGetElementPtrInstruction canonical && canonical.SourceElementType.IsIntegerTy(8) &&
                canonical.PointerOperand == f.Function.Arguments[0] && canonical.GetIndex(0) == f.Function.Arguments[1],
                "Exact index division cancels the matching element scale rather than emitting two offset operations.");
        }

        void VectorIndexAndSplatQueries()
        {
            Fixture f = new();
            var type = LlvmFixedVectorType.Get(f.Integer, 4);
            LlvmConstant index = LlvmConstantVector.GetSplat(type.ElementCount, f.C(2));
            var address = f.Add(new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(f.Context), f.Function.Arguments[0], [index]));
            var typed = f.Add(new LlvmGetElementPtrInstruction(LlvmType.GetInt32Ty(f.Context), f.Function.Arguments[0], [index]));
            var insert = f.Add(new LlvmInsertElementInstruction(LlvmPoisonValue.Get(type), f.Function.Arguments[1], f.C(0)));
            var shuffle = f.Add(new LlvmShuffleVectorInstruction(insert, LlvmPoisonValue.Get(type), [0, -1, 0, 0]));
            var uniform = f.Add(new LlvmShuffleVectorInstruction(insert, LlvmPoisonValue.Get(type), [0, 0, 0, 0]));
            f.Seal(f.Function.Arguments[0]);
            var offset = LlvmAPInt.GetZero(64);
            Require(!LlvmValueTracking.AccumulateConstantOffset(address, f.Module.DataLayout, ref offset),
                "The source scalar constant-offset query must not collapse a vector ConstantInt index into one scalar address.");
            Require(!LlvmValueTracking.AccumulateConstantOffset(typed, f.Module.DataLayout, ref offset),
                "The general sequential-offset path retains the same scalar-only source index condition as the i8 fast path.");
            Require(LlvmValueTracking.GetSplatValue(shuffle) == f.Function.Arguments[1],
                "The extraction query recognizes a scalar-at-zero broadcast with poison mask lanes.");
            Require(!LlvmValueTracking.IsSplatValue(shuffle),
                "The independent source isSplatValue query requires equal shuffle-mask lanes, not the broader extraction predicate.");
            Require(LlvmValueTracking.IsSplatValue(uniform) && LlvmValueTracking.IsSplatValue(uniform, 0) &&
                !LlvmValueTracking.IsSplatValue(uniform, 1),
                "The source requested-index predicate is preserved independently of whether an arbitrary splat can be extracted.");
        }

        void PreservedAlignment()
        {
            Fixture f = new();
            var bytes = LlvmArrayType.Get(LlvmType.GetInt8Ty(f.Context), 8);
            var variable = f.Add(new LlvmGetElementPtrInstruction(bytes, f.Function.Arguments[0], [f.Function.Arguments[1]]));
            var constant = f.Add(new LlvmGetElementPtrInstruction(bytes, f.Function.Arguments[0], [f.C(4)]));
            var zero = f.Add(new LlvmGetElementPtrInstruction(bytes, f.Function.Arguments[0], [f.C(0)]));
            f.Seal(f.Function.Arguments[0]);
            Require(LlvmGepOperator.Get(variable).GetMaxPreservedAlignment(f.Module.DataLayout).Value == 8,
                "An unknown sequential index preserves the source stride alignment using the source worst-offset count of one.");
            Require(LlvmGepOperator.Get(constant).GetMaxPreservedAlignment(f.Module.DataLayout).Value == 32,
                "A constant sequential index uses its actual byte displacement in the preserved-alignment query.");
            Require(LlvmGepOperator.Get(zero).GetMaxPreservedAlignment(f.Module.DataLayout).Value == LlvmValue.MaximumAlignment,
                "A zero displacement retains the source maximum alignment instead of inventing a base-pointer alignment fact.");
        }
    }

    private sealed class Fixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmModule Module;
        internal readonly LlvmIntegerType Integer;
        internal readonly LlvmFunction Function;
        internal readonly LlvmBasicBlock Block;
        internal LlvmReturnInstruction Return = null!;

        internal Fixture()
        {
            Module = new("instcombine-gep", Context);
            Integer = LlvmType.GetInt64Ty(Context);
            var pointer = LlvmPointerType.Get(Context);
            Function = LlvmFunction.Create(LlvmFunctionType.Get(pointer, [pointer, Integer]), LlvmLinkage.InternalLinkage, "gep", Module);
            Block = new(Context, "entry", Function);
        }

        internal LlvmConstantInt C(ulong value) => LlvmConstantInt.Get(Integer, value);
        internal T Add<T>(T instruction) where T : LlvmInstruction { Block.Append(instruction); return instruction; }
        internal void Seal(LlvmValue value) { Return = new(Context, value); Block.Append(Return); }
        internal void Combine()
        {
            var tree = new LlvmDominatorTree(Function);
            using var assumptions = new LlvmAssumptionCache(Function);
            LlvmInstCombine.Run(Function, new LlvmAAResults(Function, tree: tree), assumptions,
                new LlvmTargetLibraryInfo(Module), new LlvmTargetTransformInfo(Module.DataLayout), tree);
        }
    }
}
