// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SimplifyCFG.cpp performBranchToCommonDestFolding, addPredecessorToBlock,
// cloneInstructionsIntoPredecessorBlockAndUpdateSSAUses and createLogicalOp;
// BasicBlockUtils.cpp InvertBranch; IRBuilder.h / ConstantFolder.h.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    private sealed partial class CommonDestinationFolder
    {
        private bool Perform(LlvmCondBrInstruction predecessor)
        {
            var recipe = CommonDestination(branch, predecessor)!.Value;
            if (trace is null) Rewrite(predecessor, recipe);
            else trace.Run("optimizer.llvm-simplifycfg.fold-common-destination.rewrite", (Folder: this, predecessor, recipe),
                static state => state.Folder.Rewrite(state.predecessor, state.recipe), Attributes(predecessor));
            Count("branches.folded");
            return true;
        }

        private void Rewrite(LlvmCondBrInstruction predecessor, CommonDestinationRecipe recipe)
        {
            if (recipe.InvertPredecessor) InvertPredecessor(predecessor);
            LlvmBasicBlock predecessorBlock = predecessor.Parent!;
            LlvmBasicBlock unique = predecessor.GetSuccessor(0) == Block ? branch.GetSuccessor(0) : branch.GetSuccessor(1);
            AddPredecessor(unique, predecessorBlock);
            predecessor.SetSuccessor(predecessor.GetSuccessor(0) != Block ? 1 : 0, unique);
            updater?.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, predecessorBlock, unique),
                new(LlvmCfgUpdateKind.Delete, predecessorBlock, Block)]);
            Dictionary<LlvmValue, LlvmValue> mapping = [];
            CloneIntoPredecessor(predecessor, mapping);
            predecessor.Condition = CreateLogical(recipe.Opcode, predecessor.Condition, mapping[branch.Condition], predecessor);
        }

        private void AddPredecessor(LlvmBasicBlock successor, LlvmBasicBlock predecessor)
        {
            for (var instruction = successor.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                int original = phi.GetBasicBlockIndex(Block);
                phi.AddIncoming(phi.GetIncomingValue(original), predecessor);
                phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = phi.GetOperandUse(original).LogicalType;
                Count("phis.incoming_added");
            }
        }

        private void CloneIntoPredecessor(LlvmCondBrInstruction predecessor, Dictionary<LlvmValue, LlvmValue> mapping)
        {
            LlvmBasicBlock predecessorBlock = predecessor.Parent!;
            foreach (LlvmInstruction original in Block.Instructions)
            {
                if (original.IsTerminator) continue;
                LlvmInstruction clone = original.Clone();
                predecessorBlock.InsertBefore(clone, predecessor);
                for (int index = 0; index < clone.OperandCount; ++index)
                {
                    LlvmUse operand = clone.GetOperandUse(index);
                    if (mapping.TryGetValue(operand.Value!, out LlvmValue? mapped)) operand.Set(mapped);
                }
                // The represented IR has no UB-implying parameter attributes or
                // instruction metadata; poison-generating arithmetic flags are
                // retained, exactly as dropUBImplyingAttrsAndMetadata requires.
                clone.TakeName(original);
                original.SetName(AppendName(clone.NameBytes.Span, ".old"u8));
                mapping[original] = clone;
                Count("instructions.cloned");
                for (LlvmUse? use = original.FirstUse; use is not null;)
                {
                    LlvmUse? next = use.Next;
                    var user = (LlvmInstruction)use.User;
                    if (user is LlvmPhiInstruction phi)
                    {
                        LlvmBasicBlock incoming = phi.GetIncomingBlock(use.OperandNo);
                        if (incoming != Block)
                        {
                            Debug.Assert(incoming == predecessorBlock);
                            use.Set(clone);
                            Count("phis.liveout_uses_remapped");
                        }
                    }
                    else Debug.Assert(user.Parent == Block && original.ComesBefore(user));
                    use = next;
                }
            }
        }

        private void InvertPredecessor(LlvmCondBrInstruction predecessor)
        {
            LlvmValue condition = predecessor.Condition;
            if (condition.HasOneUse && condition is LlvmCmpInstruction comparison)
                comparison.Predicate = LlvmCmpInstruction.GetInversePredicate(comparison.Predicate);
            else
            {
                var one = LlvmConstantInt.GetTrue(condition.Context);
                condition = CreateBinary(LlvmOpcode.Xor, condition, one, predecessor,
                    AppendName(condition.NameBytes.Span, ".not"u8));
            }
            predecessor.Condition = condition;
            predecessor.SwapSuccessors();
            Count("predecessors.inverted");
        }

        private LlvmValue CreateLogical(LlvmOpcode opcode, LlvmValue lhs, LlvmValue rhs, LlvmCondBrInstruction before)
        {
            if (LlvmValueTracking.ImpliesPoison(rhs, lhs, Metrics))
                return CreateBinary(opcode, lhs, rhs, before, "or.cond"u8);
            LlvmValue whenTrue = opcode == LlvmOpcode.And ? rhs : LlvmConstantInt.GetTrue(lhs.Context);
            LlvmValue whenFalse = opcode == LlvmOpcode.And ? LlvmConstantInt.GetFalse(lhs.Context) : rhs;
            if (lhs is LlvmConstant condition && whenTrue is LlvmConstant t && whenFalse is LlvmConstant f &&
                LlvmConstantFolding.ConstantFoldSelectInstruction(condition, t, f, Metrics) is { } folded) return folded;
            var select = new LlvmSelectInstruction(lhs, whenTrue, whenFalse)
            { Source = branch.Source, LogicalType = lhs.LogicalType };
            before.Parent!.InsertBefore(select, before);
            select.SetName("or.cond");
            Count("logical.select_created");
            return select;
        }

        private LlvmValue CreateBinary(LlvmOpcode opcode, LlvmValue lhs, LlvmValue rhs,
            LlvmCondBrInstruction before, ReadOnlySpan<byte> name)
        {
            if (lhs is LlvmConstant l && rhs is LlvmConstant r &&
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, l, r, before.Parent!.Parent!.Parent!.DataLayout, Metrics) is { } folded)
                return folded;
            var binary = new LlvmBinaryOperator(opcode, lhs, rhs) { Source = branch.Source, LogicalType = lhs.LogicalType };
            before.Parent!.InsertBefore(binary, before);
            binary.SetName(name);
            Count("logical.binary_created");
            return binary;
        }

        private static byte[] AppendName(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix)
        {
            byte[] result = new byte[prefix.Length + suffix.Length];
            prefix.CopyTo(result);
            suffix.CopyTo(result.AsSpan(prefix.Length));
            return result;
        }
    }
}
