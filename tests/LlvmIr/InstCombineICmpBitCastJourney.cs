using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineICmpBitCastJourney
{
    internal static long Run()
    {
        long checks = 0;
        foreach (var opcode in new[] { LlvmOpcode.SIToFP, LlvmOpcode.UIToFP })
            foreach (var predicate in new[] { LlvmCmpPredicate.ICmpEq, LlvmCmpPredicate.ICmpNe })
            {
                var context = new LlvmContext();
                var module = new LlvmModule("bitcast-compare", context);
                var inputType = LlvmType.GetIntNTy(context, 8);
                var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetInt1Ty(context), [inputType]),
                    LlvmLinkage.InternalLinkage, "zero", module);
                var block = new LlvmBasicBlock(context, "entry", function);
                var floating = new LlvmCastInstruction(opcode, function.Arguments[0], LlvmType.GetFloatTy(context));
                var bits = new LlvmCastInstruction(LlvmOpcode.BitCast, floating, LlvmType.GetInt32Ty(context));
                var compare = new LlvmICmpInstruction(predicate, bits, LlvmConstant.GetNullValue(bits.Type));
                var result = new LlvmReturnInstruction(context, compare);
                block.Append(floating); block.Append(bits); block.Append(compare); block.Append(result);
                using var assumptions = new LlvmAssumptionCache(function);
                var tree = new LlvmDominatorTree(function);
                LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
                    new LlvmTargetLibraryInfo(module), new LlvmTargetTransformInfo(module.DataLayout), tree);
                Program.Require(result.GetOperand(0) is LlvmICmpInstruction folded && folded.Predicate == predicate &&
                    folded.Left == function.Arguments[0] && folded.Right is LlvmConstant { IsNullValue: true },
                    "Int-to-FP bitcast zero comparison returns to the original integer operand");
                Program.Require(!block.Instructions.Any(instruction => instruction is LlvmCastInstruction),
                    "The worklist removes both obsolete conversions after the compare rewrite");
                checks += 2;
            }
        return checks;
    }
}
