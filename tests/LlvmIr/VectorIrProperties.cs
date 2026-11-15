using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class VectorIrProperties
{
    internal static int Run()
    {
        int checks = 0;
        Instructions();
        Constants();
        Masks();
        Builder();
        return checks;

        void Instructions()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("vector-ir", context);
            var integer = LlvmIntegerType.Get(context, 32);
            var type = LlvmFixedVectorType.Get(integer, 4);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [type, type, integer, LlvmIntegerType.Get(context, 64)]),
                LlvmLinkage.InternalLinkage, "vector-model", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var vector = function.Arguments[0]; var other = function.Arguments[1];
            var element = function.Arguments[2]; var index = function.Arguments[3];
            var extract = new LlvmExtractElementInstruction(vector, index);
            var insert = new LlvmInsertElementInstruction(vector, element, index);
            var shuffle = new LlvmShuffleVectorInstruction(insert, other, [3, 2, 1, 0]);
            entry.Append(extract); entry.Append(insert); entry.Append(shuffle);
            entry.Append(new LlvmReturnInstruction(context, extract));
            Program.Require(extract.Opcode == LlvmOpcode.ExtractElement && extract.Type == integer && insert.Type == type
                && shuffle.OperandCount == 2 && shuffle.ShuffleMask.Count == 4 && shuffle.IsReverse, "Vector instruction shape or source mask state changed.");
            extract.GetOperandUse(0).LogicalType = 17;
            var clone = (LlvmExtractElementInstruction)extract.Clone();
            Program.Require(clone.GetOperandUse(0).LogicalType == 17 && clone.VectorOperand == vector && clone.Parent is null,
                "Vector clone lost actual operand use annotations.");
            clone.DropAllReferences();
            var shuffleClone = (LlvmShuffleVectorInstruction)shuffle.Clone();
            shuffleClone.SetShuffleMask([0, 1, 2, 3]);
            Program.Require(!shuffle.IsIdenticalToWhenDefined(shuffleClone) && shuffle.GetMaskValue(0) == 3,
                "Shuffle clone shared mutable masks or mask equality was ignored.");
            shuffleClone.DropAllReferences();
            vector.ReplaceAllUsesWith(other);
            Program.Require(extract.VectorOperand == other && insert.VectorOperand == other && extract.GetOperandUse(0).LogicalType == 17,
                "Vector operands are not real RAUW-tracked Uses.");
            Program.Require(LlvmExtractElementInstruction.IsValidOperands(other, LlvmConstantInt.Get(LlvmIntegerType.Get(context, 64), 99)),
                "Out-of-range lane was rejected instead of preserving LLVM's poison-producing operation.");
            string rendered = LlvmIrWriter.Write(module);
            Program.Require(rendered.Contains("extractelement <4 x i32>", StringComparison.Ordinal)
                && rendered.Contains("insertelement <4 x i32>", StringComparison.Ordinal)
                && rendered.Contains("shufflevector <4 x i32>", StringComparison.Ordinal), "Vector IR is not rendered through actual standard instruction forms.");
            checks += 6;
        }

        void Constants()
        {
            var context = new LlvmContext();
            var integer = LlvmIntegerType.Get(context, 32);
            var values = new LlvmConstant[] { LlvmConstantInt.Get(integer, 1), LlvmConstantInt.Get(integer, 2), LlvmConstantInt.Get(integer, 3), LlvmConstantInt.Get(integer, 4) };
            var vector = LlvmConstantVector.Get(values);
            Program.Require(vector is LlvmConstantDataVector data && data.GetElementAsInteger(2) == 3
                && ReferenceEquals(vector, LlvmConstantVector.Get(values)), "Vector numeric constants lost typed-data canonicalization or uniquing.");
            Program.Require(LlvmConstantVector.Get(Enumerable.Repeat<LlvmConstant>(LlvmConstantInt.Get(integer, 0), 4).ToArray()) is LlvmConstantAggregateZero,
                "Zero vector did not canonicalize to aggregate zero.");
            Program.Require(LlvmConstantVector.Get(Enumerable.Repeat<LlvmConstant>(LlvmPoisonValue.Get(integer), 4).ToArray()) is LlvmPoisonValue,
                "Poison vector did not canonicalize to poison.");
            var scalable = LlvmConstantVector.GetSplat(LlvmElementCount.Scalable(4), values[1]);
            Program.Require(scalable is LlvmConstantExpr { Opcode: LlvmOpcode.ShuffleVector }
                && ReferenceEquals(scalable.GetSplatValue(), values[1]), "Scalable splat did not retain its source insert/shuffle constant-expression form.");
            context.UseConstantIntForFixedLengthSplat = true;
            var native = LlvmConstantVector.GetSplat(LlvmElementCount.Fixed(4), values[1]);
            Program.Require(native is LlvmConstantInt { Type: LlvmFixedVectorType } && native.GetAggregateElement(3) == values[1],
                "Explicit upstream native integer splat option was not represented.");
            var half = LlvmType.GetHalfTy(context);
            var halfValue = LlvmConstantFP.Get(half, 1.5);
            var floating = LlvmConstantVector.GetSplat(LlvmElementCount.Fixed(4), halfValue);
            Program.Require(floating is LlvmConstantFP { Type: LlvmFixedVectorType }
                && ReferenceEquals(floating.GetAggregateElement(2), halfValue), "Floating splat did not preserve its exact lane semantics.");
            var byteValue = LlvmConstantByte.Get(LlvmByteType.Get(context, 8), 9);
            var bytes = LlvmConstantVector.GetSplat(LlvmElementCount.Scalable(4), byteValue);
            Program.Require(bytes is LlvmConstantByte { Type: LlvmScalableVectorType } && bytes.GetSplatValue() == byteValue,
                "Byte splat was converted into an integer or Range representation.");
            var globalA = new LlvmGlobalVariable(integer, false, LlvmLinkage.InternalLinkage, values[0]);
            var globalB = new LlvmGlobalVariable(integer, false, LlvmLinkage.InternalLinkage, values[0]);
            var original = LlvmConstantVector.Get([globalA, globalB]);
            var expected = LlvmConstantVector.Get([globalB, globalB]);
            var holder = new LlvmGlobalVariable(original.Type, true, LlvmLinkage.InternalLinkage, original);
            globalA.ReplaceAllUsesWith(globalB);
            Program.Require(ReferenceEquals(holder.Initializer, expected), "Vector constant RAUW did not merge a canonical collision.");
            globalB.ReplaceAllUsesWith(LlvmConstantPointerNull.Get((LlvmPointerType)globalB.Type));
            Program.Require(holder.Initializer is LlvmConstantPointerNull { Type: LlvmFixedVectorType },
                "Vector constant RAUW did not canonicalize its null-pointer splat.");
            checks += 9;
        }

        void Masks()
        {
            var context = new LlvmContext();
            var integer = LlvmIntegerType.Get(context, 32);
            for (int count = 1; count <= 3; ++count)
            {
                var type = LlvmFixedVectorType.Get(integer, (uint)count);
                var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, type]), LlvmLinkage.InternalLinkage);
                int combinations = (int)Math.Pow(count * 2 + 1, count);
                for (int ordinal = 0; ordinal < combinations; ++ordinal)
                {
                    int digits = ordinal;
                    var mask = new int[count];
                    for (int lane = 0; lane < count; ++lane) { mask[lane] = digits % (count * 2 + 1) - 1; digits /= count * 2 + 1; }
                    var shuffle = new LlvmShuffleVectorInstruction(function.Arguments[0], function.Arguments[1], mask);
                    shuffle.GetOperandUse(0).LogicalType = 11;
                    shuffle.GetOperandUse(1).LogicalType = 12;
                    shuffle.Commute();
                    for (int lane = 0; lane < count; ++lane)
                    {
                        int transformed = shuffle.GetMaskValue(lane);
                        Program.Require(mask[lane] == -1 ? transformed == -1
                            : transformed == (mask[lane] < count ? mask[lane] + count : mask[lane] - count), "Commute changed a shuffle lane's source value.");
                    }
                    Program.Require(shuffle.GetOperandUse(0).LogicalType == 12 && shuffle.GetOperandUse(1).LogicalType == 11,
                        "Shuffle commute did not swap operand roles' ABI annotations.");
                    shuffle.Commute();
                    Program.Require(shuffle.ShuffleMask.SequenceEqual(mask) && shuffle.Left == function.Arguments[0], "Shuffle double commute did not restore its original source form.");
                    shuffle.DropAllReferences();
                    ++checks;
                }
            }
            Program.Require(LlvmShuffleVectorInstruction.IsTransposeMask([0, 4, 2, 6], 4)
                && !LlvmShuffleVectorInstruction.IsTransposeMask([0, 4, -1, 6], 4), "Transpose mask matching changed source poison treatment.");
            Program.Require(LlvmShuffleVectorInstruction.IsExtractSubvectorMask([5, 6], 4, out int extract) && extract == 1,
                "Extract-subvector mask did not report the source-relative index.");
            Program.Require(LlvmShuffleVectorInstruction.IsInsertSubvectorMask([0, 1, 4, 5], 4, out int inserted, out int start)
                && inserted == 2 && start == 2, "Insert-subvector mask did not identify the source insertion span.");
            Program.Require(LlvmShuffleVectorInstruction.IsReplicationMask([0, 0, -1, 1, 2, 2], out int replication, out int width)
                && replication == 2 && width == 3, "Replication mask lost source larger-factor preference.");
            Program.Require(LlvmShuffleVectorInstruction.IsInterleaveMask([4, -1, 2, 5, 1, 3], 3, 6, out var starts)
                && starts.SequenceEqual(new uint[] { 4, 0, 2 }), "Interleave mask did not reconstruct poison-covered starts.");
            Program.Require(LlvmShuffleVectorInstruction.IsDeInterleaveMaskOfFactor([1, 3, 5, 7], 2, out uint offset) && offset == 1,
                "Deinterleave mask did not identify its lane offset.");
            Program.Require(LlvmShuffleVectorInstruction.IsBitRotateMask([1, 0, 3, 2, 5, 4, 7, 6], 8, 2, 8, out uint sub, out uint rotation)
                && sub == 2 && rotation == 8, "Bit-rotation mask did not retain the source subgroup size/rotation.");
            Program.Require(!LlvmShuffleVectorInstruction.IsSingleSourceMask([-1, -1], 2)
                && LlvmShuffleVectorInstruction.IsSelectMask([-1, -1], 2), "All-poison mask classification no longer matches the source rules.");
            checks += 8;
        }

        void Builder()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("vector-builder", context);
            var integer = LlvmIntegerType.Get(context, 32);
            var type = LlvmFixedVectorType.Get(integer, 4);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [integer]), LlvmLinkage.InternalLinkage, "splat", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var metrics = new Dictionary<string, ulong>();
            var builder = new LlvmTransformBuilder(entry, metrics);
            var splat = builder.CreateVectorSplat(4, function.Arguments[0]);
            Program.Require(splat is LlvmShuffleVectorInstruction { Left: LlvmInsertElementInstruction },
                "Nonconstant vector splat did not lower to source insert/shuffle instructions.");
            var constant = builder.CreateVectorSplat(4, LlvmConstantInt.Get(integer, 7));
            Program.Require(constant is LlvmConstantDataVector, "Constant splat did not use the source vector constant folder.");
            var vectorConstant = (LlvmConstant)constant;
            Program.Require(builder.CreateExtractElement(vectorConstant, 2UL) is LlvmConstantInt { Value: var lane } && lane == new LlvmAPInt(32, 7),
                "Vector extract folder did not return its exact scalar lane.");
            var noFolder = new LlvmTransformBuilder(entry, foldConstants: false);
            Program.Require(noFolder.CreateExtractElement(vectorConstant, 2UL) is LlvmExtractElementInstruction,
                "NoFolder vector operation was silently folded.");
            var target = new LlvmTransformBuilder(entry, targetFolder: new LlvmTargetFolder(module.DataLayout, metrics));
            Program.Require(target.CreateExtractElement(vectorConstant, 99UL) is LlvmPoisonValue,
                "TargetFolder did not preserve out-of-range extract poison.");
            builder.CreateRet(splat);
            Program.Require(metrics.GetValueOrDefault("transform_builder.shufflevector.queries") != 0
                && metrics.GetValueOrDefault("transform_builder.constant_folded") != 0, "Vector builder work was not observed.");
            checks += 6;
        }
    }
}
