// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/BasicBlock.cpp
// BasicBlock::~BasicBlock / eraseFromParent; IR/Value.cpp Value::~Value.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmBasicBlock
{
    public void EraseFromParent()
    {
        RemoveFromParent();
        DeleteValue();
    }

    public void DeleteValue()
    {
        Debug.Assert(Parent is null, "BasicBlock still linked into the program");
        foreach (LlvmInstruction instruction in instructions) instruction.DropAllReferences();
        while (FirstInstruction is { } instruction) instruction.EraseFromParent();
        NotifyValueDeletion();
        Debug.Assert(UseEmpty);
        DestroyValueName();
    }
}
