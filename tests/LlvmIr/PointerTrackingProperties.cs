using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class PointerTrackingProperties
{
    internal static int Run()
    {
        LlvmContext context = new();
        var layout = new LlvmDataLayout();
        var pointer = LlvmType.GetPointerTy(context);
        var i8 = LlvmType.GetInt8Ty(context); var i64 = LlvmType.GetInt64Ty(context);
        var function = new LlvmFunction(LlvmFunctionType.Get(pointer, [pointer, pointer, i64]), LlvmLinkage.InternalLinkage, 0);
        var first = function.Arguments[0]; var second = function.Arguments[1]; var index = function.Arguments[2];
        var three = LlvmConstantInt.Get(i64, 3); var zero = LlvmConstantInt.Get(i64, 0);
        var gep = new LlvmGetElementPtrInstruction(i8, first, [three]);
        var offset = LlvmAPInt.GetZero(64);
        Program.Require(LlvmValueTracking.StripAndAccumulateConstantOffsets(gep, layout, ref offset) == first && offset.GetZExtValue() == 3,
            "Shared offset stripping must preserve the byte GEP displacement.");
        offset = LlvmAPInt.GetZero(64);
        Program.Require(LlvmValueTracking.StripAndAccumulateConstantOffsets(gep, layout, ref offset, allowNonInbounds: false) == gep && offset.IsZero,
            "Inbounds-only stripping must stop before an unflagged GEP.");
        var structure = LlvmStructType.Get(context, [i8, LlvmType.GetDoubleTy(context)]);
        var field = new LlvmGetElementPtrInstruction(structure, first, [zero, LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1)]);
        offset = LlvmAPInt.GetZero(64);
        Program.Require(LlvmValueTracking.AccumulateConstantOffset(field, layout, ref offset) && offset.GetZExtValue() == 8,
            $"Struct field offsets must come from the DataLayout: expected 8 for {{i8,double}}, observed {offset.GetZExtValue()}, field ABI alignment {layout.GetABITypeAlign(LlvmType.GetDoubleTy(context)).Value}.");
        var integerStructure = LlvmStructType.Get(context, [i8, i64]);
        var integerField = new LlvmGetElementPtrInstruction(integerStructure, first,
            [zero, LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1)]);
        offset = LlvmAPInt.GetZero(64);
        Program.Require(LlvmValueTracking.AccumulateConstantOffset(integerField, layout, ref offset) && offset.GetZExtValue() == 4,
            $"LLVM default {{i8,i64}} field offset must be 4 (ABI alignment 4, preferred alignment 8); observed {offset.GetZExtValue()}.");
        var varying = new LlvmGetElementPtrInstruction(i64, first, [index]);
        offset = LlvmAPInt.GetZero(64);
        Program.Require(!LlvmValueTracking.AccumulateConstantOffset(varying, layout, ref offset), "Variable GEP indices do not supply invented constant facts.");
        Program.Require(LlvmValueTracking.AccumulateConstantOffset(varying, layout, ref offset, externalAnalysis: _ => new LlvmAPInt(64, 3)) && offset.GetZExtValue() == 24,
            "A supplied external index fact follows the source scaled-offset transfer.");
        offset = LlvmAPInt.GetZero(64);
        Program.Require(!LlvmValueTracking.AccumulateConstantOffset(varying, layout, ref offset, externalAnalysis: _ => LlvmAPInt.GetSignedMaxValue(64)),
            "The source external-analysis path must report signed offset multiplication overflow.");
        var toInteger = new LlvmCastInstruction(LlvmOpcode.PtrToInt, first, i64);
        var integerOffset = new LlvmBinaryOperator(LlvmOpcode.Add, toInteger, three);
        var toPointer = new LlvmCastInstruction(LlvmOpcode.IntToPtr, integerOffset, pointer);
        offset = LlvmAPInt.GetZero(64);
        Program.Require(LlvmValueTracking.StripAndAccumulateConstantOffsets(toPointer, layout, ref offset) == toPointer,
            "IntToPtr lookthrough is disabled by the source default.");
        Program.Require(LlvmValueTracking.StripAndAccumulateConstantOffsets(toPointer, layout, ref offset, lookThroughIntToPtr: true) == first && offset.GetZExtValue() == 3,
            "Explicit IntToPtr lookthrough accumulates the represented ptrtoint/add pattern.");
        var same = new LlvmSelectInstruction(LlvmConstantInt.GetTrue(context), gep, first);
        var distinct = new LlvmSelectInstruction(LlvmConstantInt.GetTrue(context), first, second);
        Program.Require(LlvmValueTracking.GetUnderlyingObjectAggressive(same) == first && LlvmValueTracking.GetUnderlyingObjectAggressive(distinct) == distinct,
            "Aggressive underlying-object lookup finds a common base but preserves the first-object fallback for distinct bases.");
        var addressCast = new LlvmCastInstruction(LlvmOpcode.AddrSpaceCast, first, LlvmType.GetPointerTy(context, 1));
        Program.Require(LlvmValueTracking.StripPointerCasts(addressCast) == first && LlvmValueTracking.StripPointerCastsSameRepresentation(addressCast) == addressCast,
            "Only the source same-representation strip kind stops at address-space casts.");
        return 11;
    }
}
