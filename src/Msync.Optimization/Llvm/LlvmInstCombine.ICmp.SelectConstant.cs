// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 3408–3508.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool MatchThreeWayIntCompare(LlvmSelectInstruction select, out LlvmValue left, out LlvmValue right,
            out LlvmConstantInt less, out LlvmConstantInt equal, out LlvmConstantInt greater)
        {
            left = right = null!; less = equal = greater = null!;
            if (select.Condition is not LlvmICmpInstruction first || !ICmpIsEquality(first.Predicate)) return false;
            left = first.Left; right = first.Right;
            var equalValue = first.Predicate == LlvmCmpPredicate.ICmpEq ? select.TrueValue : select.FalseValue;
            var unequalValue = first.Predicate == LlvmCmpPredicate.ICmpEq ? select.FalseValue : select.TrueValue;
            if (equalValue is not LlvmConstantInt eq || unequalValue is not LlvmSelectInstruction
                { Condition: LlvmICmpInstruction second, TrueValue: LlvmConstantInt lt, FalseValue: LlvmConstantInt gt }) return false;
            equal = eq; less = lt; greater = gt;
            var left2 = second.Left; var right2 = second.Right; var predicate = second.Predicate;
            if (left2 != left) { (left2, right2) = (right2, left2); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (left2 != left) return false;
            if (predicate == LlvmCmpPredicate.ICmpSgt && right2 is LlvmConstant constant)
            {
                var flipped = LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, constant, second.SameSign, Metrics);
                if (!flipped.HasValue) return false;
                right2 = flipped.Value.Constant;
                (less, greater) = (greater, less);
                predicate = LlvmCmpPredicate.ICmpSlt;
            }
            return predicate == LlvmCmpPredicate.ICmpSlt && right == right2;
        }

        private LlvmInstruction? FoldICmpSelectConstant(LlvmICmpInstruction compare, LlvmSelectInstruction select, LlvmConstantInt constant)
        {
            if (!compare.HasOneUse || !MatchThreeWayIntCompare(select, out var left, out var right,
                out var less, out var equal, out var greater)) return null;
            LlvmValue condition = LlvmConstantInt.GetFalse(compare.Context);
            if (LlvmConstantFolding.CompareInteger(less.Value, constant.Value, compare.Predicate))
                condition = Builder.CreateOr(condition, Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, left, right));
            if (LlvmConstantFolding.CompareInteger(equal.Value, constant.Value, compare.Predicate))
                condition = Builder.CreateOr(condition, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, left, right));
            if (LlvmConstantFolding.CompareInteger(greater.Value, constant.Value, compare.Predicate))
                condition = Builder.CreateOr(condition, Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, left, right));
            Count("icmp.select.three_way");
            return ReplaceInstUsesWith(compare, condition);
        }
    }
}
