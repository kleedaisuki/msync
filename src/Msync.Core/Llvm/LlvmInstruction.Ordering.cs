// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/Instruction.h Order; lib/IR/Instruction.cpp Instruction::comesBefore.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    internal uint Order;

    public bool ComesBefore(LlvmInstruction other, CompilationTrace? trace = null)
    {
        Debug.Assert(Parent is not null && other.Parent is not null);
        Debug.Assert(ReferenceEquals(Parent, other.Parent));
        LlvmBasicBlock block = Parent!;
        if (!block.IsInstrOrderValid) block.RenumberInstructions(trace);
        return Order < other.Order;
    }
}
