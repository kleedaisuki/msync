// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp
// handleBranchCondition; Utils/Local.cpp replaceDominatedUsesWith.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private sealed partial class Work
    {
        private void HandleBranchCondition(LlvmInstruction condition, LlvmCondBrInstruction branch,
            LlvmBasicBlock block, LlvmBasicBlock predecessor)
        {
            bool takenTrue = branch.GetSuccessor(0) == block;
            var known = LlvmConstantInt.GetBool(block.Context, takenTrue);
            List<LlvmInstruction> pending = [condition];
            HashSet<LlvmInstruction> visited = new(ReferenceEqualityComparer.Instance);
            while (pending.Count != 0)
            {
                var current = pending[^1]; pending.RemoveAt(pending.Count - 1);
                values.Insert(current, known); Count("branch.condition_available");
                foreach (var use in current.Uses.ToArray())
                {
                    Count("branch.use_checked");
                    if (use.User is not LlvmInstruction user) continue;
                    bool dominates;
                    if (user is LlvmPhiInstruction phi)
                    {
                        var incoming = phi.GetIncomingBlock(use.OperandNo);
                        dominates = incoming == predecessor && phi.Parent == block ||
                            tree.DominatesEdge(predecessor, block, incoming);
                    }
                    else dominates = tree.DominatesEdge(predecessor, block, user.Parent!);
                    if (!dominates) continue;
                    use.Set(known); changed = true; Count("branch.dominated_use_replaced");
                }
                if (!MatchLogicalCondition(current, takenTrue, out var a, out var b)) continue;
                if (a is LlvmInstruction ai && CanHandleSimple(ai) && visited.Add(ai)) pending.Add(ai);
                if (b is LlvmInstruction bi && CanHandleSimple(bi) && visited.Add(bi)) pending.Add(bi);
            }
        }

        private static bool MatchLogicalCondition(LlvmInstruction instruction, bool and, out LlvmValue a, out LlvmValue b)
        {
            if (instruction is LlvmBinaryOperator binary && binary.Opcode == (and ? LlvmOpcode.And : LlvmOpcode.Or))
            { a = binary.Left; b = binary.Right; return true; }
            if (instruction is LlvmSelectInstruction select && (and ? select.FalseValue is LlvmConstantInt { IsZero: true } :
                select.TrueValue is LlvmConstantInt { IsOne: true }))
            { a = select.Condition; b = and ? select.TrueValue : select.FalseValue; return true; }
            a = b = null!; return false;
        }
    }
}
