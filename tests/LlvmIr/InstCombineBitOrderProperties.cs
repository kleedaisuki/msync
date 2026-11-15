using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineBitOrderProperties
{
    internal static int Run()
    {
        int checks = 0;
        Recognize(16, false);
        Recognize(8, true);
        Pack();
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }

        void Recognize(uint width, bool reverseBits)
        {
            var context = new LlvmContext();
            var module = new LlvmModule("bit-order", context);
            var type = LlvmIntegerType.Get(context, width);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "permute", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            LlvmValue? joined = null;
            uint unit = reverseBits ? 1u : 8u;
            for (uint offset = 0; offset < width; offset += unit)
            {
                LlvmValue piece = function.Arguments[0];
                if (offset != 0) piece = Append(new LlvmBinaryOperator(LlvmOpcode.LShr, piece, LlvmConstantInt.Get(type, offset)));
                piece = Append(new LlvmBinaryOperator(LlvmOpcode.And, piece, LlvmConstantInt.Get(type, (1ul << (int)unit) - 1)));
                uint destination = width - unit - offset;
                if (destination != 0) piece = Append(new LlvmBinaryOperator(LlvmOpcode.Shl, piece, LlvmConstantInt.Get(type, destination)));
                joined = joined is null ? piece : Append(new LlvmBinaryOperator(LlvmOpcode.Or, joined, piece));
            }
            var root = (LlvmInstruction)joined!;
            block.Append(new LlvmReturnInstruction(context, root));
            List<LlvmInstruction> inserted = [];
            Require(LlvmLocal.RecognizeBSwapOrBitReverseIdiom(root, true, true, inserted), "The complete shift/mask permutation is recognized through the shared source helper.");
            Require(inserted[^1] is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } &&
                descriptor.Kind == (reverseBits ? LlvmIntrinsicKind.BitReverse : LlvmIntrinsicKind.BSwap), "The recognized operation uses the real standard intrinsic identity.");
            Require(((LlvmCallInstruction)inserted[^1]).GetArgument(0) == function.Arguments[0], "All provenance resolves to the original dynamic provider.");
            LlvmInstruction Append(LlvmInstruction instruction) { block.Append(instruction); return instruction; }
        }

        void Pack()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("vector-integer-pack", context);
            var byteType = LlvmType.GetInt8Ty(context);
            var resultType = LlvmType.GetInt16Ty(context);
            var vectorType = LlvmVectorType.Get(byteType, 4, false);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(resultType, [vectorType]), LlvmLinkage.InternalLinkage, "pack", module);
            var block = new LlvmBasicBlock(context, "entry", function);
            var first = new LlvmExtractElementInstruction(function.Arguments[0], LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 0));
            var second = new LlvmExtractElementInstruction(function.Arguments[0], LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1));
            var low = new LlvmCastInstruction(LlvmOpcode.ZExt, first, resultType);
            var high = new LlvmCastInstruction(LlvmOpcode.ZExt, second, resultType);
            var shifted = new LlvmBinaryOperator(LlvmOpcode.Shl, high, LlvmConstantInt.Get(resultType, 8));
            var joined = new LlvmBinaryOperator(LlvmOpcode.Or, low, shifted);
            var returned = new LlvmReturnInstruction(context, joined);
            foreach (var instruction in new LlvmInstruction[] { first, second, low, high, shifted, joined, returned }) block.Append(instruction);
            var tree = new LlvmDominatorTree(function);
            using var assumptions = new LlvmAssumptionCache(function);
            LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
                new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
            Require(returned.ReturnValue is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, Operand: LlvmShuffleVectorInstruction shuffle } &&
                shuffle.Left == function.Arguments[0] && shuffle.ShuffleMask.SequenceEqual(new[] { 0, 1 }), "The user-visible combine pipeline packs adjacent dynamic vector bytes with one shuffle and bitcast.");
        }
    }
}
