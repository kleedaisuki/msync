// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Value.cpp deleteValue,
// IR/Instruction.cpp getInsertionPointAfterDef; represented non-EH instructions.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    public void DeleteValue()
    {
        Debug.Assert(Parent is null && UseEmpty);
        DropAllReferences();
        NotifyValueDeletion();
        DestroyValueName();
    }
    public LlvmInstruction? GetInsertionPointAfterDef()
    {
        Debug.Assert(!Type.IsVoidTy());
        if (this is not LlvmPhiInstruction) { Debug.Assert(!IsTerminator); return Next; }
        foreach (var instruction in Parent!.Instructions)
            if (instruction is not LlvmPhiInstruction) return instruction;
        return null;
    }
}
