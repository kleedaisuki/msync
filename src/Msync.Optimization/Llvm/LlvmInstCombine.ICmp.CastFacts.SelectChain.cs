// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp dominatesAllUses/isChainSelectCmpBranch/replacedSelectWithOperand.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool DominatesAllUses(LlvmInstruction definition, LlvmInstruction user, LlvmBasicBlock block)
        {
            Count("icmp.select-chain.dominance-queries");
            if (definition.Parent is null || definition.Parent != user.Parent || definition.Parent == block) return false;
            foreach (LlvmUser other in definition.Users)
            {
                Count("icmp.select-chain.dominance-uses");
                if (other != user && !DT.Dominates(block, ((LlvmInstruction)other).Parent)) return false;
            }
            return true;
        }
        private static bool IsChainSelectCmpBranch(LlvmSelectInstruction select) =>
            select.Parent?.GetTerminatorOrNull() is LlvmCondBrInstruction { Condition: LlvmICmpInstruction compare } &&
            (compare.Left == select || compare.Right == select);

        private bool ReplacedSelectWithOperand(LlvmSelectInstruction select, LlvmICmpInstruction compare, int operand)
        {
            Debug.Assert(operand is 1 or 2);
            Count("icmp.select-chain.queries");
            if (!IsChainSelectCmpBranch(select) || compare.Predicate != LlvmCmpPredicate.ICmpEq) return false;
            LlvmBasicBlock successor = select.Parent!.GetTerminator().GetSuccessor(1);
            if (LlvmBasicBlockUtils.GetSinglePredecessor(successor) is null || !DominatesAllUses(select, compare, successor)) return false;
            LlvmValue replacement = select.GetOperand(operand)!;
            foreach (LlvmUse use in select.Uses.ToArray())
            {
                if (((LlvmInstruction)use.User).Parent == select.Parent) continue;
                ReplaceUse(use, replacement);
                Count("icmp.select-chain.replaced-uses");
            }
            Count("icmp.select-chain.rewrites");
            return true;
        }
    }
}
