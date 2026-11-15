// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldFCmpReciprocalAndZero, foldFCmpFNegCommonOp, foldFCmpFSubIntoFCmp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool FCmpHasFlag(LlvmInstruction instruction, LlvmFastMathFlags flag) =>
            instruction is ILlvmFastMathInstruction floating && (floating.FastMathFlags & flag) != 0;

        private static LlvmValue? FCmpNegatedOperand(LlvmValue value)
        {
            if (value is LlvmUnaryOperator { Opcode: LlvmOpcode.FNeg } negation) return negation.Operand;
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmConstantFP zero } subtraction &&
                zero.IsZero && (zero.IsNegative || (subtraction.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0)) return subtraction.Right;
            return null;
        }

        private bool FCmpNeverNaN(LlvmValue value, LlvmInstruction context) =>
            LlvmKnownFPClasses.Compute(value, LlvmFPClassTest.NaN, context, DT, Metrics,
                assumptions: AC, allowEphemerals: SQ.AllowEphemerals).IsKnownNeverNaN();
        private bool FCmpNeverInfinity(LlvmValue value, LlvmInstruction context) =>
            LlvmKnownFPClasses.Compute(value, LlvmFPClassTest.Infinity, context, DT, Metrics,
                assumptions: AC, allowEphemerals: SQ.AllowEphemerals).IsKnownNeverInfinity();

        private LlvmInstruction? FoldFCmpReciprocalAndZero(LlvmFCmpInstruction comparison, LlvmInstruction left, LlvmConstant right)
        {
            Count("fcmp.reciprocal_zero.queries");
            LlvmCmpPredicate predicate = comparison.Predicate;
            if (predicate is not (LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpOle) ||
                right is not LlvmConstantFP { IsZero: true } || !FCmpHasFlag(left, LlvmFastMathFlags.NoInfs) ||
                !FCmpHasFlag(comparison, LlvmFastMathFlags.NoInfs) || left.GetOperand(0) is not LlvmConstantFP constant || constant.IsZero) return null;
            if (constant.IsNegative) predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            Count("fcmp.rewrite.reciprocal_sign_test");
            return NewFCmp(predicate, left.GetOperand(1)!, right, comparison);
        }

        private LlvmInstruction? FoldFCmpFNegCommonOp(LlvmFCmpInstruction comparison)
        {
            LlvmValue left = comparison.Left, right = comparison.Right;
            LlvmCmpPredicate predicate = comparison.Predicate;
            if (FCmpNegatedOperand(left) is not null && FCmpNegatedOperand(right) is null)
            { (left, right) = (right, left); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (FCmpNegatedOperand(right) != left) return null;
            Count("fcmp.rewrite.negation_common_operand");
            return NewFCmp(predicate, left, FCmpZero(left.Type), comparison);
        }

        private LlvmInstruction? FoldFCmpFSubIntoFCmp(LlvmFCmpInstruction comparison, LlvmInstruction subtraction, LlvmConstant right)
        {
            Count("fcmp.subtract.queries");
            LlvmCmpPredicate predicate = comparison.Predicate;
            LlvmValue x = subtraction.GetOperand(0)!, y = subtraction.GetOperand(1)!;
            switch (predicate)
            {
                case LlvmCmpPredicate.FCmpUgt: case LlvmCmpPredicate.FCmpUlt: case LlvmCmpPredicate.FCmpUne:
                case LlvmCmpPredicate.FCmpOeq: case LlvmCmpPredicate.FCmpOge: case LlvmCmpPredicate.FCmpOle:
                    if (!FCmpHasFlag(subtraction, LlvmFastMathFlags.NoNaNs) && !FCmpHasFlag(subtraction, LlvmFastMathFlags.NoInfs) &&
                        !FCmpNeverInfinity(y, comparison) && !FCmpNeverInfinity(x, comparison)) return null;
                    break;
                case LlvmCmpPredicate.FCmpOgt: case LlvmCmpPredicate.FCmpOlt: case LlvmCmpPredicate.FCmpOne:
                case LlvmCmpPredicate.FCmpUeq: case LlvmCmpPredicate.FCmpUge: case LlvmCmpPredicate.FCmpUle:
                    break;
                default: return null;
            }
            if (right is LlvmConstantFP { IsZero: true } && FCmpDenormalsAreIEEE(subtraction.Type))
            {
                ReplaceOperand(comparison, 0, x); ReplaceOperand(comparison, 1, y);
                comparison.FastMathFlags = FCmpHasFlag(subtraction, LlvmFastMathFlags.NoInfs)
                    ? comparison.FastMathFlags | LlvmFastMathFlags.NoInfs : comparison.FastMathFlags & ~LlvmFastMathFlags.NoInfs;
                if (FCmpHasFlag(subtraction, LlvmFastMathFlags.NoNaNs)) comparison.FastMathFlags |= LlvmFastMathFlags.NoNaNs;
                Count("fcmp.rewrite.subtract_zero");
                return comparison;
            }
            if (right is LlvmConstantFP constant && x == right && y is LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } &&
                constant.Value.IsNormal)
            {
                int mantissa = subtraction.Type.GetFPMantissaWidth();
                if (mantissa != -1 && constant.Value.ILogB() < mantissa)
                {
                    comparison.Predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
                    ReplaceOperand(comparison, 0, y); ReplaceOperand(comparison, 1, FCmpZero(subtraction.Type));
                    Count("fcmp.rewrite.integer_step_subtract_constant");
                    return comparison;
                }
            }
            return null;
        }
    }
}
