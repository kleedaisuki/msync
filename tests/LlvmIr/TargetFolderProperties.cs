using System.Numerics;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class TargetFolderProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        LlvmModule module = new("target-folder", context);
        var integer = LlvmType.GetInt32Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer]), LlvmLinkage.InternalLinkage, "main", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        var ret = new LlvmReturnInstruction(context, function.Arguments[0]);
        entry.Append(ret); exit.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(integer, 0)));
        Dictionary<string, ulong> work = [];
        var folder = new LlvmTargetFolder(module.DataLayout, work);
        List<LlvmInstruction> inserted = [];
        var builder = new LlvmTransformBuilder(ret, work, inserted.Add, targetFolder: folder);
        var three = LlvmConstantInt.Get(integer, 3);
        var five = LlvmConstantInt.Get(integer, 5);
        Require(builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMin, three, five) is LlvmConstantInt { Value: var min } && min.GetZExtValue() == 3 && inserted.Count == 0,
            "TargetFolder folds a real intrinsic result without inserting a dead call or notifying the inserter.");
        Require(builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [LlvmConstantInt.Get(integer, 0b101101)]) is LlvmConstantInt { Value: var pop } && pop.GetZExtValue() == 4,
            "The generic intrinsic builder must return the actual TargetFolder constant result.");
        var defaultBuilder = new LlvmTransformBuilder(ret);
        Require(defaultBuilder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMin, three, five) is LlvmCallInstruction,
            "Default ConstantFolder retains its source no-intrinsic-folding behavior.");
        var noFolder = new LlvmTransformBuilder(ret, foldConstants: false);
        Require(noFolder.CreateAdd(three, five) is LlvmBinaryOperator,
            "The existing NoFolder constructor mode must still emit constant-operand arithmetic.");
        Require(builder.CreateAdd(three, five) is LlvmConstantInt { Value: var sum } && sum.GetZExtValue() == 8,
            "TargetFolder's desirable-binop source factory must fold literal integer addition.");
        var nonconstant = builder.CreateAdd(function.Arguments[0], five);
        Require(nonconstant is LlvmBinaryOperator && inserted.Count == 1 && ReferenceEquals(nonconstant, inserted[0]),
            "Only a newly inserted non-folded instruction reaches the supplied insertion callback.");
        var smallestDenormal = LlvmConstantFP.Get(LlvmType.GetDoubleTy(context), LlvmAPFloat.FromBits(1));
        Require(folder.FoldCmp(LlvmCmpPredicate.FCmpOeq, smallestDenormal, smallestDenormal) is null &&
            defaultBuilder.CreateFCmp(LlvmCmpPredicate.FCmpOeq, smallestDenormal, smallestDenormal) is LlvmConstantInt,
            "TargetFolder uses the source DataLayout-aware comparison folder, including its absent-context denormal rule, rather than aliasing ConstantFolder.");

        SourceSpan? source = new SourceSpan("target-folder.msync", 4, 3);
        builder.CurrentSource = source;
        using (new LlvmTransformBuilder.InsertPointGuard(builder))
        {
            builder.SetInsertPoint(exit.GetTerminator());
            builder.CurrentSource = new SourceSpan("nested.msync", 1, 1);
        }
        Require(ReferenceEquals(builder.InsertBefore, ret) && builder.CurrentSource == source,
            "InsertPointGuard restores both the source insertion point and source-location annotation.");
        BitFields();
        Require(work.GetValueOrDefault("target_folder.intrinsic.folded") == 2 && work.GetValueOrDefault("target_folder.compare.not_folded") == 1,
            "TargetFolder reports actual family decisions in the caller-owned sink-neutral metrics.");
        IntrinsicDispatch();
        return checks;

        void Require(bool condition, string message)
        {
            Program.Require(condition, message);
            ++checks;
        }

        void IntrinsicDispatch()
        {
            var floating = LlvmType.GetFloatTy(context);
            var negativeZero = LlvmConstantFP.Get(floating, LlvmAPFloat.GetZero(floating.GetFltSemantics(), true));
            Require(folder.FoldIntrinsic(LlvmIntrinsicKind.FAbs, [negativeZero], floating) is LlvmConstantFP { Value.IsZero: true, Value.IsNegative: false },
                "The intrinsic folder preserves the operand format and clears the negative-zero sign.");
            Require(folder.FoldIntrinsic(LlvmIntrinsicKind.FMA, [LlvmPoisonValue.Get(floating), negativeZero, negativeZero], floating) is null,
                "The source absent-caller strictfp gate precedes even poison propagation.");
            Require(folder.FoldIntrinsic(LlvmIntrinsicKind.Tan, [negativeZero], floating, function: function) is null,
                "The pinned source does not include llvm.tan in canConstantFoldIntrinsic; its registry identity is not permission to fold it.");
            var one = LlvmConstantFP.Get(floating, LlvmAPFloat.GetOne(floating.GetFltSemantics()));
            Require(folder.FoldIntrinsic(LlvmIntrinsicKind.PowI, [one, LlvmConstantInt.GetSigned(integer, -7)], floating, function: function) is LlvmConstantFP { Value: var power } && power.BitwiseIsEqual(one.Value),
                "powi uses square-and-multiply in the operand's own semantics and a reciprocal for negative exponents.");
            var signedSum = LlvmStructType.Get(context, [integer, LlvmType.GetInt1Ty(context)]);
            var overflow = folder.FoldIntrinsic(LlvmIntrinsicKind.SAddWithOverflow,
                [LlvmConstantInt.Get(context, LlvmAPInt.GetSignedMaxValue(32)), LlvmConstantInt.Get(integer, 1)], signedSum);
            Require(overflow?.GetAggregateElement(0) is LlvmConstantInt { Value.IsMinSignedValue: true } &&
                overflow.GetAggregateElement(1) is LlvmConstantInt { IsOne: true },
                "Integer overflow folding returns the actual {iN,i1} aggregate, not a function descriptor or Number.");
            var vector = LlvmFixedVectorType.Get(integer, 2);
            var source = LlvmConstantVector.Get([LlvmConstantInt.Get(integer, 0b1011), LlvmConstantInt.Get(integer, 0b1000)]);
            var populations = folder.FoldIntrinsic(LlvmIntrinsicKind.CTPop, [source], vector);
            Require(populations?.GetAggregateElement(0) is LlvmConstantInt { Value: var first } && first.GetZExtValue() == 3 &&
                populations.GetAggregateElement(1) is LlvmConstantInt { IsOne: true },
                "Fixed-vector intrinsic folding runs the source scalar fold independently on each lane.");
            Require(folder.FoldIntrinsic(LlvmIntrinsicKind.Abs, [LlvmUndefValue.Get(integer), LlvmConstantInt.GetTrue(context)], integer) is LlvmPoisonValue,
                "abs(undef,true) preserves the source poison-minimum result.");
            Require(folder.FoldIntrinsic(LlvmIntrinsicKind.FShr, [three, five, LlvmUndefValue.Get(integer)], integer) == five,
                "An undef right funnel-shift amount selects the second input, not the left-shift input.");
        }

        void BitFields()
        {
            uint[] widths = [8, 63, 64, 65, 96, 128, 192];
            foreach (uint width in widths)
            {
                var original = LlvmAPInt.GetAllOnes(width);
                foreach (uint count in new uint[] { 1, Math.Min(17, width), Math.Min(64, width), width })
                {
                    uint[] positions = [0, (width - count) / 2, width - count];
                    foreach (uint position in positions)
                    {
                        var input = new LlvmAPInt(count, 0x5a5a5a5a5a5a5a5aUL, implicitTrunc: true);
                        LlvmAPInt updated = original.InsertBits(input, position);
                        BigInteger mask = (BigInteger.One << (int)count) - 1;
                        BigInteger expected = ((BigInteger.One << (int)width) - 1) & ~(mask << (int)position);
                        expected |= (new BigInteger(0x5a5a5a5a5a5a5a5aUL) & mask) << (int)position;
                        Require(ToBigInteger(updated) == expected && updated.ExtractBits(count, position) == input,
                            "Source APInt bit insertion/extraction must preserve surrounding bits across word boundaries.");
                    }
                }
            }
            var bytes = LlvmConstantByte.Get(context, new LlvmAPInt(64, 0x0123456789abcdef));
            var offset = LlvmConstantInt.Get(integer, 8);
            var i16 = LlvmType.GetInt16Ty(context);
            Require(folder.FoldBitExtract(i16, bytes, offset) is LlvmConstantInt { Value: var bits } && bits.GetZExtValue() == 0xabcd,
                "TargetFolder bitextract reads the source ConstantByte bit interval, not an endian-dependent byte reinterpretation.");
            Require(folder.FoldBitInsert(bytes, LlvmConstantInt.Get(i16, 0xffee), offset) is LlvmConstantByte { Value: var updatedBytes } &&
                updatedBytes.GetZExtValue() == 0x0123456789ffeeef,
                "TargetFolder bitinsert replaces only the selected ConstantByte interval.");
            Require(folder.FoldBitExtract(i16, bytes, LlvmConstantInt.Get(integer, 60)) is LlvmPoisonValue &&
                folder.FoldBitInsert(bytes, five, LlvmUndefValue.Get(integer)) is LlvmPoisonValue,
                "Bit-field source folders preserve out-of-range and undefined-offset poison results.");
        }
    }

    private static BigInteger ToBigInteger(LlvmAPInt value)
    {
        BigInteger result = BigInteger.Zero;
        for (int index = (int)value.NumWords - 1; index >= 0; --index)
            result = (result << 64) | value.GetWord((uint)index);
        return result;
    }
}
