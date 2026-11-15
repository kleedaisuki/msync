// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp 8917-9210.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool SimplifyUncondBranch(LlvmUncondBrInstruction branch, LlvmTransformBuilder builder)
        {
            LlvmBasicBlock block = branch.Parent!;
            bool canonicalLoop = Options.NeedCanonicalLoop && LoopHeaders.Count != 0 &&
                LlvmDominatorTree.Predecessors(block).Take(2).Count() == 2 &&
                (IsLoopHeader(block) || IsLoopHeader(branch.GetSuccessor(0)));
            LlvmInstruction first = FirstNonPhi(block);
            if (first == branch && block != block.Parent!.BasicBlocks[0] && !canonicalLoop &&
                LlvmLocalCfg.TryToSimplifyUncondBranchFromEmptyBlock(block, DominatorUpdater, Metrics)) return true;
            if (first is not LlvmICmpInstruction compare || compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) ||
                compare.GetOperand(1) is not LlvmConstantInt) return false;
            if (compare.Next == branch) return TryToSimplifyUncondBranchWithICmpInIt(compare, builder);
            return compare.Next is LlvmSelectInstruction select && select.Next == branch &&
                TryToSimplifyUncondBranchWithICmpSelectInIt(compare, select, builder);
        }

        private bool SimplifyCondBranch(LlvmCondBrInstruction branch, LlvmTransformBuilder builder)
        {
            LlvmBasicBlock block = branch.Parent!;
            if (!Options.SimplifyCondBranch) return false;
            if (IsValueEqualityComparison(branch) is not null)
            {
                LlvmBasicBlock? predecessor = LlvmBasicBlockUtils.GetSinglePredecessor(block);
                if (predecessor is not null && SimplifyEqualityComparisonWithOnlyPredecessor(branch, predecessor, builder))
                    return RequestResimplify();
                foreach (LlvmInstruction instruction in block.Instructions)
                {
                    if (instruction == branch.Condition) continue;
                    if (instruction == branch && FoldValueComparisonIntoPredecessors(branch, builder)) return RequestResimplify();
                    break;
                }
            }
            if (SimplifyBranchOnICmpChain(branch, builder)) return true;
            bool? implied = LlvmInstructionSimplify.IsImpliedByDomCondition(branch.Condition,
                new LlvmInstructionSimplify.BinaryQuery(Layout, branch, Metrics));
            if (implied is { } result)
            {
                LlvmValue old = branch.Condition;
                branch.Condition = LlvmConstantInt.Get((LlvmIntegerType)old.Type, result ? 1UL : 0UL);
                LlvmLocalCfg.RecursivelyDeleteTriviallyDeadInstructions(old, metrics: Metrics);
                Count("branches.implied-by-dominator");
                return RequestResimplify();
            }
            if (Options.SpeculateBlocks && FoldBranchToCommonDest(branch, DominatorUpdater, TargetInfo, Options,
                Tuning, Trace)) return RequestResimplify();
            if (LlvmBasicBlockUtils.GetSinglePredecessor(branch.GetSuccessor(0)) is not null)
            {
                if (LlvmBasicBlockUtils.GetSinglePredecessor(branch.GetSuccessor(1)) is not null)
                {
                    if (Tuning.HoistCommon && HoistCommonCodeFromSuccessors(branch, !Options.HoistCommonInsts))
                        return RequestResimplify();
                    if (Options.HoistLoadsStoresWithCondFaulting && IsProfitableToSpeculate(branch, null) &&
                        TryHoistConditionalMemory(branch)) return RequestResimplify();
                }
                else if (BranchesTo(branch.GetSuccessor(0), branch.GetSuccessor(1)) &&
                    SpeculativelyExecuteBB(branch, branch.GetSuccessor(0))) return RequestResimplify();
            }
            else if (LlvmBasicBlockUtils.GetSinglePredecessor(branch.GetSuccessor(1)) is not null &&
                BranchesTo(branch.GetSuccessor(1), branch.GetSuccessor(0)) &&
                SpeculativelyExecuteBB(branch, branch.GetSuccessor(1))) return RequestResimplify();
            if (FoldCondBranchOnValueKnownInPredecessor(branch)) return RequestResimplify();
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block))
                if (predecessor.GetTerminator() is LlvmCondBrInstruction prior && prior != branch &&
                    SimplifyCondBranchToCondBranch(prior, branch)) return RequestResimplify();
            if (Tuning.MergeCondStores && AllPredecessorsSameSource(block)?.GetTerminator() is LlvmCondBrInstruction previous &&
                previous != branch && MergeConditionalStores(previous, branch)) return RequestResimplify();
            return MergeNestedCondBranch(branch) && RequestResimplify();
        }
        private static bool BranchesTo(LlvmBasicBlock block, LlvmBasicBlock successor) =>
            block.GetTerminator() is { SuccessorCount: 1 } terminator && terminator.GetSuccessor(0) == successor;

        private bool TryHoistConditionalMemory(LlvmCondBrInstruction branch)
        {
            List<LlvmInstruction> instructions = [];
            foreach (LlvmBasicBlock successor in branch.Successors)
                foreach (LlvmInstruction instruction in successor.Instructions)
                {
                    if (instruction.IsTerminator)
                    {
                        if (instruction.SuccessorCount > 1) return false;
                        continue;
                    }
                    if (!IsSafeCheapLoadStore(instruction) || instructions.Count == Tuning.HoistLoadsStoresWithCondFaultingThreshold)
                        return false;
                    instructions.Add(instruction);
                }
            if (instructions.Count == 0) return false;
            HoistConditionalLoadsStores(branch, instructions);
            return true;
        }

        private bool MergeNestedCondBranch(LlvmCondBrInstruction branch)
        {
            LlvmBasicBlock block = branch.Parent!, left = branch.GetSuccessor(0), right = branch.GetSuccessor(1);
            if (!IsSimple(left, out LlvmCondBrInstruction? leftBranch) || !IsSimple(right, out LlvmCondBrInstruction? rightBranch))
                return false;
            if (leftBranch!.Condition != rightBranch!.Condition || leftBranch.GetSuccessor(0) != rightBranch.GetSuccessor(1) ||
                leftBranch.GetSuccessor(1) != rightBranch.GetSuccessor(0)) return false;
            LlvmBasicBlock third = leftBranch.GetSuccessor(0), fourth = leftBranch.GetSuccessor(1);
            if (third == fourth) return false;
            var builder = new LlvmTransformBuilder(branch, Metrics) { CurrentSource = branch.Source };
            branch.Condition = builder.CreateXor(branch.Condition, leftBranch.Condition);
            LlvmBasicBlockUtils.RemovePredecessor(left, block, trace: Trace, metrics: Metrics);
            branch.SetSuccessor(0, fourth);
            LlvmBasicBlockUtils.RemovePredecessor(right, block, trace: Trace, metrics: Metrics);
            branch.SetSuccessor(1, third);
            DominatorUpdater?.ApplyUpdates([
                new(LlvmCfgUpdateKind.Delete, block, left), new(LlvmCfgUpdateKind.Insert, block, fourth),
                new(LlvmCfgUpdateKind.Delete, block, right), new(LlvmCfgUpdateKind.Insert, block, third)]);
            Count("branches.nested-merged");
            return true;

            bool IsSimple(LlvmBasicBlock successor, out LlvmCondBrInstruction? successorBranch)
            {
                successorBranch = successor.FirstInstruction as LlvmCondBrInstruction;
                if (successor == block || successorBranch is null) return false;
                foreach (LlvmBasicBlock destination in successorBranch.Successors)
                    if (destination == successor || destination == block || destination.FirstInstruction is LlvmPhiInstruction)
                        return false;
                return true;
            }
        }
        private void EraseTerminatorAndDceCondition(LlvmInstruction terminator)
        {
            LlvmValue? condition = terminator is LlvmCondBrInstruction branch ? branch.Condition :
                terminator is LlvmSwitchInstruction @switch ? @switch.Condition : null;
            terminator.EraseFromParent();
            if (condition is not null) LlvmLocalCfg.RecursivelyDeleteTriviallyDeadInstructions(condition, metrics: Metrics);
        }
    }
}
