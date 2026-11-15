// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MustExecute.cpp
// ICFLoopSafetyInfo including instruction insertion/removal cache hooks.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmICFLoopSafetyInfo : LlvmLoopSafetyInfo
{
    private readonly bool mayThrow;
    private readonly LlvmImplicitControlFlowTracking implicitControlFlow;
    private readonly LlvmMemoryWriteTracking memoryWrites;

    public LlvmICFLoopSafetyInfo(LlvmLoop loop, Dictionary<string, ulong>? metrics = null) : base(loop, metrics)
    {
        implicitControlFlow = new(metrics);
        memoryWrites = new(metrics);
        foreach (var block in loop.Blocks)
        {
            Count("loop_execution.icf.initial_blocks");
            if (!implicitControlFlow.HasICF(block)) continue;
            mayThrow = true;
            break;
        }
    }

    public override bool BlockMayThrow(LlvmBasicBlock block) => implicitControlFlow.HasICF(block);
    public override bool AnyBlockMayThrow() => mayThrow;
    public override bool IsGuaranteedToExecute(LlvmInstruction instruction, LlvmDominatorTree dominators)
    {
        Count("loop_execution.icf.queries");
        if (implicitControlFlow.IsDominatedByICFIFromSameBlock(instruction))
        { Count("loop_execution.icf.preceding_implicit_exit"); return false; }
        return AllLoopPathsLeadToBlock(instruction.Parent!, dominators);
    }

    public bool DoesNotWriteMemoryBefore(LlvmBasicBlock block)
    {
        Debug.Assert(CurrentLoop.Contains(block));
        Count("loop_execution.memory_before.block_queries");
        if (block == CurrentLoop.Header) return true;
        HashSet<LlvmBasicBlock> predecessors = new(ReferenceEqualityComparer.Instance);
        CollectTransitivePredecessors(block, predecessors);
        foreach (var predecessor in predecessors)
        {
            if (!memoryWrites.MayWriteToMemory(predecessor)) continue;
            Count("loop_execution.memory_before.predecessor_write");
            return false;
        }
        return true;
    }

    public bool DoesNotWriteMemoryBefore(LlvmInstruction instruction)
    {
        Debug.Assert(CurrentLoop.Contains(instruction.Parent!));
        Count("loop_execution.memory_before.instruction_queries");
        if (memoryWrites.IsDominatedByMemoryWriteFromSameBlock(instruction))
        { Count("loop_execution.memory_before.preceding_write"); return false; }
        return DoesNotWriteMemoryBefore(instruction.Parent!);
    }

    public void InsertInstructionTo(LlvmInstruction instruction, LlvmBasicBlock block)
    {
        implicitControlFlow.InsertInstructionTo(instruction, block);
        memoryWrites.InsertInstructionTo(instruction, block);
    }

    public void RemoveInstruction(LlvmInstruction instruction)
    {
        implicitControlFlow.RemoveInstruction(instruction);
        memoryWrites.RemoveInstruction(instruction);
    }
}
