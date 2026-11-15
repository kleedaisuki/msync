// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicBlockUtils.cpp GetIfCondition.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    private static LlvmCondBrInstruction? GetIfCondition(LlvmBasicBlock block,
        out LlvmBasicBlock ifTrue, out LlvmBasicBlock ifFalse)
    {
        ifTrue = ifFalse = null!;
        LlvmBasicBlock first, second;
        if (block.FirstInstruction is LlvmPhiInstruction phi)
        {
            if (phi.IncomingCount != 2) return null;
            first = phi.GetIncomingBlock(0); second = phi.GetIncomingBlock(1);
        }
        else
        {
            using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
            if (!predecessors.MoveNext()) return null;
            first = predecessors.Current;
            if (!predecessors.MoveNext()) return null;
            second = predecessors.Current;
            if (predecessors.MoveNext()) return null;
        }
        LlvmInstruction firstTerminator = first.GetTerminator(), secondTerminator = second.GetTerminator();
        if (secondTerminator is LlvmCondBrInstruction)
        {
            if (firstTerminator is LlvmCondBrInstruction) return null;
            (first, second) = (second, first);
            (firstTerminator, secondTerminator) = (secondTerminator, firstTerminator);
        }
        if (secondTerminator is not LlvmUncondBrInstruction) return null;
        if (firstTerminator is LlvmCondBrInstruction conditional)
        {
            if (LlvmBasicBlockUtils.GetSinglePredecessor(second) is null) return null;
            if (ReferenceEquals(conditional.GetSuccessor(0), block) && ReferenceEquals(conditional.GetSuccessor(1), second))
            { ifTrue = first; ifFalse = second; }
            else if (ReferenceEquals(conditional.GetSuccessor(0), second) && ReferenceEquals(conditional.GetSuccessor(1), block))
            { ifTrue = second; ifFalse = first; }
            else return null;
            return conditional;
        }
        if (firstTerminator is not LlvmUncondBrInstruction) return null;
        LlvmBasicBlock? common = LlvmBasicBlockUtils.GetSinglePredecessor(first);
        if (common is null || !ReferenceEquals(common, LlvmBasicBlockUtils.GetSinglePredecessor(second)) || common.GetTerminator() is not LlvmCondBrInstruction branch) return null;
        if (ReferenceEquals(branch.GetSuccessor(0), first)) { ifTrue = first; ifFalse = second; }
        else { ifTrue = second; ifFalse = first; }
        return branch;
    }
}
