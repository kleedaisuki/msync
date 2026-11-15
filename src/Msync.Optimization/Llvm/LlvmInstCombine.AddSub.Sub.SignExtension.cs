// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 3079–3129.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? SubSignExtendedAdds(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (SubSExtLike(right, out var x) && SubSExtLike(left, out var sum) && SubNSW(sum) &&
                SubCommuted(sum, LlvmOpcode.Add, x, out var y))
                return SubReplace(instruction, Builder.CreateSExt(y, instruction.Type), "sext_sum_minus_sext_operand");
            if (!SubSExtLike(left, out var first) || !SubSExtLike(right, out var second) || !SubNSW(first) || !SubNSW(second) ||
                !SubBinary(first, LlvmOpcode.Add, out var firstLeft, out var firstRight)) return null;
            LlvmValue otherFirst, otherSecond;
            if (SubCommuted(second, LlvmOpcode.Add, firstLeft, out otherSecond)) otherFirst = firstRight;
            else if (SubCommuted(second, LlvmOpcode.Add, firstRight, out otherSecond)) otherFirst = firstLeft;
            else return null;
            uint newInstructions = (otherFirst is LlvmConstant ? 0u : 1u) + (otherSecond is LlvmConstant ? 0u : 1u);
            uint deadInstructions = left.HasOneUse ? 1u + (first.HasOneUse ? 1u : 0u) : 0u;
            if (right.HasOneUse) deadInstructions += 1u + (second.HasOneUse ? 1u : 0u);
            Count("addsub.sub.sext_add.old_instructions", deadInstructions);
            Count("addsub.sub.sext_add.new_instructions", newInstructions);
            if (deadInstructions < newInstructions) return null;
            var firstExtended = Builder.CreateSExt(otherFirst, instruction.Type);
            var secondExtended = Builder.CreateSExt(otherSecond, instruction.Type);
            return SubReplace(instruction, Builder.CreateSub(firstExtended, secondExtended, nsw: instruction.HasNoSignedWrap), "sext_sums_common_operand");
        }
    }
}
