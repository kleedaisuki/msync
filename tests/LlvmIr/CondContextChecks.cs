using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CondContextChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Require(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            ++checks;
        }
        LlvmContext context = new();
        LlvmModule module = new("injected-condition", context);
        var byteType = LlvmIntegerType.Get(context, 8);
        var fpType = LlvmType.GetDoubleTy(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(byteType, [byteType, byteType, fpType]),
            LlvmLinkage.InternalLinkage, "injected-condition", module);
        var x = function.Arguments[0];
        var y = function.Arguments[1];
        var fp = function.Arguments[2];
        var five = LlvmConstantInt.Get(byteType, 5);
        var condition = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, x, five);
        var injected = new LlvmCondContext(condition);
        injected.AffectedValues.Add(x);
        Dictionary<string, ulong> metrics = [];
        var known = LlvmKnownBitsAnalysis.Compute(x, metrics: metrics, condContext: injected);
        Require(known.IsConstant && known.One == five.Value,
            "Injected condition must refine an affected value even without a context instruction.");
        Require(metrics.GetValueOrDefault("known_bits.injected_condition.applied") == 1,
            "Telemetry must record actual injected-condition consumption.");
        Require(LlvmKnownBitsAnalysis.Compute(y, condContext: injected).IsUnknown,
            "Only explicitly affected values consume the injected condition.");
        Require(LlvmKnownBitsAnalysis.Compute(x).IsUnknown,
            "An independent query must not inherit a prior injected context.");
        var addition = new LlvmBinaryOperator(LlvmOpcode.Add, x, LlvmConstantInt.Get(byteType, 1));
        var sum = LlvmKnownBitsAnalysis.Compute(addition, condContext: injected);
        Require(sum.IsConstant && sum.One == new LlvmAPInt(8, 6),
            "Ordinary expression recursion must retain the injected query.");
        Require(LlvmKnownBitsAnalysis.MaskedValueIsZero(x, new LlvmAPInt(8, 2), condContext: injected),
            "KnownBits public fact wrappers must not drop the condition.");
        Require(LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpSge, x, LlvmConstantInt.Get(byteType, 0),
            module.DataLayout, condContext: injected) is LlvmConstantInt { IsOne: true },
            "The actual SimplifyQuery consumer must forward the condition to KnownBits.");
        Require(LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpSge, x, LlvmConstantInt.Get(byteType, 0),
            module.DataLayout) is null, "Without the injected query, the argument remains unknown.");
        injected.Invert = true;
        Require(LlvmKnownBitsAnalysis.Compute(x, condContext: injected).IsUnknown,
            "Inverting equality must not retain its equal-value bits.");
        var signCondition = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, x, LlvmConstantInt.Get(byteType, 0));
        var sign = new LlvmCondContext(signCondition);
        sign.AffectedValues.Add(x);
        Require(LlvmKnownBitsAnalysis.Compute(x, condContext: sign).IsNegative,
            "The injected true condition must set the sign fact.");
        sign.Invert = true;
        Require(LlvmKnownBitsAnalysis.Compute(x, condContext: sign).IsNonNegative,
            "The inverted condition must select the opposite signed region.");

        var left = new LlvmBasicBlock(context, "left", function);
        var right = new LlvmBasicBlock(context, "right", function);
        var join = new LlvmBasicBlock(context, "join", function);
        left.Append(new LlvmUncondBrInstruction(join));
        right.Append(new LlvmUncondBrInstruction(join));
        var phi = new LlvmPhiInstruction(byteType, 2);
        phi.AddIncoming(x, left); phi.AddIncoming(x, right);
        join.Append(phi); join.Append(new LlvmReturnInstruction(context, phi));
        injected.Invert = false;
        metrics.Clear();
        Require(LlvmKnownBitsAnalysis.Compute(phi, metrics: metrics, condContext: injected).IsUnknown,
            "PHI incoming values must be queried before the injected condition, with CC cleared.");
        Require(metrics.GetValueOrDefault("known_bits.injected_condition.applied") == 0,
            "PHI incoming recursion must not accidentally apply an affected incoming value's condition.");

        var positive = new LlvmCondContext(new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOgt, fp,
            LlvmConstantFP.Get(context, new LlvmAPFloat(0))));
        positive.AffectedValues.Add(fp);
        Require(LlvmKnownFPClasses.Compute(fp, condContext: positive)
            .IsKnownNever(LlvmFPClassTest.Negative | LlvmFPClassTest.Zero | LlvmFPClassTest.NaN),
            "FP context injection must occur before the missing-context-instruction return too.");
        var cast = new LlvmCastInstruction(LlvmOpcode.FPToSI, fp, byteType);
        Require(LlvmKnownBitsAnalysis.Compute(cast, condContext: positive).IsNonNegative,
            "KnownBits to FP-class recursion must preserve the same query.");
        var fpPhi = new LlvmPhiInstruction(fpType, 2);
        fpPhi.AddIncoming(fp, left); fpPhi.AddIncoming(fp, right);
        join.InsertBefore(fpPhi, phi);
        Require(LlvmKnownFPClasses.Compute(fpPhi, condContext: positive).IsUnknown,
            "FP PHI incoming recursion must clear the injected condition.");
        return checks;
    }
}
