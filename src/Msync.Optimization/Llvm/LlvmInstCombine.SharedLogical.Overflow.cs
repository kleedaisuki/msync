// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldOrUnsignedUMulOverflowICmp; source OverflowInstAnalysis consumer bridge.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool SharedLogicalZeroInt(LlvmValue value)
            => value is LlvmConstant { IsNullValue: true } || value.Type.IsVectorTy() &&
                value is LlvmConstant constant && constant.GetSplatValue(allowPoison: true) is LlvmConstantInt { IsZero: true };
        private bool IsCheckForZeroAndMulWithOverflow(LlvmValue first, LlvmValue second, bool and, out LlvmUse? operand)
            => LlvmOverflowInstAnalysis.IsCheckForZeroAndMulWithOverflow(first, second, and, out operand, Metrics);
        private static LlvmAPInt? SharedLogicalAPInt(LlvmValue value)
        {
            if (value is LlvmConstantInt integer) return integer.Value;
            return value.Type.IsVectorTy() && value is LlvmConstant constant && constant.GetSplatValue() is LlvmConstantInt splat ? splat.Value : null;
        }
        private LlvmValue? FoldOrUnsignedUMulOverflowICmp(LlvmBinaryOperator operation)
        {
            if (operation.Opcode != LlvmOpcode.Or) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                LlvmValue overflow = orientation == 0 ? operation.Left : operation.Right;
                LlvmValue comparison = orientation == 0 ? operation.Right : operation.Left;
                if (overflow is not LlvmExtractValueInstruction { Indices.Count: 1 } overflowBit || overflowBit.Indices[0] != 1 ||
                    overflowBit.AggregateOperand is not LlvmCallInstruction call || call.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.UMulWithOverflow ||
                    SharedLogicalAPInt(call.GetArgument(1)) is not { } multiplier || multiplier.IsZero ||
                    comparison is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpUgt, HasOneUse: true,
                        Left: LlvmExtractValueInstruction { Indices.Count: 1 } product } compare || product.Indices[0] != 0 || product.AggregateOperand != call ||
                    SharedLogicalAPInt(compare.Right) is not { } bound) continue;
                LlvmValue input = call.GetArgument(0);
                LlvmConstant threshold = LlvmConstantInt.Get(input.Type, bound.UDiv(multiplier));
                Count("logical.unsigned_mul_overflow_bound");
                return Builder.CreateICmp(LlvmCmpPredicate.ICmpUgt, input, threshold);
            }
            return null;
        }
        private LlvmValue? FoldOrOfSelectSmaxToAbs(LlvmBinaryOperator operation)
        {
            if (operation.Opcode != LlvmOpcode.Or) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                LlvmValue selection = orientation == 0 ? operation.Left : operation.Right;
                LlvmValue maximum = orientation == 0 ? operation.Right : operation.Left;
                if (!maximum.HasOneUse || maximum is not LlvmCallInstruction call ||
                    call.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.SMax || !SharedLogicalZeroInt(call.GetArgument(1)) ||
                    selection is not LlvmSelectInstruction { Condition: LlvmICmpInstruction compare } select) continue;
                LlvmValue input = call.GetArgument(0);
                bool Negated(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } subtraction &&
                    SharedLogicalZeroInt(subtraction.Left) && subtraction.Right == input;
                if (compare.Left != input || !SharedLogicalZeroInt(compare.Right)) continue;
                bool matched = compare.Predicate == LlvmCmpPredicate.ICmpSgt && SharedLogicalZeroInt(select.TrueValue) && Negated(select.FalseValue) ||
                    compare.Predicate == LlvmCmpPredicate.ICmpSlt && Negated(select.TrueValue) && SharedLogicalZeroInt(select.FalseValue);
                if (!matched) continue;
                Count("logical.select_smax_to_abs");
                return Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.Abs, input, LlvmConstantInt.GetFalse(input.Context));
            }
            return null;
        }
    }
}
