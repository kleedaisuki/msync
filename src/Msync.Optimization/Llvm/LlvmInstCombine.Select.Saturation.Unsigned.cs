// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// canonicalizeSaturatedSubtractUnsigned/canonicalizeSaturatedAddUnsigned.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue SaturationIntrinsic(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right, string reason)
        {
            if (Metrics is not null) Count("select.saturation." + reason);
            return Builder.CreateIntrinsic(kind, [left, right]);
        }
        private static bool SaturationSub(LlvmValue value, LlvmValue left, LlvmValue right) =>
            value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub && sub.Left == left && sub.Right == right;
        private static bool SaturationAdd(LlvmValue value, LlvmValue left, LlvmValue right, bool commutable = false) =>
            value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add &&
            (add.Left == left && add.Right == right || commutable && add.Left == right && add.Right == left);
        private static bool SaturationAddConstant(LlvmValue value, LlvmValue left, out LlvmAPInt constant)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt integer } add && add.Left == left)
            { constant = integer.Value; return true; }
            constant = default; return false;
        }
        private static bool SaturationSpecific(LlvmValue value, LlvmAPInt constant) => value is LlvmConstantInt integer && integer.Value == constant;
        private LlvmValue? CanonicalizeSaturatedSubtractUnsigned(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            var predicate = compare.Predicate; var a = compare.Left; var b = compare.Right;
            Count("select.saturation.usub_candidates");
            if (SelectIsZero(whenTrue)) { predicate = LlvmCmpInstruction.GetInversePredicate(predicate); (whenTrue,whenFalse) = (whenFalse,whenTrue); }
            if (!SelectIsZero(whenFalse)) return null;
            if (predicate == LlvmCmpPredicate.ICmpNe)
            {
                if (SelectIsZero(b) && whenTrue is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add && add.Left == a && SelectIsOnes(add.Right))
                    return SaturationIntrinsic(LlvmIntrinsicKind.USubSat, a, LlvmConstantInt.Get((LlvmIntegerType)a.Type, 1), "usub_decrement");
                return null;
            }
            if (predicate is not (>= LlvmCmpPredicate.ICmpUgt and <= LlvmCmpPredicate.ICmpUle)) return null;
            if (predicate is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUlt)
            { (a,b) = (b,a); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            bool negative = SaturationSub(whenTrue, b, a) || a is LlvmConstantInt ac && SaturationAddConstant(whenTrue, b, out var av) && av == -ac.Value;
            if (!negative && !SaturationSub(whenTrue, a, b) &&
                !(b is LlvmConstantInt bc && SaturationAddConstant(whenTrue, a, out var bv) && bv == -bc.Value)) return null;
            if (negative && !whenTrue.HasOneUse && !compare.HasOneUse)
            { Count("select.saturation.usub_negative_extra_instructions"); return null; }
            var result = SaturationIntrinsic(LlvmIntrinsicKind.USubSat, a, b, negative ? "usub_negative" : "usub");
            return negative ? Builder.CreateNeg(result) : result;
        }
        private LlvmValue? CanonicalizeSaturatedAddUnsigned(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            var a = compare.Left; var b = compare.Right; var predicate = compare.Predicate;
            Count("select.saturation.uadd_candidates");
            if (SelectIsOnes(whenFalse)) { (whenTrue,whenFalse) = (whenFalse,whenTrue); predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
            if (!SelectIsOnes(whenTrue)) return null;
            if (predicate == LlvmCmpPredicate.ICmpEq)
            {
                if (whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add && add.Left == a && SelectIsOne(add.Right) && SelectIsOnes(b))
                    return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, a, LlvmConstantInt.Get((LlvmIntegerType)a.Type, 1), "uadd_increment");
                return null;
            }
            if (SaturationAddConstant(whenFalse, a, out var c))
            {
                var one = new LlvmAPInt(c.BitWidth, 1);
                if (predicate is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUgt && SaturationSpecific(b, ~c))
                    return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, a, LlvmConstantInt.Get(a.Context, c), "uadd_complement_threshold");
                if (predicate == LlvmCmpPredicate.ICmpUgt && SaturationSpecific(b, ~c - one) && !c.IsAllOnes)
                    return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, a, LlvmConstantInt.Get(a.Context, c), "uadd_strict_threshold");
                if (predicate == LlvmCmpPredicate.ICmpUge && SaturationSpecific(b, -c) && !c.IsZero)
                    return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, a, LlvmConstantInt.Get(a.Context, c), "uadd_nonstrict_threshold");
            }
            if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge)
            { (a,b) = (b,a); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (predicate is not (LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle)) return null;
            if (SelectMatchNot(a, out var x) && SaturationAdd(whenFalse, x, b, true))
                return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, x, b, "uadd_complement_compare");
            if (whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } falseAdd &&
                (SelectMatchNot(falseAdd.Left, out var leftNot) && leftNot == a && falseAdd.Right == b ||
                 SelectMatchNot(falseAdd.Right, out var rightNot) && rightNot == a && falseAdd.Left == b))
                return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, falseAdd.Left, falseAdd.Right, "uadd_complement_sum");
            if (predicate == LlvmCmpPredicate.ICmpUlt && a is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } conditionAdd)
            {
                LlvmValue? y = conditionAdd.Left == b ? conditionAdd.Right : conditionAdd.Right == b ? conditionAdd.Left : null;
                if (y is not null && SaturationAdd(whenFalse, b, y, true))
                    return SaturationIntrinsic(LlvmIntrinsicKind.UAddSat, b, y, "uadd_wrap_compare");
            }
            return null;
        }
    }
}
