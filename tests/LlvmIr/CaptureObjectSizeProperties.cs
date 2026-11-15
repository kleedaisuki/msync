using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CaptureObjectSizeProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmDataLayout layout = new();
        var i64 = LlvmType.GetInt64Ty(context);
        var one = LlvmConstantInt.Get(i64, 1);
        var three = LlvmConstantInt.Get(i64, 3);
        var alloca = new LlvmAllocaInstruction(i64, three, alignment: new LlvmAlign(32));
        var load = new LlvmLoadInstruction(i64, alloca);
        var store = new LlvmStoreInstruction(one, alloca);
        Program.Require(!LlvmCaptureTracking.PointerMayBeCaptured(alloca, true), "Loading and storing through an object do not capture the pointer.");
        Program.Require(LlvmObjectSize.GetBaseObjectSize(alloca, layout)?.GetFixedValue() == 24,
            "An alloca's base extent multiplies its constant count by allocation stride.");
        Program.Require(LlvmObjectSize.GetBaseObjectSize(alloca, layout, roundToAlign: true)?.GetFixedValue() == 32,
            "The source RoundToAlign option uses the explicit alloca alignment.");
        var pointer = LlvmPointerType.Get(context);
        var returned = new LlvmAllocaInstruction(i64, one);
        var ret = new LlvmReturnInstruction(context, returned);
        var result = LlvmCaptureTracking.PointerMayBeCaptured(returned, LlvmCaptureComponents.Provenance);
        Program.Require(result.WithoutRet.CapturesNothing() && result.WithRet.CapturesFullProvenance(),
            "Return capture remains separate from captures outside the return value.");
        var cache = new LlvmSimpleCaptureAnalysis();
        Program.Require(cache.GetCapturesBefore(returned, ret, true, false).CapturesNothing() &&
            cache.GetCapturesBefore(returned, ret, true, true).CapturesFullProvenance(),
            "SimpleCaptureAnalysis caches both return policies, not the first caller's policy.");
        var addressOnly = new LlvmAllocaInstruction(i64, one);
        var compare = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, addressOnly, LlvmConstantPointerNull.Get(pointer));
        var address = LlvmCaptureTracking.PointerMayBeCaptured(addressOnly, LlvmCaptureComponents.All);
        Program.Require(address.WithRet == LlvmCaptureComponents.AddressIsNull,
            "A base-pointer equality comparison with null captures only its nullness.");
        var ptrToAddr = new LlvmCastInstruction(LlvmOpcode.PtrToAddr, addressOnly, i64);
        Program.Require(LlvmCaptureTracking.PointerMayBeCaptured(addressOnly, LlvmCaptureComponents.Provenance).WithRet.CapturesNothing(),
            "Address-only observations do not acquire pointer provenance.");
        var escaped = new LlvmAllocaInstruction(i64, one);
        var slot = new LlvmAllocaInstruction(pointer, one);
        var escape = new LlvmStoreInstruction(escaped, slot);
        Program.Require(LlvmCaptureTracking.PointerMayBeCaptured(escaped, true), "Storing the pointer itself is a capture.");
        var derived = new LlvmAllocaInstruction(i64, one);
        var gep = new LlvmGetElementPtrInstruction(i64, derived, [one]);
        var derivedLoad = new LlvmLoadInstruction(i64, gep);
        Program.Require(!LlvmCaptureTracking.PointerMayBeCaptured(derived, true), "Capture tracking follows GEP result uses.");
        Program.Require(LlvmCaptureTracking.PointerMayBeCaptured(derived, true, maxUsesToExplore: 1),
            "The source use budget bounds derived-pointer exploration.");
        var called = new LlvmAllocaInstruction(i64, one);
        var functionType = LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [pointer]);
        var callee = LlvmFunction.Create(functionType, LlvmLinkage.ExternalLinkage, "unknown");
        var call = new LlvmCallInstruction(functionType, callee, [called]);
        Program.Require(LlvmCaptureTracking.PointerMayBeCaptured(called, true),
            "An unannotated call argument follows the source default capture attributes.");
        var bundled = new LlvmAllocaInstruction(i64, one);
        var bundleFunctionType = LlvmFunctionType.Get(LlvmType.GetVoidTy(context), []);
        var bundleCallee = LlvmFunction.Create(bundleFunctionType, LlvmLinkage.ExternalLinkage, "with_bundle");
        var bundleCall = new LlvmCallInstruction(bundleFunctionType, bundleCallee, [],
            [new LlvmOperandBundleDef("deopt", [bundled])]);
        Program.Require(!LlvmCaptureTracking.PointerMayBeCaptured(bundled, true),
            "Deopt operand bundles carry the source captures(none) semantics.");
        var global = new LlvmGlobalVariable(i64, false, LlvmLinkage.InternalLinkage, one);
        Program.Require(LlvmObjectSize.GetBaseObjectSize(global, layout)?.GetFixedValue() == 8,
            "A non-interposable initialized global has its allocation extent.");
        var unknownGlobal = new LlvmGlobalVariable(i64, false, LlvmLinkage.ExternalLinkage);
        Program.Require(LlvmObjectSize.GetBaseObjectSize(unknownGlobal, layout) is null,
            "A declaration does not supply a concrete base-object extent.");
        var nullPointer = LlvmConstantPointerNull.Get(pointer);
        Program.Require(LlvmObjectSize.GetBaseObjectSize(nullPointer, layout)?.IsZero == true &&
            LlvmObjectSize.GetBaseObjectSize(nullPointer, layout, nullIsUnknownSize: true) is null,
            "Null size follows the consumer's source NullIsUnknownSize option.");
        var huge = new LlvmAllocaInstruction(i64, LlvmConstantInt.Get(i64, ulong.MaxValue));
        Program.Require(LlvmObjectSize.GetBaseObjectSize(huge, layout) is null,
            "An overflowing allocation product is not a concrete object extent.");
        return 16;
    }
}
