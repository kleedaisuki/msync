// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// isNeutralValue and OptimizeOverflowCheck (computeOverflow is shared).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool OptimizeOverflowCheck(LlvmOpcode opcode, bool signed, LlvmValue left,
            LlvmValue right, LlvmInstruction original, out LlvmValue result, out LlvmConstant overflow)
        {
            Count("instcombine.overflow_check.visits");
            if ((original is LlvmBinaryOperator { IsCommutative: true } ||
                original is LlvmCmpInstruction { IsCommutative: true }) && left is LlvmConstant && right is not LlvmConstant)
                (left, right) = (right, left);
            Builder.SetInsertPoint(original);
            LlvmType overflowType = LlvmType.GetInt1Ty(left.Context);
            if (left.Type is LlvmVectorType vector)
                overflowType = LlvmVectorType.Get(overflowType, vector.ElementCount);
            bool neutralZero = ICmpConstantMatches(right, value => value.IsZero);
            bool neutralOne = ICmpConstantMatches(right, value => value.IsOne);
            bool isNeutral = opcode switch
            {
                LlvmOpcode.Add or LlvmOpcode.Sub => neutralZero,
                LlvmOpcode.Mul => !(left.Type.IsIntOrIntVectorTy(1) && signed) && neutralOne,
                _ => throw new InvalidOperationException("The source overflow query expects add, sub or mul.")
            };
            if (isNeutral)
            {
                result = left;
                overflow = LlvmConstantInt.GetFalse(overflowType);
                Count("instcombine.overflow_check.neutral");
                return true;
            }
            var outcome = ComputeOverflow(opcode, signed, left, right, original);
            if (outcome == LlvmOverflowResult.MayOverflow)
            {
                result = null!; overflow = null!;
                Count("instcombine.overflow_check.may_overflow");
                return false;
            }
            result = Builder.CreateBinary(opcode, left, right);
            result.TakeName(original);
            bool never = outcome == LlvmOverflowResult.NeverOverflows;
            overflow = LlvmConstantInt.GetBool(overflowType, !never);
            if (never && result is LlvmBinaryOperator operation)
            {
                operation.HasNoSignedWrap = signed;
                operation.HasNoUnsignedWrap = !signed;
            }
            Count(never ? "instcombine.overflow_check.never" : "instcombine.overflow_check.always");
            return true;
        }
    }
}
