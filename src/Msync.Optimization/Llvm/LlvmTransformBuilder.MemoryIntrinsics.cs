// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.cpp
// CreateMemSet/Inline, CreateMemTransferInst, CreateLifetimeStart/End, CreateInvariantStart.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmCallInstruction CreateMemSet(LlvmValue destination, LlvmValue value, LlvmValue length,
        LlvmAlign? alignment, bool isVolatile = false) =>
        CreateMemoryCall(LlvmIntrinsicKind.MemSet, destination, value, length, alignment, null, isVolatile);
    public LlvmCallInstruction CreateMemSet(LlvmValue destination, LlvmValue value, ulong length,
        LlvmAlign? alignment, bool isVolatile = false) =>
        CreateMemSet(destination, value, LlvmConstantInt.Get(LlvmType.GetInt64Ty(destination.Context), length), alignment, isVolatile);
    public LlvmCallInstruction CreateMemSetInline(LlvmValue destination, LlvmAlign? alignment, LlvmValue value,
        LlvmValue length, bool isVolatile = false) =>
        CreateMemoryCall(LlvmIntrinsicKind.MemSetInline, destination, value, length, alignment, null, isVolatile);
    public LlvmCallInstruction CreateMemCpy(LlvmValue destination, LlvmAlign? destinationAlignment,
        LlvmValue source, LlvmAlign? sourceAlignment, LlvmValue length, bool isVolatile = false) =>
        CreateMemoryCall(LlvmIntrinsicKind.MemCpy, destination, source, length, destinationAlignment, sourceAlignment, isVolatile);
    public LlvmCallInstruction CreateMemCpyInline(LlvmValue destination, LlvmAlign? destinationAlignment,
        LlvmValue source, LlvmAlign? sourceAlignment, LlvmValue length, bool isVolatile = false) =>
        CreateMemoryCall(LlvmIntrinsicKind.MemCpyInline, destination, source, length, destinationAlignment, sourceAlignment, isVolatile);
    public LlvmCallInstruction CreateMemMove(LlvmValue destination, LlvmAlign? destinationAlignment,
        LlvmValue source, LlvmAlign? sourceAlignment, LlvmValue length, bool isVolatile = false) =>
        CreateMemoryCall(LlvmIntrinsicKind.MemMove, destination, source, length, destinationAlignment, sourceAlignment, isVolatile);

    private LlvmCallInstruction CreateMemoryCall(LlvmIntrinsicKind kind, LlvmValue destination, LlvmValue sourceOrValue,
        LlvmValue length, LlvmAlign? destinationAlignment, LlvmAlign? sourceAlignment, bool isVolatile)
    {
        var call = LlvmIntrinsic.CreateMemoryIntrinsic(kind,
            [destination, sourceOrValue, length, LlvmConstantInt.GetBool(destination.Context, isVolatile)], InsertBlock!.Module!, Metrics);
        LlvmMemIntrinsic.TryGet(call, out var memory);
        if (destinationAlignment is { } dest) memory.SetDestAlignment(dest);
        if (sourceAlignment is { } source) memory.SetSourceAlignment(source);
        return Insert(call);
    }
    public LlvmCallInstruction CreateLifetimeStart(LlvmValue pointer) =>
        Insert(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.LifetimeStart, [pointer], InsertBlock!.Module!, Metrics));
    public LlvmCallInstruction CreateLifetimeEnd(LlvmValue pointer) =>
        Insert(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.LifetimeEnd, [pointer], InsertBlock!.Module!, Metrics));
    public LlvmCallInstruction CreateInvariantStart(LlvmValue pointer, LlvmConstantInt? size = null) =>
        Insert(LlvmIntrinsic.CreateMemoryIntrinsic(LlvmIntrinsicKind.InvariantStart,
            [size ?? LlvmConstantInt.Get(LlvmType.GetInt64Ty(pointer.Context), ulong.MaxValue), pointer], InsertBlock!.Module!, Metrics));
}
