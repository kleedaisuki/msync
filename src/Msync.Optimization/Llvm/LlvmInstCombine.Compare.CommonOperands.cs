// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// hasNoWrapProblem and matchCommonBinOpOperands.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmAPInt? CommonOperandsInteger(LlvmValue value)
        {
            if (value is LlvmConstantInt integer) return integer.Value;
            return value is LlvmConstant constant && value.Type is LlvmVectorType &&
                constant.GetSplatValue(allowPoison: true) is LlvmConstantInt splat ? splat.Value : null;
        }

        private static bool CommonOperandsNoWrap(LlvmBinaryOperator operation, LlvmCmpPredicate predicate)
        {
            if (operation.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl)
                return predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe ||
                    (predicate is >= LlvmCmpPredicate.ICmpUgt and <= LlvmCmpPredicate.ICmpUle && operation.HasNoUnsignedWrap) ||
                    (predicate is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle && operation.HasNoSignedWrap);
            return operation.Opcode == LlvmOpcode.Or;
        }

        private bool MatchCommonBinOpOperands(LlvmValue first, LlvmValue second, LlvmCmpPredicate predicate,
            out LlvmValue left, out LlvmValue right, LlvmInstructionSimplify.BinaryQuery query)
        {
            Count("instcombine.common_binop.visits");
            left = right = null!;
            if (first is not LlvmBinaryOperator lhs || second is not LlvmBinaryOperator rhs) return false;
            bool lhsNoWrap = CommonOperandsNoWrap(lhs, predicate), rhsNoWrap = CommonOperandsNoWrap(rhs, predicate);
            bool lhsAdd = lhs.Opcode == LlvmOpcode.Add || lhs is { Opcode: LlvmOpcode.Or, IsDisjoint: true };
            bool rhsAdd = rhs.Opcode == LlvmOpcode.Add || rhs is { Opcode: LlvmOpcode.Or, IsDisjoint: true };
            LlvmValue a = lhs.Left, b = lhs.Right, c = rhs.Left, d = rhs.Right;
            if (lhsAdd && rhsAdd)
            {
                if (!lhsNoWrap || !rhsNoWrap || !CommonOperandsCancel(a, b, c, d, out left, out right, out _)) return false;
                Count("instcombine.common_binop.add_cancel");
                return true;
            }
            if (lhs.Opcode != rhs.Opcode) return false;
            if (lhs.Opcode == LlvmOpcode.Sub)
            {
                if (!lhsNoWrap || !rhsNoWrap) return false;
                if (ReferenceEquals(b, d)) { left = a; right = c; }
                else if (ReferenceEquals(a, c)) { left = d; right = b; }
                else return false;
                Count("instcombine.common_binop.sub_cancel");
                return true;
            }
            if (lhs.Opcode != LlvmOpcode.Mul || predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe ||
                !lhsNoWrap || !rhsNoWrap || !CommonOperandsCancel(a, b, c, d, out left, out right, out var common)) return false;
            bool NonZero() => LlvmKnownBitsAnalysis.IsKnownNonZero(common, context: query.Context, tree: query.Tree,
                metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions,
                allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext);
            if (predicate is >= LlvmCmpPredicate.ICmpUgt and <= LlvmCmpPredicate.ICmpUle)
            {
                bool nonZero = NonZero();
                if (nonZero) Count("instcombine.common_binop.unsigned_mul_cancel");
                return nonZero;
            }
            var known = LlvmKnownBitsAnalysis.Compute(common, context: query.Context, tree: query.Tree,
                metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions,
                allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext, layout: query.Layout);
            if (known.IsNonNegative && (known.IsNonZero || NonZero()))
            {
                Count("instcombine.common_binop.positive_mul_cancel");
                return true;
            }
            if (!known.IsNegative) return false;
            (left, right) = (right, left);
            Count("instcombine.common_binop.negative_mul_reverse");
            return true;
        }

        private static bool CommonOperandsCancel(LlvmValue a, LlvmValue b, LlvmValue c, LlvmValue d,
            out LlvmValue left, out LlvmValue right, out LlvmValue common)
        {
            if (ReferenceEquals(a, c)) { common = a; left = b; right = d; return true; }
            if (ReferenceEquals(a, d)) { common = a; left = b; right = c; return true; }
            if (ReferenceEquals(b, c)) { common = b; left = a; right = d; return true; }
            if (ReferenceEquals(b, d)) { common = b; left = a; right = c; return true; }
            left = right = common = null!;
            return false;
        }
    }
}
