using Msync.Core.Llvm;

internal static class InstructionEquivalenceChecks
{
    internal static void Run()
    {
        LlvmContext context = new();
        LlvmType integer = LlvmType.GetInt32Ty(context), fp = LlvmType.GetDoubleTy(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer, integer, fp, fp]), LlvmLinkage.InternalLinkage);
        var x = function.Arguments[0];
        var y = function.Arguments[1];
        LlvmBinaryOperator add = new(LlvmOpcode.Add, x, y) { HasNoSignedWrap = true, LogicalType = 11 };
        LlvmBinaryOperator plain = new(LlvmOpcode.Add, x, y) { LogicalType = 22 };
        add.GetOperandUse(0).LogicalType = 33;
        Program.Require(add.IsIdenticalToWhenDefined(plain) && add.HasPoisonGeneratingFlags(),
            "When-defined instruction identity must ignore poison flags and native ABI annotations.");
        add.AndIRFlags(plain);
        Program.Require(!add.HasNoSignedWrap && !add.HasPoisonGeneratingFlags(), "Flag intersection must drop unmatched wrap flags.");
        LlvmBinaryOperator divide = new(LlvmOpcode.UDiv, x, y) { IsExact = true };
        divide.AndIRFlags(new LlvmBinaryOperator(LlvmOpcode.SDiv, x, y));
        Program.Require(!divide.IsExact, "PossiblyExactOperator flag intersection is shared across supported exact opcodes.");
        LlvmICmpInstruction compare = new(LlvmCmpPredicate.ICmpEq, x, y) { SameSign = true };
        compare.AndIRFlags(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, x, y));
        Program.Require(!compare.SameSign && !compare.HasPoisonGeneratingFlags(), "ICmp same-sign must intersect independently of its predicate.");

        LlvmBinaryOperator floating = new(LlvmOpcode.FAdd, function.Arguments[2], function.Arguments[3])
        { FastMathFlags = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros };
        LlvmBinaryOperator otherFloating = new(LlvmOpcode.FAdd, function.Arguments[2], function.Arguments[3])
        { FastMathFlags = LlvmFastMathFlags.NoSignedZeros };
        Program.Require(floating.IsFloatingMathOperator && floating.HasPoisonGeneratingFlags()
            && floating.IsIdenticalToWhenDefined(otherFloating), "FP when-defined comparison must ignore fast-math flags.");
        floating.AndIRFlags(otherFloating);
        Program.Require(floating.FastMathFlags == LlvmFastMathFlags.NoSignedZeros && !floating.HasPoisonGeneratingFlags(),
            "FP flag intersection must retain only common flags; nsz alone is not poison generating upstream.");

        LlvmAllocaInstruction address = new(integer, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1));
        LlvmLoadInstruction load = new(integer, address, new LlvmAlign(4));
        LlvmLoadInstruction aligned = new(integer, address, new LlvmAlign(8));
        Program.Require(!load.IsIdenticalToWhenDefined(aligned) && load.HasSameSpecialState(aligned, ignoreAlignment: true),
            "Load special state must compare alignment unless the caller explicitly ignores it.");
        aligned.Alignment = load.Alignment;
        aligned.IsElementwise = true;
        Program.Require(!load.IsIdenticalToWhenDefined(aligned)
            && ((LlvmLoadInstruction)aligned.Clone()).IsElementwise, "Elementwise memory state must participate in identity and cloning.");

        LlvmBasicBlock first = new(context), second = new(context);
        LlvmPhiInstruction phi = new(integer), otherPhi = new(integer);
        phi.AddIncoming(x, first); otherPhi.AddIncoming(x, second);
        Program.Require(!phi.IsIdenticalToWhenDefined(otherPhi), "PHI identity includes incoming block identity.");
        otherPhi.SetIncomingBlock(0, first);
        Program.Require(phi.IsIdenticalToWhenDefined(otherPhi), "Identical PHI incoming values and blocks must match.");
        Calls(context);
    }

    private static void Calls(LlvmContext context)
    {
        LlvmFunctionType type = LlvmFunctionType.Get(LlvmType.GetVoidTy(context), []);
        LlvmFunction target = LlvmFunction.Create(type, LlvmLinkage.ExternalLinkage);
        LlvmCallInstruction tail = new(type, target, []) { TailCallKind = LlvmTailCallKind.Tail };
        LlvmCallInstruction mustTail = new(type, target, []) { TailCallKind = LlvmTailCallKind.MustTail };
        Program.Require(tail.IsIdenticalToWhenDefined(mustTail), "Source when-defined call comparison uses isTailCall, not the exact tail kind.");
        LlvmCallInstruction a = new(type, target, [], [new("first", [])]);
        LlvmCallInstruction b = new(type, target, [], [new("second", [])]);
        Program.Require(!a.IsIdenticalToWhenDefined(b), "Call operand bundle tags belong to special instruction state.");
    }
}
