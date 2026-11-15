using Msync.Core.Llvm;

internal static class CoreAttributeProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        var i32 = LlvmType.GetInt32Ty(context);
        var ptr = LlvmPointerType.Get(context, 0);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i32, [ptr]), LlvmLinkage.ExternalLinkage);
        var caller = LlvmFunction.Create(LlvmFunctionType.Get(i32, [ptr]), LlvmLinkage.InternalLinkage);
        var call = new LlvmCallInstruction(function.FunctionType, function, [caller.Arguments[0]]);
        function.SetParamAttributes(0, new() { CaptureInfo = LlvmCaptureInfo.RetOnly(LlvmCaptureComponents.ReadProvenance),
            Flags = LlvmAttributeKind.ReadOnly | LlvmAttributeKind.NoUndef });
        Program.Require(call.GetParamAttributes(0).IsEmpty && call.ParamHasAttr(0, LlvmAttributeKind.ReadOnly)
            && call.IsPassingUndefUB(0), "Callsite payloads and effective function queries must remain distinct.");
        Program.Require(!call.GetCaptureInfo(0).CapturesFullProvenance && call.GetCaptureInfo(0).IsRetOnly,
            "Read-provenance return capture is not full-provenance capture.");
        call.SetParamAttributes(0, new() { CaptureInfo = LlvmCaptureInfo.None(), Alignment = new(16) });
        Program.Require(call.DoesNotCapture(0) && call.GetParamAlign(0) == new LlvmAlign(16),
            "Call captures intersect declaration and callsite; call alignment is own payload.");
        var clone = (LlvmCallInstruction)call.Clone();
        clone.SetParamAttributes(0, new() { Alignment = new(4) });
        Program.Require(call.GetParamAlign(0) == new LlvmAlign(16), "Clone attribute dictionary must not alias original.");
        function.ReturnAttributes = new() { Range = new(new(32, 1), new(32, 10)) };
        call.ReturnAttributes = new() { Range = new(new(32, 4), new(32, 12)),
            Flags = LlvmAttributeKind.NoUndef };
        Program.Require(call.GetRange() == new LlvmConstantRange(new LlvmAPInt(32, 4), new LlvmAPInt(32, 10)),
            "Effective return range must intersect source modular intervals.");
        call.DropPoisonGeneratingAnnotations();
        Program.Require(call.ReturnAttributes.Range is null &&
            call.ReturnAttributes.NoUndef && call.GetParamAlign(0) is null,
            "Dropping poison annotations must not drop UB-implying noundef.");
        call.DropUBImplyingAttrsAndMetadata();
        Program.Require(!call.ReturnAttributes.NoUndef,
            "UB-implying annotation drop edits only callsite attributes.");
        Program.Require(function.ReturnAttributes.Range is not null && call.IsPassingUndefUB(0),
            "Declaration attributes survive callsite annotation dropping.");

        var pointerFunction = LlvmFunction.Create(LlvmFunctionType.Get(ptr, []), LlvmLinkage.ExternalLinkage);
        pointerFunction.ReturnAttributes = new() { DereferenceableBytes = 8 };
        var pointerCall = new LlvmCallInstruction(pointerFunction.FunctionType, pointerFunction, [])
        { ReturnAttributes = new() { DereferenceableBytes = 4, Flags = LlvmAttributeKind.NoUndef | LlvmAttributeKind.NonNull } };
        Program.Require(pointerCall.GetRetDereferenceableBytes() == 8, "Effective return dereferenceability takes the source maximum.");
        pointerCall.DropPoisonGeneratingAnnotations();
        Program.Require(!pointerCall.ReturnAttributes.NonNull && pointerCall.ReturnAttributes.NoUndef,
            "Poison annotation removal drops nonnull but retains noundef on a pointer return.");
        pointerCall.DropUBImplyingAttrsAndMetadata();
        Program.Require(pointerCall.ReturnAttributes.DereferenceableBytes is null && !pointerCall.ReturnAttributes.NoUndef,
            "UB annotation removal drops actual pointer dereferenceability and noundef payloads.");

        var a = new LlvmParameterAttributes { Range = new(new(32, 1), new(32, 5)) };
        var b = new LlvmParameterAttributes { Range = new(new(32, 3), new(32, 8)) };
        Program.Require(a.TryIntersect(b, out var common) && common.Range?.ToConstantRange() == new LlvmConstantRange(new LlvmAPInt(32, 1), new LlvmAPInt(32, 8)),
            "Integer range attribute intersection uses modular range union.");
        a = new() { Alignment = new(16), CaptureInfo = LlvmCaptureInfo.None() };
        b = new() { Alignment = new(4), CaptureInfo = LlvmCaptureInfo.RetOnly() };
        Program.Require(a.TryIntersect(b, out common) && common.Alignment == new LlvmAlign(4) && common.CaptureInfo == LlvmCaptureInfo.RetOnly(),
            "Pointer attribute intersection takes minimum alignment and capture union.");
        a = a with { ByValType = i32 }; b = b with { ByValType = i32 };
        Program.Require(!a.TryIntersect(b, out _), "Byval makes alignment an ABI-preserved attribute.");
        a = new() { NoFPClass = LlvmFPClassTest.NaN }; b = new() { NoFPClass = LlvmFPClassTest.QNaN };
        Program.Require(a.TryIntersect(b, out common) && common.NoFPClass == LlvmFPClassTest.QNaN,
            "Floating-point class attributes intersect their exclusion masks.");

        var x = LlvmConstantInt.Get(i32, 2);
        var add = new LlvmBinaryOperator(LlvmOpcode.Add, x, x) { HasNoSignedWrap = true, HasNoUnsignedWrap = true };
        var copied = new LlvmBinaryOperator(LlvmOpcode.Add, x, x);
        copied.CopyIRFlags(add);
        Program.Require(copied.HasNoSignedWrap && copied.HasNoUnsignedWrap, "IR flag copy must retain both wrap flags.");
        copied.DropPoisonGeneratingFlags();
        Program.Require(!copied.HasPoisonGeneratingFlags(), "Poison flag removal must clear overflowing binary flags.");
        var signed = new LlvmCmpPredicateInfo(LlvmCmpPredicate.ICmpSlt, true);
        var unsigned = new LlvmCmpPredicateInfo(LlvmCmpPredicate.ICmpUlt);
        Program.Require(LlvmCmpPredicateInfo.GetMatching(signed, unsigned)?.Predicate == LlvmCmpPredicate.ICmpUlt &&
            LlvmCmpPredicateInfo.GetMatching(new(LlvmCmpPredicate.ICmpSlt), unsigned) is null,
            "Only an explicit same-sign flag permits matching flipped signedness.");
        function.DenormalFPEnvironment = new(LlvmDenormalModeInfo.IEEE, LlvmDenormalModeInfo.PreserveSign);
        Program.Require(function.GetDenormalMode((LlvmAPFloatSemantics)2).InputsAreZero &&
            function.GetDenormalMode((LlvmAPFloatSemantics)3) == LlvmDenormalModeInfo.IEEE,
            "F32 denormal environment must override only single precision.");
        function.VScaleRange = new(4, 4);
        Program.Require(function.GetVScaleValue() == 4, "A fixed vscale source range must return its positive singleton.");
        var module = new LlvmModule("droppable", context);
        var condition = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, x, x);
        var assume = LlvmIntrinsic.CreateAssumption(condition, module, [new("nonnull", [caller.Arguments[0]])]);
        Program.Require(condition.HasNUndroppableUses(0), "Assume condition is a droppable use, not an ordinary use.");
        condition.DropDroppableUses();
        Program.Require(assume.GetArgument(0) is LlvmConstantInt { IsOne: true } && condition.UseEmpty,
            "Dropped assumption condition must become literal true.");
        LlvmValue.DropDroppableUse(assume.GetOperandUse(1));
        Program.Require(assume.GetOperandBundleAt(0).Tag == "ignore" && assume.GetOperand(1) is LlvmPoisonValue,
            "Dropped bundle operand must become poison and retag the actual owning bundle.");
        condition.DeleteValue();
        return 23;
    }
}
