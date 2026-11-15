using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class MinMaxIntrinsicChecks
{
    public static void Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("minmax-intrinsics", context);
        LlvmIntegerType i8 = LlvmType.GetInt8Ty(context), i64 = LlvmType.GetInt64Ty(context);
        LlvmFunction caller = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [i8, i8]),
            LlvmLinkage.InternalLinkage, "caller", module);
        LlvmValue a = caller.Arguments[0], b = caller.Arguments[1];
        LlvmIntrinsicKind[] kinds = [LlvmIntrinsicKind.SMin, LlvmIntrinsicKind.SMax, LlvmIntrinsicKind.UMin, LlvmIntrinsicKind.UMax];
        ulong[] expected = [255, 1, 1, 255];
        Dictionary<string, ulong> metrics = [];
        LlvmConstantInt negative = LlvmConstantInt.Get(i8, 255), one = LlvmConstantInt.Get(i8, 1);
        for (int i = 0; i < kinds.Length; ++i)
        {
            LlvmIntrinsicKind kind = kinds[i];
            LlvmFunction declaration = LlvmIntrinsic.GetDeclaration(module, kind, i8);
            Require(declaration.Name == kind.Name + ".i8", "LLVM overloaded declaration spelling");
            Require(declaration == LlvmIntrinsic.GetDeclaration(module, kind, i8), "module declaration uniquing");
            Require(declaration != LlvmIntrinsic.GetDeclaration(module, kind, i64), "separate integer overloads");
            LlvmCallInstruction call = LlvmIntrinsic.CreateBinary(kind, negative, one, module);
            Require(call.Parent is null && call.Opcode == LlvmOpcode.Call, "default builder creates an uninserted normal call");
            Require(call.CalledFunction == declaration && call.Type == i8, "call declaration and exact type");
            Require(!call.MayReadFromMemory() && !call.MayWriteToMemory() && !call.MayThrow() && call.WillReturn() && !call.MayHaveSideEffects(),
                "source intrinsic attributes affect instruction effects");
            RequireBits(LlvmConstantFolding.ConstantFoldInstruction(call, module.DataLayout, metrics), expected[i], "signed/unsigned constant fold");
            RequireBits(LlvmInstructionSimplify.SimplifyIntrinsic(kind, negative, one, module.DataLayout, metrics: metrics), expected[i], "simplify constant fold");
            Require(LlvmInstructionSimplify.SimplifyIntrinsic(kind, a, a, module.DataLayout) == a, "equal operands");
            Require(LlvmInstructionSimplify.SimplifyIntrinsic(kind, a, LlvmPoisonValue.Get(i8), module.DataLayout) is LlvmPoisonValue, "poison propagation");
            RequireBits(LlvmInstructionSimplify.SimplifyIntrinsic(kind, a, LlvmUndefValue.Get(i8), module.DataLayout),
                LlvmIntrinsic.GetSaturationPoint(kind, 8).GetZExtValue(), "undef saturation");
            LlvmConstantInt saturation = LlvmConstantInt.Get(context, LlvmIntrinsic.GetSaturationPoint(kind, 8));
            LlvmConstantInt identity = LlvmConstantInt.Get(context, LlvmIntrinsic.GetSaturationPoint(LlvmIntrinsic.GetInverseMinMax(kind), 8));
            Require(LlvmInstructionSimplify.SimplifyIntrinsic(kind, a, saturation, module.DataLayout) == saturation, "saturation operand");
            Require(LlvmInstructionSimplify.SimplifyIntrinsic(kind, a, identity, module.DataLayout) == a, "identity operand");
            LlvmCallInstruction inner = LlvmIntrinsic.CreateBinary(kind, a, b, module);
            Require(LlvmInstructionSimplify.SimplifyIntrinsic(kind, inner, a, module.DataLayout) == inner, "shared operand absorption");
            Require(LlvmInstructionSimplify.SimplifyIntrinsic(LlvmIntrinsic.GetInverseMinMax(kind), inner, a, module.DataLayout) == a,
                "inverse shared operand absorption");
        }
        LlvmFunction smin = LlvmIntrinsic.GetDeclaration(module, LlvmIntrinsicKind.SMin, i8);
        smin.SetName("not-an-intrinsic");
        Require(smin.IntrinsicDescriptor is null, "renaming clears semantic descriptor cache");
        smin.SetName("llvm.smin.i8");
        Require(smin.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.SMin, "canonical reserved name restores registry identity");
        LlvmFunction ordinary = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8, i8]), LlvmLinkage.ExternalLinkage, "smin", module);
        LlvmCallInstruction ordinaryCall = new(ordinary.FunctionType, ordinary, [a, b]);
        Require(ordinary.IntrinsicDescriptor is null && ordinaryCall.MayHaveSideEffects(), "ordinary names do not inherit intrinsic effects");
        LlvmCallInstruction deopt = new(smin.FunctionType, smin, [a, b], [new LlvmOperandBundleDef("deopt", [])]);
        Require(deopt.MayReadFromMemory() && !deopt.MayWriteToMemory(), "source deopt bundle read effects");
        LlvmCallInstruction unknownBundle = new(smin.FunctionType, smin, [a, b], [new LlvmOperandBundleDef("custom", [])]);
        Require(unknownBundle.MayReadFromMemory() && unknownBundle.MayWriteToMemory(), "source unknown bundle effects");
        Require(metrics.GetValueOrDefault("constant_fold.intrinsic.minmax") != 0, "constant-fold decision telemetry");
    }

    private static void RequireBits(LlvmValue? value, ulong expected, string cause) =>
        Require(value is LlvmConstantInt integer && integer.Value.GetZExtValue() == expected, cause);
    private static void Require(bool condition, string cause)
    {
        if (!condition) throw new InvalidOperationException($"Min/max intrinsic: {cause}");
    }
}
