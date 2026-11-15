// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// canFreelyInvert, freelyInvert and sinkNotIntoOtherHandOfLogicalOp.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool SharedLogicalMatch(LlvmValue value, bool and, out LlvmValue left, out LlvmValue right)
        {
            left = right = null!;
            if (!value.Type.IsIntOrIntVectorTy(1)) return false;
            if (value is LlvmBinaryOperator binary && binary.Opcode == (and ? LlvmOpcode.And : LlvmOpcode.Or))
            { left = binary.Left; right = binary.Right; return true; }
            if (value is not LlvmSelectInstruction select || select.Condition.Type != select.Type) return false;
            if (and ? SharedLogicalZero(select.FalseValue) : SharedLogicalOne(select.TrueValue))
            { left = select.Condition; right = and ? select.TrueValue : select.FalseValue; return true; }
            return false;
        }
        private static bool SharedLogicalZero(LlvmValue value) => value is LlvmConstant { IsNullValue: true };
        private static bool SharedLogicalOne(LlvmValue value)
            => value is LlvmConstantInt { IsOne: true } || value.Type.IsVectorTy() &&
                value is LlvmConstant constant && constant.GetSplatValue() is LlvmConstantInt { IsOne: true };
        private static bool SharedLogicalAllOnes(LlvmValue value)
            => value is LlvmConstantInt { IsMinusOne: true } || value.Type.IsVectorTy() &&
                value is LlvmConstant constant && constant.GetSplatValue(allowPoison: true) is LlvmConstantInt { IsMinusOne: true };
        private static bool SharedLogicalNot(LlvmValue value, out LlvmValue operand)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } binary)
            {
                if (SharedLogicalAllOnes(binary.Left)) { operand = binary.Right; return true; }
                if (SharedLogicalAllOnes(binary.Right)) { operand = binary.Left; return true; }
            }
            operand = null!; return false;
        }
        private bool CanFreelyInvert(LlvmValue operand, LlvmInstruction ignoredUser)
            => operand is LlvmInstruction instruction && instruction.GetInsertionPointAfterDef() is not null &&
                IsFreeToInvert(instruction, true) && CanFreelyInvertAllUsersOf(instruction, ignoredUser);
        private LlvmValue FreelyInvert(LlvmValue operand, LlvmInstruction ignoredUser)
        {
            var instruction = (LlvmInstruction)operand;
            LlvmInstruction? insertBefore = instruction.GetInsertionPointAfterDef();
            Debug.Assert(insertBefore is not null);
            Builder.SetInsertPoint(insertBefore!);
            LlvmValue complement = Builder.CreateNot(operand);
            BoolCmpAppendName(complement, operand, ".not"u8);
            foreach (LlvmUse use in operand.Uses.ToArray())
                if (use.User != complement) use.Set(complement);
            FreelyInvertAllUsersOf(complement, ignoredUser);
            Count("logical.invert_operand");
            return complement;
        }
        private bool SinkNotIntoOtherHandOfLogicalOp(LlvmInstruction instruction)
        {
            bool and = SharedLogicalMatch(instruction, true, out var left, out var right);
            if (!and && !SharedLogicalMatch(instruction, false, out left, out right)) return false;
            LlvmOpcode opcode = and ? LlvmOpcode.Or : LlvmOpcode.And;
            bool invertRight;
            if (SharedLogicalNot(left, out var leftOperand) && CanFreelyInvert(right, instruction))
            { left = leftOperand; invertRight = true; }
            else if (SharedLogicalNot(right, out var rightOperand) && CanFreelyInvert(left, instruction))
            { right = rightOperand; invertRight = false; }
            else return false;
            LlvmValue invert = invertRight ? right : left;
            LlvmValue kept = invert == right ? left : right;
            if (SharedLogicalNot(kept, out var keptOperand) && keptOperand == invert) return false;
            if (!CanFreelyInvertAllUsersOf(instruction, null)) return false;
            if (invertRight) right = FreelyInvert(right, instruction);
            else left = FreelyInvert(left, instruction);
            Builder.SetInsertPoint(instruction.GetInsertionPointAfterDef()!);
            LlvmValue replacement = instruction is LlvmBinaryOperator
                ? Builder.CreateBinOp(opcode, left, right)
                : opcode == LlvmOpcode.And
                    ? Builder.CreateSelect(left, right, LlvmConstant.GetNullValue(right.Type))
                    : Builder.CreateSelect(left, LlvmConstant.GetAllOnesValue(right.Type), right);
            BoolCmpAppendName(replacement, instruction, ".not"u8);
            ReplaceInstUsesWith(instruction, replacement);
            FreelyInvertAllUsersOf(replacement, null);
            Count("logical.sink_not_other_operand");
            return true;
        }
    }
}
