// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitCondBrInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitCondBrInst(LlvmCondBrInstruction branch)
        {
            LlvmValue condition = branch.Condition;
            if (MatchBranchNot(condition) is { } value && value is not LlvmConstant)
            {
                branch.SwapSuccessors();
                Count("control.branch.not-inverted");
                return ReplaceOperand(branch, 0, value);
            }
            if (condition is LlvmSelectInstruction { HasOneUse: true, FalseValue: LlvmConstant { IsNullValue: true } } logicalAnd &&
                logicalAnd.TrueValue.HasOneUse && MatchBranchNot(logicalAnd.TrueValue) is { } right)
            {
                LlvmValue invertedLeft = Builder.CreateNot(logicalAnd.Condition, "not." + logicalAnd.Condition.Name);
                LlvmValue logicalOr = Builder.CreateLogicalOr(invertedLeft, right);
                branch.SwapSuccessors();
                Count("control.branch.logical-and-inverted");
                return ReplaceOperand(branch, 0, logicalOr);
            }
            if (condition is not LlvmConstantInt && branch.GetSuccessor(0) == branch.GetSuccessor(1))
                return ReplaceOperand(branch, 0, LlvmConstantInt.GetFalse(condition.Context));
            if (condition is LlvmFCmpInstruction { HasOneUse: true } comparison && !IsCanonicalPredicate(comparison.Predicate))
            {
                comparison.Predicate = LlvmCmpInstruction.GetInversePredicate(comparison.Predicate);
                branch.SwapSuccessors();
                Worklist.Push(comparison);
                Count("control.branch.fcmp-inverted");
                return branch;
            }
            if (condition is LlvmUndefValue)
            {
                HandlePotentiallyDeadSuccessors(branch.Parent!, null);
                return null;
            }
            if (condition is LlvmConstantInt constant)
            {
                HandlePotentiallyDeadSuccessors(branch.Parent!, branch.GetSuccessor(constant.IsZero ? 1 : 0));
                return null;
            }
            if (condition is not LlvmConstant && branch.GetSuccessor(0) != branch.GetSuccessor(1))
                ReplaceBranchDominatedUses(branch, condition);
            DC.RegisterBranch(branch);
            return null;
        }

        private void ReplaceBranchDominatedUses(LlvmCondBrInstruction branch, LlvmValue condition)
        {
            foreach (LlvmUse use in condition.Uses.ToArray())
            {
                Count("control.branch.dominated-uses.examined");
                if (DT.Dominates(new LlvmBasicBlockEdge(branch.Parent!, branch.GetSuccessor(0)), use))
                {
                    ReplaceUse(use, LlvmConstantInt.GetTrue(condition.Context));
                    Worklist.Push((LlvmInstruction)use.User);
                    continue;
                }
                if (!DT.Dominates(new LlvmBasicBlockEdge(branch.Parent!, branch.GetSuccessor(1)), use)) continue;
                ReplaceUse(use, LlvmConstantInt.GetFalse(condition.Context));
                Worklist.Push((LlvmInstruction)use.User);
            }
        }

        private static LlvmValue? MatchBranchNot(LlvmValue value)
        {
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } binary) return null;
            if (binary.Right is LlvmConstantInt { IsMinusOne: true }) return binary.Left;
            return binary.Left is LlvmConstantInt { IsMinusOne: true } ? binary.Right : null;
        }
    }
}
