// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// tryToSimplifyUncondBranchWithICmp{,Select}InIt.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool TryToSimplifyUncondBranchWithICmpInIt(LlvmICmpInstruction compare, LlvmTransformBuilder builder) =>
            TryToSimplifyUncondBranchWithICmpSelectInIt(compare, null, builder);

        private bool TryToSimplifyUncondBranchWithICmpSelectInIt(LlvmICmpInstruction compare,
            LlvmSelectInstruction? select, LlvmTransformBuilder builder)
        {
            var block = compare.Parent!;
            if (block.FirstInstruction is LlvmPhiInstruction || !compare.HasOneUse || select is not null && !select.HasOneUse) return false;
            var predecessor = LlvmBasicBlockUtils.GetSinglePredecessor(block);
            if (predecessor?.GetTerminator() is not LlvmSwitchInstruction instruction || compare.Right is not LlvmConstantInt newCase) return false;
            Count("conditional.icmp_select.queries");
            LlvmValue whenTrue, whenFalse;
            LlvmInstruction user;
            if (select is null)
            {
                whenTrue = LlvmConstantInt.GetTrue(block.Context); whenFalse = LlvmConstantInt.GetFalse(block.Context);
                user = (LlvmInstruction)compare.Users.First();
            }
            else
            {
                if (select.Condition != compare) return false;
                whenTrue = select.TrueValue; whenFalse = select.FalseValue;
                user = (LlvmInstruction)select.Users.First();
            }
            if (instruction.Condition != compare.Left) return false;
            if (instruction.DefaultDestination != block)
            {
                LlvmConstantInt? value = null;
                for (int i = 0; i < instruction.CaseCount; ++i)
                    if (instruction.GetCaseDestination(i) == block) { value = instruction.GetCaseValue(i); break; }
                compare.Left = value!;
                if (LlvmInstructionSimplify.SimplifyInstruction(compare, metrics: Metrics) is { } simplified)
                { compare.ReplaceAllUsesWith(simplified); compare.EraseFromParent(); }
                Count("conditional.icmp_select.nondefault_fold");
                return RequestResimplify();
            }
            if (instruction.FindCaseValue(newCase) >= 0)
            {
                compare.ReplaceAllUsesWith(LlvmConstantInt.GetBool(block.Context, compare.Predicate != LlvmCmpPredicate.ICmpEq));
                compare.EraseFromParent();
                Count("conditional.icmp_select.excluded_case");
                return RequestResimplify();
            }
            var successor = block.GetTerminator().GetSuccessor(0);
            if (user is not LlvmPhiInstruction phi || phi != successor.FirstInstruction || phi.Next is LlvmPhiInstruction) return false;
            LlvmValue defaultValue = whenFalse, newValue = whenTrue;
            if (compare.Predicate == LlvmCmpPredicate.ICmpNe) (defaultValue, newValue) = (newValue, defaultValue);
            if (select is not null) { select.ReplaceAllUsesWith(defaultValue); select.EraseFromParent(); }
            else compare.ReplaceAllUsesWith(defaultValue);
            compare.EraseFromParent();
            var edge = new LlvmBasicBlock(block.Context, "switch.edge", block.Parent, block);
            instruction.AddCase(newCase, edge);
            builder.SetInsertPoint(edge);
            builder.CurrentSource = instruction.Source;
            builder.CreateBr(successor);
            phi.AddIncoming(newValue, edge);
            phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = phi.LogicalType;
            DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, predecessor, edge), new(LlvmCfgUpdateKind.Insert, edge, successor)]);
            Count("conditional.icmp_select.case_added");
            return true;
        }
    }
}
