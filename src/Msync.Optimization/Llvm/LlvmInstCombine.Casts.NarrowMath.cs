// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// narrowMathIfNoOverflow (2761..2811).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? NarrowMathIfNoOverflow(LlvmBinaryOperator instruction)
        {
            Count("casts.narrow_math.queries");
            LlvmValue first = instruction.Left, second = instruction.Right;
            if (instruction.Opcode == LlvmOpcode.Sub) (first, second) = (second, first);
            if (first is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt or LlvmOpcode.ZExt } extension) return null;
            var opcode = extension.Opcode;
            bool signed = opcode == LlvmOpcode.SExt;
            var left = extension.Operand;
            LlvmValue right;
            if (second is LlvmCastInstruction other && other.Opcode == opcode && ReferenceEquals(left.Type, other.Operand.Type)
                && (first.HasOneUse || second.HasOneUse)) right = other.Operand;
            else
            {
                if (!first.HasOneUse || second is not LlvmConstant constant) return null;
                if (GetLosslessInvCast(constant, left.Type, opcode, DL) is not { } narrowed) return null;
                right = narrowed;
            }
            if (instruction.Opcode == LlvmOpcode.Sub) (left, right) = (right, left);
            if (!WillNotOverflow(instruction.Opcode, left, right, instruction, signed)) return null;
            var value = Builder.CreateBinOp(instruction.Opcode, left, right, "narrow");
            if (value is LlvmBinaryOperator binary)
            {
                if (signed) binary.HasNoSignedWrap = true;
                else binary.HasNoUnsignedWrap = true;
            }
            Count("casts.narrow_math.rewrites");
            return new LlvmCastInstruction(opcode, value, instruction.Type);
        }
    }
}
