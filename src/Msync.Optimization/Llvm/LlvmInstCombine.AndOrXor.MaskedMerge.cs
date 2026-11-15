// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineAndOrXor.cpp visitMaskedMerge (5022–5058).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitMaskedMerge(LlvmBinaryOperator instruction)
        {
            Count("instcombine.logic.masked_merge.queries");
            if (!MatchMaskedMerge(instruction, out LlvmValue baseline, out LlvmValue other,
                out LlvmBinaryOperator difference, out LlvmValue mask)) return null;
            if (LogicNot(mask, out LlvmValue uninvertedMask))
            {
                LlvmValue selected = Builder.CreateAnd(difference, uninvertedMask);
                Count("instcombine.logic.masked_merge.mask_deinverted");
                return new LlvmBinaryOperator(LlvmOpcode.Xor, selected, other);
            }
            if (!difference.HasOneUse || mask is not LlvmConstant constant) return null;
            constant = LlvmConstant.ReplaceUndefsWith(constant, LlvmConstant.GetAllOnesValue(constant.Type.GetScalarType()));
            LlvmValue left = Builder.CreateAnd(other, constant);
            LlvmValue inverse = Builder.CreateNot(constant);
            LlvmValue right = Builder.CreateAnd(baseline, inverse);
            Count("instcombine.logic.masked_merge.constant_unfolded");
            return new LlvmBinaryOperator(LlvmOpcode.Or, left, right);
        }

        private static bool MatchMaskedMerge(LlvmBinaryOperator instruction, out LlvmValue baseline,
            out LlvmValue other, out LlvmBinaryOperator difference, out LlvmValue mask)
        {
            if (instruction.Opcode != LlvmOpcode.Xor)
            {
                baseline = other = mask = null!;
                difference = null!;
                return false;
            }
            for (int outer = 0; outer < 2; ++outer)
            {
                baseline = outer == 0 ? instruction.Left : instruction.Right;
                LlvmValue candidate = outer == 0 ? instruction.Right : instruction.Left;
                if (candidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and) continue;
                for (int inner = 0; inner < 2; ++inner)
                {
                    LlvmValue xorCandidate = inner == 0 ? and.Left : and.Right;
                    if (xorCandidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor) continue;
                    if (xor.Left == baseline) other = xor.Right;
                    else if (xor.Right == baseline) other = xor.Left;
                    else continue;
                    difference = xor;
                    mask = inner == 0 ? and.Right : and.Left;
                    return true;
                }
            }
            baseline = other = mask = null!;
            difference = null!;
            return false;
        }
    }
}
