// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LoopRotationUtils.cpp shouldSpeculateInstrs/simplifyLoopLatch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopRotation
{
    private sealed partial class Work
    {
        private bool SimplifyLoopLatch()
        {
            LlvmBasicBlock? latch = loop.GetLoopLatch();
            // BlockAddress constants are not represented, so source
            // hasAddressTaken cannot be true for the represented latch.
            if (latch?.GetTerminator() is not LlvmUncondBrInstruction jump) return false;
            LlvmBasicBlock? lastExit = LlvmBasicBlockUtils.GetSinglePredecessor(latch);
            if (lastExit is null || !loop.IsLoopExiting(lastExit)) return false;
            if (lastExit.GetTerminator() is not (LlvmUncondBrInstruction or LlvmCondBrInstruction)) return false;
            if (!ShouldSpeculateInstructions(latch.FirstInstruction, jump)) return false;
            using var updater = new LlvmDomTreeUpdater(tree, LlvmDomTreeUpdateStrategy.Eager, trace: trace, metrics: Metrics);
            bool merged = LlvmBasicBlockUtils.MergeBlockIntoPredecessor(latch, updater: updater, loops: loops,
                memoryUpdater: memoryUpdater, predecessorWithTwoSuccessors: true, trace: trace, metrics: Metrics);
            Count(merged ? "latch.merge_succeeded" : "latch.merge_declined");
            // Source returns true after the merge invocation; eligibility above
            // supplies its preconditions rather than a different heuristic.
            return true;
        }
        private bool ShouldSpeculateInstructions(LlvmInstruction? first, LlvmInstruction end)
        {
            bool seenIncrement = false;
            bool multipleExits = loop.GetExitingBlock() is null;
            for (LlvmInstruction? instruction = first; !ReferenceEquals(instruction, end); instruction = instruction!.Next)
            {
                Count("latch.speculation_instructions");
                if (!LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction!, metrics: Metrics))
                { Count("latch.not_speculatable"); return false; }
                switch (instruction!.Opcode)
                {
                    default: Count("latch.unsupported_speculation_opcode"); return false;
                    case LlvmOpcode.GetElementPtr:
                        if (!((LlvmGetElementPtrInstruction)instruction).HasAllConstantIndices) return false;
                        goto case LlvmOpcode.Add;
                    case LlvmOpcode.Add: case LlvmOpcode.Sub: case LlvmOpcode.And: case LlvmOpcode.Or:
                    case LlvmOpcode.Xor: case LlvmOpcode.Shl: case LlvmOpcode.LShr: case LlvmOpcode.AShr:
                        LlvmValue? operand = instruction.GetOperand(0) is not LlvmConstant ? instruction.GetOperand(0) :
                            instruction.GetOperand(1) is not LlvmConstant ? instruction.GetOperand(1) : null;
                        if (operand is null) return false;
                        if (multipleExits)
                            foreach (LlvmUser user in operand.Users)
                                if (!loop.Contains((LlvmInstruction)user)) return false;
                        if (seenIncrement) return false;
                        seenIncrement = true;
                        break;
                    case LlvmOpcode.Trunc: case LlvmOpcode.ZExt: case LlvmOpcode.SExt: break;
                }
            }
            return true;
        }
    }
}
