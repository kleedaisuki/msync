// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/LoopInfo.cpp
// Loop::isLoopInvariant/hasLoopInvariantOperands and
// LoopInfo::wouldBeOutOfLoopUseRequiringLCSSA.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoop
{
    public bool IsLoopInvariant(LlvmValue value)
    {
        owner.RecordMutation("loop_info.query.invariant");
        return value is not LlvmInstruction instruction || !Contains(instruction);
    }
    public bool HasLoopInvariantOperands(LlvmInstruction instruction)
    {
        foreach (LlvmUse operand in instruction.OperandUses)
            if (!IsLoopInvariant(operand.Value!)) return false;
        return true;
    }
}

public sealed partial class LlvmLoopInfo
{
    public bool WouldBeOutOfLoopUseRequiringLCSSA(LlvmValue value, LlvmBasicBlock exitBlock)
    {
        RecordMutation("loop_info.query.out_of_loop_lcssa");
        if (value.Type.IsTokenLikeTy()) return false;
        if (value is not LlvmInstruction instruction) return false;
        LlvmLoop? loop = GetLoopFor(instruction.Parent!);
        if (loop is null) return false;
        return !loop.Contains(exitBlock);
    }
}
