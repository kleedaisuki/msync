// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MustExecute.cpp
// SimpleLoopSafetyInfo computeLoopSafetyInfo, isGuaranteedToExecute.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmSimpleLoopSafetyInfo : LlvmLoopSafetyInfo
{
    private bool mayThrow;
    private readonly bool headerMayThrow;

    public LlvmSimpleLoopSafetyInfo(LlvmLoop loop, Dictionary<string, ulong>? metrics = null) : base(loop, metrics)
    {
        headerMayThrow = !LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(loop.Header, Metrics);
        mayThrow = headerMayThrow;
        for (int i = 1; i < loop.Blocks.Count; ++i)
        {
            mayThrow |= !LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(loop.Blocks[i], Metrics);
            if (mayThrow) break;
        }
    }

    public override bool BlockMayThrow(LlvmBasicBlock block) => AnyBlockMayThrow();
    public override bool AnyBlockMayThrow() => mayThrow;
    public override bool IsGuaranteedToExecute(LlvmInstruction instruction, LlvmDominatorTree dominators)
    {
        Count("loop_execution.simple.queries");
        if (instruction.Parent != CurrentLoop.Header) return AllLoopPathsLeadToBlock(instruction.Parent!, dominators);
        if (!headerMayThrow) return true;
        var first = instruction.Parent!.FirstInstruction;
        while (first is LlvmPhiInstruction) first = first.Next;
        return first == instruction;
    }
}
