// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitFreeze / freezeOtherUses.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitFreeze(LlvmFreezeInstruction instruction)
        {
            LlvmValue operand = instruction.Operand;
            if (LlvmInstructionSimplify.SimplifyInstructionWithOperands(instruction, [operand], Query(instruction)) is { } simplified)
                return ReplaceInstUsesWith(instruction, simplified);
            if (operand is LlvmPhiInstruction phi)
            {
                if (FoldOpIntoPhi(instruction, phi) is { } folded) return folded;
                if (FoldFreezeIntoRecurrence(instruction, phi) is { } recurrence) return recurrence;
            }
            if (PushFreezeToPreventPoisonFromPropagating(instruction) is { } pushed)
                return ReplaceInstUsesWith(instruction, pushed);
            if (operand is LlvmUndefValue)
            {
                if (IsUsedWithinShuffleVector(instruction)) return null;
                Count("freeze.undef-chosen");
                return ReplaceInstUsesWith(instruction, GetFreezeUndefReplacement(instruction));
            }
            if (operand is LlvmConstant constant && constant.ContainsUndefOrPoisonElement() &&
                !constant.ContainsConstantExpression() && GetFreezeVectorReplacement(constant) is { } replacement)
                return ReplaceInstUsesWith(instruction, replacement);
            return FreezeOtherUses(instruction) ? instruction : null;
        }

        private static LlvmConstant? GetFreezeVectorReplacement(LlvmConstant constant)
        {
            if (constant.Type is not LlvmFixedVectorType vector) return null;
            LlvmConstant? best = null;
            for (uint index = 0; index < vector.NumElements; ++index)
            {
                LlvmConstant lane = constant.GetAggregateElement(index)!;
                if (lane is LlvmUndefValue) continue;
                best = lane;
                break;
            }
            best ??= LlvmConstant.GetNullValue(vector.ElementType);
            return LlvmConstant.ReplaceUndefsWith(constant, best);
        }

        private LlvmValue GetFreezeUndefReplacement(LlvmFreezeInstruction instruction)
        {
            LlvmValue zero = LlvmConstant.GetNullValue(instruction.Type);
            LlvmValue? best = null;
            foreach (LlvmUser user in instruction.Users)
            {
                Count("freeze.undef-users.examined");
                LlvmValue value = zero;
                if (user is LlvmBinaryOperator { Opcode: LlvmOpcode.Or })
                    value = LlvmConstant.GetAllOnesValue(instruction.Type);
                else if (user is LlvmSelectInstruction { TrueValue: LlvmConstant } select && select.Condition == instruction)
                    value = LlvmConstantInt.GetTrue(instruction.Type);
                else if (user is LlvmSelectInstruction otherSelect &&
                    (otherSelect.TrueValue == instruction || otherSelect.FalseValue == instruction))
                {
                    value = otherSelect.TrueValue == instruction ? otherSelect.FalseValue : otherSelect.TrueValue;
                    if (value == instruction || !LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(value, DT,
                        instruction, Metrics, assumptions: AC)) value = zero;
                }
                else if (user is LlvmPhiInstruction phi && PickCommonConstantFromPhi(phi) is { } common) value = common;
                if (best is null) best = value;
                else if (best != value) best = zero;
            }
            Debug.Assert(best is not null && best != instruction);
            return best!;
        }

        private static LlvmConstant? PickCommonConstantFromPhi(LlvmPhiInstruction phi)
        {
            LlvmConstant? best = null;
            foreach (LlvmValue? value in phi.Operands)
            {
                if (value is LlvmFreezeInstruction { Operand: LlvmUndefValue }) continue;
                if (value is not LlvmConstant constant || !LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(constant)) return null;
                if (best is not null && best != constant) return null;
                best = constant;
            }
            return best;
        }

        private bool FreezeOtherUses(LlvmFreezeInstruction instruction)
        {
            LlvmValue operand = instruction.Operand;
            if (operand is LlvmConstant || operand.HasOneUse) return false;
            LlvmInstruction? before;
            if (operand is LlvmArgument)
            {
                before = F.BasicBlocks[0].FirstInstruction;
                while (before is LlvmPhiInstruction or LlvmAllocaInstruction) before = before.Next;
            }
            else before = ((LlvmInstruction)operand).GetInsertionPointAfterDef();
            if (before is null) return false;
            bool changed = false;
            if (instruction != before)
            {
                instruction.RemoveFromParent();
                before.Parent!.InsertBefore(instruction, before);
                changed = true;
                Count("freeze.moved-after-definition");
            }
            List<LlvmInstruction> users = [];
            foreach (LlvmUse use in operand.Uses.ToArray())
            {
                if (!DT.Dominates(instruction, use)) continue;
                use.Set(instruction);
                users.Add((LlvmInstruction)use.User);
                changed = true;
                Count("freeze.dominated-uses.replaced");
            }
            foreach (LlvmInstruction user in users)
            {
                Worklist.PushUsersToWorkList(user);
                Worklist.Push(user);
            }
            return changed;
        }

        private static bool IsUsedWithinShuffleVector(LlvmValue value)
        {
            foreach (LlvmUser user in value.Users)
            {
                if (user is LlvmInstruction { Opcode: LlvmOpcode.ShuffleVector }) return true;
                if (user is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast && cast.Operand == value &&
                    IsUsedWithinShuffleVector(user)) return true;
            }
            return false;
        }
    }
}
