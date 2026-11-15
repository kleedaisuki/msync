using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class PhiTransAddrChecks
{
    public static void Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("phi-trans-address", context);
        LlvmIntegerType i32 = LlvmType.GetInt32Ty(context), i64 = LlvmType.GetInt64Ty(context);
        LlvmPointerType pointer = LlvmPointerType.Get(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [pointer, pointer, i64]),
            LlvmLinkage.InternalLinkage, "translate", module);
        LlvmValue a = function.Arguments[0], b = function.Arguments[1], n = function.Arguments[2];
        LlvmBasicBlock entry = new(context, "entry", function), left = new(context, "left", function),
            right = new(context, "right", function), join = new(context, "join", function), dead = new(context, "dead", function);
        LlvmConstantInt zero = LlvmConstantInt.Get(i64, 0), one = LlvmConstantInt.Get(i64, 1);
        LlvmConstantInt minusOne = LlvmConstantInt.Get(i64, ulong.MaxValue, isSigned: true);
        LlvmPhiInstruction phi = new(pointer), index = new(i64), plusIndex = new(i64);
        phi.AddIncoming(a, left); phi.AddIncoming(b, right);
        index.AddIncoming(zero, left); index.AddIncoming(one, right);
        LlvmBinaryOperator plus = new(LlvmOpcode.Add, n, one);
        left.Append(plus);
        plusIndex.AddIncoming(plus, left); plusIndex.AddIncoming(n, right);
        LlvmGetElementPtrInstruction availableGep = new(i32, b, [one]);
        right.Append(availableGep);
        LlvmCastInstruction availableCast = new(LlvmOpcode.PtrToInt, a, i64);
        left.Append(availableCast);
        LlvmBinaryOperator availableAdd = new(LlvmOpcode.Add, n, one);
        right.Append(availableAdd);
        join.Append(phi); join.Append(index); join.Append(plusIndex);
        LlvmGetElementPtrInstruction translatedGep = new(i32, phi, [index]);
        join.Append(translatedGep);
        LlvmCastInstruction translatedCast = new(LlvmOpcode.PtrToInt, phi, i64);
        join.Append(translatedCast);
        LlvmBinaryOperator combineAdd = new(LlvmOpcode.Add, plusIndex, minusOne);
        join.Append(combineAdd);
        LlvmBinaryOperator translatedAdd = new(LlvmOpcode.Add, plusIndex, one);
        join.Append(translatedAdd);
        LlvmBinaryOperator unsupported = new(LlvmOpcode.Mul, n, one);
        join.Append(unsupported);
        LlvmGetElementPtrInstruction unchanged = new(i32, a, [one]);
        join.Append(unchanged);
        entry.Append(new LlvmCondBrInstruction(LlvmUndefValue.Get(LlvmType.GetInt1Ty(context)), left, right));
        left.Append(new LlvmUncondBrInstruction(join)); right.Append(new LlvmUncondBrInstruction(join));
        join.Append(new LlvmReturnInstruction(context)); dead.Append(new LlvmReturnInstruction(context));
        LlvmDominatorTree tree = new(function);
        Dictionary<string, ulong> metrics = [];
        LlvmPhiTransAddr translator = new(phi, module.DataLayout, metrics);
        Require(translator.NeedsPHITranslationFromBlock(join) && translator.IsPotentiallyPHITranslatable(), "PHI input classification");
        Require(translator.TranslateValue(join, left, tree, true) == a, "direct PHI incoming value");
        Require(!translator.NeedsPHITranslationFromBlock(join), "translated arguments leave no instruction inputs");
        Require(Translate(translatedGep, left) == a, "translated zero GEP simplifies");
        Require(Translate(translatedGep, right) == availableGep, "existing dominating GEP reused");
        Require(Translate(translatedCast, left) == availableCast, "existing dominating cast reused");
        Require(Translate(combineAdd, left) == n, "translated add combines immediates then cancels");
        Require(Translate(translatedAdd, right) == availableAdd, "existing dominating add reused");
        Require(Translate(unchanged, left) is null, "unchanged local address cannot dominate predecessor");
        Require(!new LlvmPhiTransAddr(unsupported, module.DataLayout).IsPotentiallyPHITranslatable(), "nontranslatable opcode filter");
        Require(Translate(unsupported, left) is null, "nontranslatable input fails");
        Require(Translate(a, dead) is null, "unreachable predecessor follows source failure");
        Require(new LlvmPhiTransAddr(a, module.DataLayout).TranslateValue(join, left, null, false) is null,
            "missing tree follows source failure even for an argument");
        Require(metrics.GetValueOrDefault("phi_translate.add_combined_immediates") != 0 &&
            metrics.GetValueOrDefault("phi_translate.gep_available") != 0, "translation work/outcome telemetry");

        LlvmValue? Translate(LlvmValue address, LlvmBasicBlock predecessor) =>
            new LlvmPhiTransAddr(address, module.DataLayout, metrics).TranslateValue(join, predecessor, tree, true);
    }

    private static void Require(bool condition, string cause)
    {
        if (!condition) throw new InvalidOperationException($"PHI address translation: {cause}");
    }
}
