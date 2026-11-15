// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Instruction.h isEHPad;
// Instruction.cpp mayReadFromMemory/mayWriteToMemory/mayThrow/willReturn.
namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    public bool IsEHPad => Opcode is LlvmOpcode.CatchSwitch or LlvmOpcode.CatchPad or
        LlvmOpcode.CleanupPad or LlvmOpcode.LandingPad;

    public bool MayReadFromMemory() => Opcode switch
    {
        LlvmOpcode.VAArg or LlvmOpcode.Load or LlvmOpcode.Fence or LlvmOpcode.AtomicCmpXchg or
            LlvmOpcode.AtomicRMW or LlvmOpcode.CatchPad or LlvmOpcode.CatchRet => true,
        LlvmOpcode.Call or LlvmOpcode.Invoke or LlvmOpcode.CallBr =>
            this is not LlvmCallBase call || (call.GetMemoryEffects().Raw & LlvmMemoryEffectsAttribute.ReadRaw) != 0,
        LlvmOpcode.Store => !((LlvmStoreInstruction)this).IsUnordered,
        LlvmOpcode.HeapAlloc or LlvmOpcode.FileOut => true,
        LlvmOpcode.FileIn => ((LlvmFileInInst)this).Format == LlvmFileInFormat.Text,
        _ => false
    };

    public bool MayWriteToMemory() => Opcode switch
    {
        LlvmOpcode.Fence or LlvmOpcode.Store or LlvmOpcode.VAArg or LlvmOpcode.AtomicCmpXchg or
            LlvmOpcode.AtomicRMW or LlvmOpcode.CatchPad or LlvmOpcode.CatchRet => true,
        LlvmOpcode.Call or LlvmOpcode.Invoke or LlvmOpcode.CallBr =>
            this is not LlvmCallBase call || (call.GetMemoryEffects().Raw & LlvmMemoryEffectsAttribute.WriteRaw) != 0,
        LlvmOpcode.Load => !((LlvmLoadInstruction)this).IsUnordered,
        LlvmOpcode.HeapAlloc or LlvmOpcode.FileOut => true,
        _ => false
    };

    public bool MayThrow(bool includePhaseOneUnwind = false) => Opcode switch
    {
        LlvmOpcode.Call => this is not LlvmCallBase { DoesNotThrow: true },
        LlvmOpcode.Resume => true,
        LlvmOpcode.CleanupPad => includePhaseOneUnwind,
        LlvmOpcode.CleanupRet or LlvmOpcode.CatchSwitch or LlvmOpcode.Invoke =>
            throw new NotSupportedException("Exception destination/landing-pad state is not yet represented for this opcode."),
        LlvmOpcode.FileIn => ((LlvmFileInInst)this).Format == LlvmFileInFormat.Text,
        LlvmOpcode.HeapAlloc or LlvmOpcode.FileOut => true,
        _ => false
    };

    public bool MayHaveSideEffects() => MayWriteToMemory() || MayThrow() || !WillReturn();

    public bool WillReturn()
    {
        if (this is LlvmCallBase call) return call.HasWillReturn && !OperationIsVolatile();
        return !OperationIsVolatile();
    }

    internal bool OperationIsVolatile() => Opcode switch
    {
        LlvmOpcode.Load => ((LlvmLoadInstruction)this).IsVolatile,
        LlvmOpcode.Store => ((LlvmStoreInstruction)this).IsVolatile,
        LlvmOpcode.AtomicRMW or LlvmOpcode.AtomicCmpXchg =>
            throw new NotSupportedException("Atomic RMW/CmpXchg volatility state is not yet represented."),
        LlvmOpcode.Call => ((LlvmCallBase)this).IsVolatile,
        _ => false
    };
}
