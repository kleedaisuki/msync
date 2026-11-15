using Msync.Core;
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class TransformBuilderProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("transform-builder", context);
        var i64 = LlvmType.GetInt64Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i64, [i64, i64]), LlvmLinkage.InternalLinkage, "build", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        SourceSpan source = new("builder.msync", 4, 7);
        var ret = new LlvmReturnInstruction(context, function.Arguments[0]) { Source = source };
        block.Append(ret);
        var metrics = new Dictionary<string, ulong>();
        int inserted = 0, checks = 0;
        var builder = new LlvmTransformBuilder(ret, metrics, instruction =>
        {
            ++inserted;
            foreach (var operand in instruction.OperandUses) operand.LogicalType = 42;
        });
        var one = LlvmConstantInt.Get(i64, 1); var two = LlvmConstantInt.Get(i64, 2);
        var three = builder.CreateAdd(one, two, "constant-name");
        Require(three is LlvmConstantInt { Value.IsZero: false } c && c.GetZExtValue() == 3
            && inserted == 0 && three.LogicalType == -1 && !three.HasName, "ConstantFolder insertion changed a constant or emitted an instruction.");
        var identity = builder.CreateAdd(function.Arguments[0], LlvmConstantInt.Get(i64, 0), "identity");
        Require(identity is LlvmBinaryOperator && !ReferenceEquals(identity, function.Arguments[0]), "TransformBuilder substituted InstructionSimplify for ConstantFolder.");
        Require(identity is LlvmInstruction { Source: { } span } && span == source
            && ReferenceEquals(((LlvmInstruction)identity).Next, ret) && ((LlvmInstruction)identity).GetOperandUse(1).LogicalType == 42
            && LlvmConstantInt.Get(i64, 0).LogicalType == -1, "Insertion lost source/order/per-use annotations or mutated a canonical constant.");
        Require(builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, one, two) is LlvmConstantInt { Value.IsOne: true }, "ICmp constants did not use the constant folder.");
        var f64 = LlvmType.GetDoubleTy(context);
        Require(builder.CreateFCmp(LlvmCmpPredicate.FCmpOlt, LlvmConstantFP.Get(f64, 1), LlvmConstantFP.Get(f64, 2)) is LlvmConstantInt { Value.IsOne: true }, "FCmp constants did not fold.");
        Require(builder.CreateSelect(LlvmConstantInt.GetTrue(context), function.Arguments[0], function.Arguments[1]) is LlvmSelectInstruction,
            "ConstantFolder must not fold a select unless all three operands are constants.");
        Require(builder.CreateNot(LlvmConstantInt.GetFalse(context)) is LlvmConstantInt { Value.IsOne: true }, "CreateNot did not use source XOR/all-ones folding.");
        Require(ReferenceEquals(builder.CreateCast(LlvmOpcode.BitCast, function.Arguments[0], i64, "ignored"), function.Arguments[0]), "An identity cast emitted or renamed its operand.");
        var aggregateType = LlvmStructType.Get(context, [i64, i64]);
        var aggregate = LlvmConstantStruct.Get(aggregateType, [one, two]);
        Require(ReferenceEquals(builder.CreateExtractValue(aggregate, 1), two)
            && builder.CreateInsertValue(aggregate, one, 1) is LlvmConstant, "Aggregate insert/extract did not use source folders.");
        var global = new LlvmGlobalVariable(module, i64, true, LlvmLinkage.InternalLinkage, one, "data");
        var load = builder.CreateLoad(i64, global, "load");
        Require(ReferenceEquals(load.Parent, block) && load.Alignment == module.DataLayout.GetABITypeAlign(i64), "CreateLoad folded a load or lost default ABI alignment.");
        var alloca = builder.Insert(new LlvmAllocaInstruction(aggregateType, one));
        var gep = builder.CreateGep(aggregateType, alloca, [LlvmConstantInt.Get(i64, 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1)], noWrapFlags: LlvmGepNoWrapFlags.InBounds);
        Require(gep is LlvmGetElementPtrInstruction { SourceElementType: LlvmIntegerType { BitWidth: 8 }, NoWrapFlags.IsInBounds: true } address
            && address.GetIndex(0) is LlvmConstantInt offset && offset.GetZExtValue() == 8, "Default CreateGEP did not perform source byte-offset canonicalization.");
        builder.DisableGEPCanonicalization();
        Require(builder.CreateGep(aggregateType, alloca, [LlvmConstantInt.Get(i64, 0), LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1)]) is LlvmGetElementPtrInstruction { SourceElementType: LlvmStructType },
            "The source GEP canonicalization switch did not preserve a structured GEP.");
        var point = builder.SaveInsertionPoint(); builder.ClearInsertionPoint();
        var detached = builder.CreateMul(function.Arguments[0], function.Arguments[1]);
        Require(detached is LlvmInstruction { Parent: null }, "A cleared insertion point inserted into the previous block.");
        builder.RestoreInsertionPoint(point);
        Require(builder.CreateFreeze(one).Parent == block, "RestoreInsertionPoint lost the block/before anchor.");
        var noFolder = new LlvmTransformBuilder(ret, foldConstants: false);
        Require(noFolder.CreateAdd(one, two) is LlvmBinaryOperator && ReferenceEquals(noFolder.CreateBitCast(one, i64), one),
            "The source NoFolder strategy folded constants or lost builder-level identity casts.");
        var tail = new LlvmBasicBlock(context, "tail", function);
        ret.RemoveFromParent(); tail.Append(ret); block.Append(new LlvmUncondBrInstruction(tail));
        var relocated = builder.CreateSub(function.Arguments[0], function.Arguments[1]);
        Require(relocated is LlvmInstruction { Parent: { } parent } && ReferenceEquals(parent, tail)
            && ReferenceEquals(builder.InsertBlock, tail) && ReferenceEquals(((LlvmInstruction)relocated).Next, ret),
            "An instruction insertion point failed to follow its anchor across a block split.");
        Require(metrics.GetValueOrDefault("transform_builder.constant_folded") >= 6 && inserted > 0, "Builder decisions lost their folding/insertion telemetry.");
        return checks;
        void Require(bool result, string message) { Program.Require(result, message); ++checks; }
    }
}
