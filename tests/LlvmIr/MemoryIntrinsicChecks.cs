using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class MemoryIntrinsicChecks
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        LlvmModule module = new("memory-intrinsics", context);
        var i8 = LlvmType.GetInt8Ty(context);
        var i32 = LlvmType.GetInt32Ty(context);
        var i64 = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i32, [pointer, pointer, i64]),
            LlvmLinkage.InternalLinkage, "memory", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        foreach (var kind in new[] { LlvmIntrinsicKind.MemCpy, LlvmIntrinsicKind.MemCpyInline,
            LlvmIntrinsicKind.MemMove, LlvmIntrinsicKind.MemSet, LlvmIntrinsicKind.MemSetInline })
        {
            bool set = kind == LlvmIntrinsicKind.MemSet || kind == LlvmIntrinsicKind.MemSetInline;
            var call = LlvmIntrinsic.CreateMemoryIntrinsic(kind,
                [function.Arguments[0], set ? LlvmConstantInt.Get(i8, 0) : function.Arguments[1],
                 function.Arguments[2], LlvmConstantInt.GetFalse(context)], module);
            entry.Append(call);
            Require(call.CalledFunction!.GetParamAttributes(3).Has(LlvmAttributeKind.ImmArg),
                "Source volatile argument has the actual immarg parameter attribute.");
            Require(LlvmMemIntrinsic.TryGet(call, out var memory), "Real CallBase memory facade.");
            Require(ReferenceEquals(memory.RawDestUse, call.GetOperandUse(0)) &&
                ReferenceEquals(memory.LengthUse, call.GetOperandUse(2)), "Facade preserves source Use identity.");
            memory.SetDestAlignment(new(8));
            Require(memory.DestAlign == new LlvmAlign(8), "Alignment is callsite argument alignment.");
            Require(call.GetMemoryEffects().Raw == (set ? 2U : 3U), "Exact source ArgMem effects.");
            Require(call.WillReturn(), "Nonvolatile memory intrinsic will return.");
            memory.SetVolatile(true);
            Require(!call.WillReturn() && call.GetMemoryEffects().Raw == (set ? 14U : 15U),
                "Volatile memory introduces inaccessible effects and does not promise return.");
            memory.SetVolatile(false);
            Require(LlvmMemoryLocation.GetForDest(memory).Size.MayBeBeforePointer ==
                (kind == LlvmIntrinsicKind.MemSetInline), "Source dynamic-length MemoryLocation distinction.");
            memory.SetLength(12);
            if (kind != LlvmIntrinsicKind.MemSetInline)
                Require(LlvmMemoryLocation.GetForDest(call)!.Value.Size.GetValue().GetFixedValue() == 12,
                    "Actual call argument effects locate sole written destination.");
            Require(call.CalledFunction!.GetParamAttributes(0).CaptureInfo == LlvmCaptureInfo.None(),
                "Destination source captures(none).");
            bool copy = kind == LlvmIntrinsicKind.MemCpy || kind == LlvmIntrinsicKind.MemCpyInline;
            Require(call.CalledFunction.GetParamAttributes(0).Has(LlvmAttributeKind.NoAlias) == copy,
                "Only memcpy variants have source noalias parameters.");
        }
        var nullPointer = LlvmConstantPointerNull.Get(pointer);
        var launder = LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.LaunderInvariantGroup, [nullPointer], module);
        Require(LlvmConstantFolding.ConstantFoldMemoryIntrinsic(LlvmIntrinsicKind.LaunderInvariantGroup,
            [nullPointer]) is null, "A detached launder null has no caller null-pointer contract.");
        entry.Append(launder);
        Require(ReferenceEquals(LlvmConstantFolding.ConstantFoldMemoryIntrinsic(LlvmIntrinsicKind.LaunderInvariantGroup,
            [nullPointer], launder), nullPointer), "Launder null folds with actual default-AS caller.");
        Require(launder.GetMemoryEffects().Raw == 12 && launder.CalledFunction!.IntrinsicDescriptor!.Speculatable,
            "Launder is speculatable but not readnone.");
        var undef = LlvmUndefValue.Get(pointer);
        Require(ReferenceEquals(LlvmConstantFolding.ConstantFoldMemoryIntrinsic(LlvmIntrinsicKind.LaunderInvariantGroup,
            [undef]), undef), "Launder undef follows the pinned scalar fold.");
        var as3 = LlvmPointerType.Get(context, 3);
        var declaration = LlvmIntrinsic.GetMemoryDeclaration(module, LlvmIntrinsicKind.MemCpy,
            [as3, pointer, i32, LlvmType.GetInt1Ty(context)]);
        Require(declaration.Name == "llvm.memcpy.p3.p0.i32", "Independent pointer address-space and length overloads.");
        entry.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(i32, 0)));
        foreach (var marker in new[] { LlvmIntrinsicKind.LifetimeStart, LlvmIntrinsicKind.LifetimeEnd })
        {
            var f = LlvmFunction.Create(LlvmFunctionType.Get(i32), LlvmLinkage.InternalLinkage, marker.Name + ".test", module);
            var block = new LlvmBasicBlock(context, "entry", f);
            var alloca = new LlvmAllocaInstruction(i32, LlvmConstantInt.Get(i64, 1), alignment: new(4)); block.Append(alloca);
            block.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(i32, 42), alloca));
            var lifetime = LlvmIntrinsic.CreateMemoryIntrinsic(marker, [alloca], module); block.Append(lifetime);
            var assume = LlvmIntrinsic.CreateAssumption(LlvmConstantInt.GetTrue(context), module,
                [new("align", [alloca, LlvmConstantInt.Get(i64, 4)])]); block.Append(assume);
            block.Append(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.FakeUse, [alloca, alloca], module));
            var load = new LlvmLoadInstruction(i32, alloca); block.Append(load);
            var ret = new LlvmReturnInstruction(context, load); block.Append(ret);
            Require(lifetime.ArgumentCount == 1 && lifetime.CalledFunction!.FunctionType.Parameters.Count == 1,
                "Pinned lifetime signature is pointer-only, not legacy size-plus-pointer.");
            Require(LlvmPromoteMemoryToRegister.IsAllocaPromotable(alloca), "Lifetime/droppable/fake-use does not block promotion.");
            using var assumptions = new LlvmAssumptionCache(f);
            LlvmPromoteMemoryToRegister.PromoteMemToReg([alloca], new(f), null, assumptions);
            Require(ret.GetOperand(0) is LlvmUndefValue, "Both lifetime markers reset promoted contents to undef.");
            Require(assume.Parent == block && !assume.Arguments.Any(value => ReferenceEquals(value, alloca)) &&
                !assume.OperandUses.Any(use => ReferenceEquals(use.Value, alloca)), "Droppable use is removed without deleting assume.");
            Require(alloca.Parent is null && lifetime.Parent is null, "Promotion removes actual alloca and markers.");
        }
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }
}
