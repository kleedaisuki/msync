// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombiner.h
// shouldAvoidAbsorbingNotIntoSelect/canFreelyInvertAllUsersOf;
// InstructionCombining.cpp freelyInvertAllUsersOf.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MatchScalarLogical(LlvmValue value, bool and, out LlvmValue left, out LlvmValue right)
        {
            left = right = null!;
            if (!value.Type.IsIntegerTy(1)) return false;
            if (value is LlvmBinaryOperator binary && binary.Opcode == (and ? LlvmOpcode.And : LlvmOpcode.Or))
            { left = binary.Left; right = binary.Right; return true; }
            if (value is not LlvmSelectInstruction select || !ReferenceEquals(select.Condition.Type, select.Type)) return false;
            if (and && select.FalseValue is LlvmConstantInt { IsZero: true })
            { left = select.Condition; right = select.TrueValue; return true; }
            if (!and && select.TrueValue is LlvmConstantInt { IsOne: true })
            { left = select.Condition; right = select.FalseValue; return true; }
            return false;
        }
        private static bool ShouldAvoidAbsorbingNotIntoSelect(LlvmSelectInstruction select) =>
            MatchScalarLogical(select, true, out _, out _) || MatchScalarLogical(select, false, out _, out _);
        private static bool CanFreelyInvertAllUsersOf(LlvmInstruction value, LlvmValue? ignored)
        {
            foreach (LlvmUse use in value.Uses)
            {
                if (ReferenceEquals(use.User, ignored)) continue;
                switch (use.User)
                {
                    case LlvmSelectInstruction select:
                        if (use.OperandNo != 0 || ShouldAvoidAbsorbingNotIntoSelect(select)) return false;
                        break;
                    case LlvmCondBrInstruction: break;
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor:
                        if (!Negator.MatchNot(xor, out _)) return false;
                        break;
                    default: return false;
                }
            }
            return true;
        }
        private void FreelyInvertAllUsersOf(LlvmValue value, LlvmValue? ignored)
        {
            // RAUW can alter the use list; source uses an early-increment range.
            foreach (LlvmUser user in value.Users.ToArray())
            {
                if (ReferenceEquals(user, ignored)) continue;
                switch (user)
                {
                    case LlvmSelectInstruction select:
                        (select.TrueValue, select.FalseValue) = (select.FalseValue, select.TrueValue);
                        break;
                    case LlvmCondBrInstruction branch:
                        branch.SwapSuccessors();
                        break;
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor:
                        ReplaceInstUsesWith(xor, value);
                        Worklist.Add(xor);
                        break;
                    default:
                        throw new InvalidOperationException("Freely-inverted users are inconsistent with the source eligibility query.");
                }
                Count("invert.users_rewritten");
            }
            // LLVM profile/debug metadata and BPI are absent in this source model.
        }
    }
}
