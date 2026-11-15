// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 201–304.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmOpcode? IsEliminableCastPair(LlvmCastInstruction first, LlvmCastInstruction second)
        {
            Count("casts.cast_pair.queries");
            var source = first.Operand.Type;
            var destination = second.Type;
            var opcode = LlvmCastInstruction.IsEliminableCastPair(first.Opcode, second.Opcode, source, first.Type, destination, DL);
            if (opcode == LlvmOpcode.IntToPtr && source != DL.GetIntPtrType(destination.Context, destination.GetScalarType().GetPointerAddressSpace())) return null;
            if (opcode == LlvmOpcode.PtrToInt && destination != DL.GetIntPtrType(source.Context, source.GetScalarType().GetPointerAddressSpace())) return null;
            return opcode;
        }
        private LlvmInstruction? CommonCastTransforms(LlvmCastInstruction instruction)
        {
            Count("casts.common.queries");
            var source = instruction.Operand;
            if (LlvmInstructionSimplify.SimplifyCast(instruction.Opcode, source, instruction.Type, Query(instruction)) is { } simplified)
            {
                Count("casts.common.simplified");
                return ReplaceInstUsesWith(instruction, simplified);
            }
            if (source is LlvmCastInstruction inner && IsEliminableCastPair(inner, instruction) is { } opcode)
            {
                Count("casts.cast_pair.rewrites");
                return new LlvmCastInstruction(opcode, inner.Operand, instruction.Type);
            }
            if (source is LlvmSelectInstruction select)
            {
                var condition = select.Condition;
                bool sameOperandType = condition is LlvmCmpInstruction || condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc };
                if (!sameOperandType || ((LlvmInstruction)condition).GetOperand(0)!.Type != select.Type ||
                    instruction.Opcode == LlvmOpcode.Trunc && ShouldChangeType(source.Type, instruction.Type))
                    if (instruction.Opcode != LlvmOpcode.BitCast || !instruction.Type.IsVectorTy() && !source.Type.IsVectorTy())
                        if (FoldOpIntoSelect(instruction, select) is { } folded) return folded;
            }
            if (source is LlvmPhiInstruction phi &&
                (!source.Type.IsIntegerTy() || !instruction.Type.IsIntegerTy() || ShouldChangeType(source.Type, instruction.Type)))
                return FoldOpIntoPhi(instruction, phi);
            return null;
        }
    }
}
