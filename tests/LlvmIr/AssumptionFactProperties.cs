using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class AssumptionFactProperties
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new(); LlvmModule module = new("assumption-facts", context);
        var i8 = LlvmType.GetInt8Ty(context); var i32 = LlvmType.GetInt32Ty(context); var i64 = LlvmType.GetInt64Ty(context);
        var pointer = LlvmType.GetPointerTy(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8, i8, pointer, pointer, i8, pointer, i8]),
            LlvmLinkage.InternalLinkage, "facts", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var x = function.Arguments[0]; var y = function.Arguments[1]; var p = function.Arguments[2]; var q = function.Arguments[3];
        var noUndef = function.Arguments[4]; var noUndefPointer = function.Arguments[5]; var power = function.Arguments[6];
        var zero = LlvmConstantInt.Get(i8, 0); var one = LlvmConstantInt.Get(i8, 1);
        var upper = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUle, x, LlvmConstantInt.Get(i8, 7));
        entry.Append(upper); entry.Append(LlvmIntrinsic.CreateAssumption(upper, module));
        var nonzero = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, x, zero);
        entry.Append(nonzero); entry.Append(LlvmIntrinsic.CreateAssumption(nonzero, module));
        var different = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, x, y);
        entry.Append(different); entry.Append(LlvmIntrinsic.CreateAssumption(different, module));
        var expression = new LlvmBinaryOperator(LlvmOpcode.Add, x, one); entry.Append(expression);
        entry.Append(LlvmIntrinsic.CreateAssumption(LlvmConstantInt.GetTrue(context), module,
            [new("range", [y, LlvmConstantInt.Get(i8, 4), LlvmConstantInt.Get(i8, 8)]),
             new("align", [p, LlvmConstantInt.Get(i64, 8), LlvmConstantInt.Get(i64, 3)]),
             new("align", [q, LlvmConstantInt.Get(i64, 4)]), new("dereferenceable", [q, LlvmConstantInt.Get(i64, 16)]),
             new("noundef", [noUndef]), new("noundef", [noUndefPointer])]));
        var pop = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [power], module); entry.Append(pop);
        var oneBit = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, pop, one); entry.Append(oneBit);
        entry.Append(LlvmIntrinsic.CreateAssumption(oneBit, module));
        var add = new LlvmBinaryOperator(LlvmOpcode.Add, x, power); entry.Append(add);
        var nonnegative = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSge, add, zero); entry.Append(nonnegative);
        entry.Append(LlvmIntrinsic.CreateAssumption(nonnegative, module));
        var load = new LlvmLoadInstruction(i32, q, new LlvmAlign(4)); entry.Append(load);
        var ret = new LlvmReturnInstruction(context, x); entry.Append(ret);
        var tree = new LlvmDominatorTree(function);
        using var cache = new LlvmAssumptionCache(function);
        Require(LlvmKnownBitsAnalysis.Compute(x, context: ret).IsUnknown, "Null AC preserves the old no-assumption query path.");
        Require(LlvmKnownBitsAnalysis.Compute(x, context: ret, assumptions: cache).CountMinLeadingZeros() == 5,
            "An actual assume comparison supplies its dynamic operand's high-zero facts.");
        Require(LlvmConstantRangeAnalysis.Compute(x, context: ret, assumptions: cache).GetUnsignedMax() == new LlvmAPInt(8, 7),
            "The source raw ConstantRange query independently intersects actual assume comparison ranges.");
        Require(LlvmConstantRangeAnalysis.Compute(y, context: ret, assumptions: cache).GetUnsignedMin() == new LlvmAPInt(8, 4),
            "The source raw ConstantRange query consumes the range bundle directly, not through a KnownBits substitute.");
        Require(LlvmKnownBitsAnalysis.Compute(expression, context: ret, assumptions: cache).CountMinLeadingZeros() == 4,
            "The real AC must propagate through recursive operand queries, not only refine the requested root.");
        Require(LlvmKnownBitsAnalysis.Compute(x, context: upper, assumptions: cache).IsUnknown &&
            LlvmKnownBitsAnalysis.Compute(x, context: upper, assumptions: cache, allowEphemerals: true).CountMinLeadingZeros() == 5,
            "The source ephemeral-context rule is distinct from explicitly allowed ephemeral queries.");
        Require(LlvmKnownBitsAnalysis.IsKnownNonZero(x, context: ret, assumptions: cache), "Assumed nonzero comparison is a source nonzero fact.");
        Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(x, y, context: ret, assumptions: cache), "The source non-equality query consumes actual assumed comparison conditions.");
        var range = LlvmKnownBitsAnalysis.Compute(y, context: ret, assumptions: cache);
        Require((range.One.GetZExtValue() & 4) != 0 && LlvmKnownBitsAnalysis.IsKnownNonZero(y, context: ret, assumptions: cache),
            "A real range bundle refines KnownBits and excludes zero.");
        var aligned = LlvmKnownBitsAnalysis.Compute(p, context: ret, assumptions: cache);
        Require((aligned.Zero.GetZExtValue() & 7) == 4 && (aligned.One.GetZExtValue() & 7) == 3 &&
            LlvmKnownBitsAnalysis.IsKnownNonZero(p, context: ret, assumptions: cache), "Offset alignment facts retain both low zero/one bits and source nonnull implication.");
        Require(!LlvmValueTracking.IsSafeToSpeculativelyExecute(load, ret) &&
            LlvmValueTracking.IsSafeToSpeculativelyExecute(load, ret, assumptions: cache), "Load speculation uses retained alignment and byte facts from the actual AC.");
        Require(!LlvmValueTracking.IsGuaranteedNotToBeUndef(noUndef, tree, ret) &&
            LlvmValueTracking.IsGuaranteedNotToBeUndef(noUndef, tree, ret, assumptions: cache) &&
            LlvmValueTracking.IsGuaranteedNotToBeUndef(noUndefPointer, tree, ret, assumptions: cache),
            "AC NoUndef knowledge applies to both integer and pointer values after the source context/DT checks.");
        Require(!LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(power, context: ret) &&
            LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(power, context: ret, assumptions: cache), "The source ctpop==1 assume pattern supplies a power-of-two fact.");
        Require(LlvmValueTracking.ComputeOverflowForSignedAdd(add, module.DataLayout, ret, assumptions: cache) == LlvmOverflowResult.NeverOverflows,
            "The signed AddOperator query uses context-only result sign facts from the AC.");
        Require(LlvmValueTracking.IsGuaranteedNotToBeUndef(nonzero, tree, ret) &&
            LlvmValueTracking.IsGuaranteedNotToBePoison(nonzero, tree, ret), "The actual assume intrinsic's noundef condition is consumed as a direct UB use.");
        CheckMayFreeCall();
        return checks;

        void CheckMayFreeCall()
        {
            var f = LlvmFunction.Create(LlvmFunctionType.Get(i32, [pointer]), LlvmLinkage.InternalLinkage, "call-may-free", module);
            var block = new LlvmBasicBlock(context, "entry", f); var value = f.Arguments[0];
            block.Append(LlvmIntrinsic.CreateAssumption(LlvmConstantInt.GetTrue(context), module,
                [new("align", [value, LlvmConstantInt.Get(i64, 4)]), new("dereferenceable", [value, LlvmConstantInt.Get(i64, 16)])]));
            var callee = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), []), LlvmLinkage.ExternalLinkage, "ordinary", module);
            block.Append(new LlvmCallInstruction(callee.FunctionType, callee, []));
            var candidate = new LlvmLoadInstruction(i32, value, new LlvmAlign(4)); block.Append(candidate);
            var result = new LlvmReturnInstruction(context, LlvmConstantInt.Get(i32, 0)); block.Append(result);
            using var ac = new LlvmAssumptionCache(f);
            Require(!LlvmValueTracking.IsSafeToSpeculativelyExecute(candidate, result, assumptions: ac),
                "The source retained byte fact does not cross a call without NoFree.");
        }
        void Require(bool condition, string message) { Program.Require(condition, message); checks++; }
    }
}
