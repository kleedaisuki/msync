using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class InstCombineSharedArithmeticProperties
{
    internal static int Run()
    {
        FoldUnsignedCasts(LlvmOpcode.FAdd, LlvmOpcode.UIToFP);
        FoldUnsignedCasts(LlvmOpcode.FSub, LlvmOpcode.SIToFP);
        PreserveSignedMultiplyZero();
        CancelCommonAdd();
        return 4;
    }

    private static void FoldUnsignedCasts(LlvmOpcode opcode, LlvmOpcode resultCast)
    {
        var (module, function, block) = Create("cast-arithmetic", floating: true);
        var i32 = LlvmType.GetInt32Ty(module.Context);
        var fp = function.ReturnType;
        var mask = LlvmConstantInt.Get(i32, 255);
        var a = Append(block, new LlvmBinaryOperator(LlvmOpcode.And, function.Arguments[0], mask));
        var b = Append(block, new LlvmBinaryOperator(LlvmOpcode.And, function.Arguments[1], mask));
        var x = Append(block, new LlvmCastInstruction(LlvmOpcode.UIToFP, a, fp));
        var y = Append(block, new LlvmCastInstruction(LlvmOpcode.UIToFP, b, fp));
        var operation = Append(block, new LlvmBinaryOperator(opcode, x, y));
        var result = Append(block, new LlvmReturnInstruction(module.Context, operation));
        Combine(function);
        Program.Require(result.GetOperand(0) is LlvmCastInstruction cast && cast.Opcode == resultCast &&
            !block.Instructions.OfType<LlvmBinaryOperator>().Any(i => i.Opcode == opcode),
            "Bounded integer-to-float arithmetic must fold through the shared precision/overflow source path, with signed subtraction output.");
    }

    private static void PreserveSignedMultiplyZero()
    {
        var (module, function, block) = Create("signed-multiply-zero", floating: true);
        var i8 = LlvmType.GetInt8Ty(module.Context);
        var a = Append(block, new LlvmCastInstruction(LlvmOpcode.Trunc, function.Arguments[0], i8));
        var b = Append(block, new LlvmCastInstruction(LlvmOpcode.Trunc, function.Arguments[1], i8));
        var x = Append(block, new LlvmCastInstruction(LlvmOpcode.SIToFP, a, function.ReturnType));
        var y = Append(block, new LlvmCastInstruction(LlvmOpcode.SIToFP, b, function.ReturnType));
        var operation = Append(block, new LlvmBinaryOperator(LlvmOpcode.FMul, x, y));
        var result = Append(block, new LlvmReturnInstruction(module.Context, operation));
        Combine(function);
        Program.Require(result.GetOperand(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul },
            "Signed multiply casts with possibly-zero operands must retain floating multiplication and its negative-zero result.");
    }

    private static void CancelCommonAdd()
    {
        var (module, function, block) = Create("common-add", floating: false);
        var x = function.Arguments[0]; var y = function.Arguments[1]; var z = function.Arguments[2];
        var a = Append(block, new LlvmBinaryOperator(LlvmOpcode.Add, x, y) { HasNoSignedWrap = true });
        var b = Append(block, new LlvmBinaryOperator(LlvmOpcode.Add, x, z) { HasNoSignedWrap = true });
        var builder = new LlvmTransformBuilder(block);
        var comparison = builder.CreateIntrinsic(LlvmIntrinsicKind.SCmp, [a, b], (LlvmIntegerType)function.ReturnType);
        var result = Append(block, new LlvmReturnInstruction(module.Context, comparison));
        Combine(function);
        Program.Require(result.GetOperand(0) is LlvmCallBase call &&
            ReferenceEquals(call.GetArgument(0), y) && ReferenceEquals(call.GetArgument(1), z),
            "The actual SCmp visitor must cancel the common nonwrapping add operand through the shared compare helper.");
    }

    private static (LlvmModule, LlvmFunction, LlvmBasicBlock) Create(string name, bool floating)
    {
        var context = new LlvmContext(); var module = new LlvmModule(name, context);
        var i32 = LlvmType.GetInt32Ty(context);
        LlvmType result = floating ? LlvmType.GetFloatTy(context) : i32;
        var function = LlvmFunction.Create(LlvmFunctionType.Get(result, [i32, i32, i32]), LlvmLinkage.InternalLinkage, name, module);
        return (module, function, new LlvmBasicBlock(context, "entry", function));
    }
    private static T Append<T>(LlvmBasicBlock block, T instruction) where T : LlvmInstruction
    { block.Append(instruction); return instruction; }
    private static void Combine(LlvmFunction function)
    {
        var tree = new LlvmDominatorTree(function);
        using var assumptions = new LlvmAssumptionCache(function);
        LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
            new LlvmTargetLibraryInfo(function.Parent!), new LlvmTargetTransformInfo(function.Parent!.DataLayout), tree);
    }
}
