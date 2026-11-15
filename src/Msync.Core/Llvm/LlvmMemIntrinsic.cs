// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/IntrinsicInst.h MemIntrinsicBase, MemTransferBase, MemSetBase, MemIntrinsic.
using System.Diagnostics;
namespace Msync.Core.Llvm;

// A view over the actual Call and its Uses, not another instruction or operand owner.
public readonly struct LlvmMemIntrinsic
{
    private LlvmMemIntrinsic(LlvmCallBase call) => Call = call;
    public LlvmCallBase Call { get; }
    public LlvmIntrinsicKind Kind => Call.CalledFunction!.IntrinsicDescriptor!.Kind;
    public LlvmValue RawDest => Call.GetArgument(0);
    public LlvmUse RawDestUse => Call.GetOperandUse(0);
    public LlvmValue Length => Call.GetArgument(2);
    public LlvmUse LengthUse => Call.GetOperandUse(2);
    public LlvmAPInt? LengthInBytes => (Length as LlvmConstantInt)?.Value;
    public uint DestAddressSpace => ((LlvmPointerType)RawDest.Type).AddressSpace;
    public LlvmAlign? DestAlign => Call.GetParamAlign(0);
    public bool IsVolatile => !((LlvmConstantInt)Call.GetArgument(3)).IsZero;
    public bool IsForceInlined => Kind == LlvmIntrinsicKind.MemCpyInline || Kind == LlvmIntrinsicKind.MemSetInline;
    public bool IsMemSet => Kind == LlvmIntrinsicKind.MemSet || Kind == LlvmIntrinsicKind.MemSetInline;
    public bool IsMemTransfer => !IsMemSet;
    public bool IsMemCpy => Kind == LlvmIntrinsicKind.MemCpy || Kind == LlvmIntrinsicKind.MemCpyInline;
    public bool IsMemMove => Kind == LlvmIntrinsicKind.MemMove;
    public LlvmValue RawSource { get { Debug.Assert(IsMemTransfer); return Call.GetArgument(1); } }
    public LlvmUse RawSourceUse { get { Debug.Assert(IsMemTransfer); return Call.GetOperandUse(1); } }
    public uint SourceAddressSpace => ((LlvmPointerType)RawSource.Type).AddressSpace;
    public LlvmAlign? SourceAlign { get { Debug.Assert(IsMemTransfer); return Call.GetParamAlign(1); } }
    public LlvmValue Value { get { Debug.Assert(IsMemSet); return Call.GetArgument(1); } }
    public LlvmUse ValueUse { get { Debug.Assert(IsMemSet); return Call.GetOperandUse(1); } }

    public static bool TryGet(LlvmValue value, out LlvmMemIntrinsic memory)
    {
        if (value is LlvmCallBase { CalledFunction.IntrinsicDescriptor.IsMemoryIntrinsic: true } call)
        { memory = new(call); return true; }
        memory = default; return false;
    }

    public void SetDest(LlvmValue pointer)
    { Debug.Assert(pointer.Type == RawDest.Type); Call.SetArgument(0, pointer); }
    public void SetSource(LlvmValue pointer)
    { Debug.Assert(IsMemTransfer && pointer.Type == RawSource.Type); Call.SetArgument(1, pointer); }
    public void SetValue(LlvmValue value)
    { Debug.Assert(IsMemSet && value.Type == Value.Type); Call.SetArgument(1, value); }
    public void SetLength(LlvmValue value)
    { Debug.Assert(value.Type == Length.Type); Call.SetArgument(2, value); }
    public void SetLength(ulong value) => SetLength(LlvmConstantInt.Get((LlvmIntegerType)Length.Type, value));
    public void SetVolatile(LlvmConstant value)
    { Debug.Assert(value.Type.IsIntegerTy(1)); Call.SetArgument(3, value); }
    public void SetVolatile(bool value) => SetVolatile(LlvmConstantInt.GetBool(Call.Context, value));
    public void SetDestAlignment(LlvmAlign? alignment) => Call.SetParamAlign(0, alignment);
    public void SetSourceAlignment(LlvmAlign? alignment)
    { Debug.Assert(IsMemTransfer); Call.SetParamAlign(1, alignment); }
}

public abstract partial class LlvmInstruction
{
    public bool IsLifetimeStartOrEnd => this is LlvmCallBase { CalledFunction.IntrinsicDescriptor.IsLifetimeStartOrEnd: true };
}

public abstract partial class LlvmCallBase
{
    public bool IsVolatile => LlvmMemIntrinsic.TryGet(this, out var memory) && memory.IsVolatile;
}
