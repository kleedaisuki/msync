// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// canonicalizeSaturatedSubtractSigned/canonicalizeSaturatedAddSigned and dispatch.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? CanonicalizeSaturatedSubtract(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse) =>
            CanonicalizeSaturatedSubtractUnsigned(compare, whenTrue, whenFalse) ?? CanonicalizeSaturatedSubtractSigned(compare, whenTrue, whenFalse);
        private LlvmValue? CanonicalizeSaturatedSubtractSigned(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            var predicate = compare.Predicate; Count("select.saturation.ssub_candidates");
            if (predicate == LlvmCmpPredicate.ICmpNe) { predicate = LlvmCmpPredicate.ICmpEq; (whenTrue,whenFalse) = (whenFalse,whenTrue); }
            if (predicate == LlvmCmpPredicate.ICmpEq && compare.Right is LlvmConstantInt { Value.IsMinSignedValue: true } &&
                whenTrue is LlvmConstantInt { Value.IsMaxSignedValue: true } && SelectMatchNeg(whenFalse, out var negative) && negative == compare.Left)
                return SaturationIntrinsic(LlvmIntrinsicKind.SSubSat, LlvmConstant.GetNullValue(compare.Left.Type), compare.Left, "ssub_negate_minimum");
            return null;
        }
        private LlvmValue? CanonicalizeSaturatedAdd(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (!compare.HasOneUse) { Count("select.saturation.add_compare_multiple_uses"); return null; }
            return CanonicalizeSaturatedAddUnsigned(compare, whenTrue, whenFalse) ?? CanonicalizeSaturatedAddSigned(compare, whenTrue, whenFalse);
        }
        private LlvmValue? CanonicalizeSaturatedAddSigned(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            var a = compare.Left; var b = compare.Right; var predicate = compare.Predicate;
            Count("select.saturation.sadd_candidates");
            if (whenFalse is LlvmConstantInt fc && (fc.Value.IsMaxSignedValue || fc.Value.IsMinSignedValue))
            { (whenTrue,whenFalse) = (whenFalse,whenTrue); predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
            if (whenTrue is not LlvmConstantInt saturation || !(saturation.Value.IsMaxSignedValue || saturation.Value.IsMinSignedValue)) return null;
            var sat = saturation.Value; bool isMax = sat.IsMaxSignedValue;
            if (predicate == LlvmCmpPredicate.ICmpEq && b == whenTrue)
            {
                if (isMax && whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add && add.Left == a && SelectIsOne(add.Right))
                    return SaturationIntrinsic(LlvmIntrinsicKind.SAddSat, a, LlvmConstantInt.Get((LlvmIntegerType)a.Type, 1), "sadd_increment");
                if (!isMax && whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } decrement && decrement.Left == a && SelectIsOnes(decrement.Right))
                    return SaturationIntrinsic(LlvmIntrinsicKind.SAddSat, a, LlvmConstantInt.Get(a.Context, LlvmAPInt.GetAllOnes(sat.BitWidth)), "sadd_decrement");
                return null;
            }
            if (isMax && predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge && b is LlvmConstant positiveBound &&
                SaturationAddConstant(whenFalse, a, out var positive) && positive.IsStrictlyPositive)
            {
                if (predicate == LlvmCmpPredicate.ICmpSge && LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, positiveBound, metrics: Metrics) is { } flipped)
                { predicate = flipped.Predicate; b = flipped.Constant; }
                var threshold = sat - positive;
                if (predicate == LlvmCmpPredicate.ICmpSgt && (SaturationSpecific(b, threshold) || SaturationSpecific(b, threshold - new LlvmAPInt(sat.BitWidth, 1))))
                    return SaturationIntrinsic(LlvmIntrinsicKind.SAddSat, a, LlvmConstantInt.Get(a.Context, positive), "sadd_positive_threshold");
            }
            if (!isMax && predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle && b is LlvmConstant negativeBound &&
                SaturationAddConstant(whenFalse, a, out var negative) && negative.IsNegative)
            {
                if (predicate == LlvmCmpPredicate.ICmpSle && LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, negativeBound, metrics: Metrics) is { } flipped)
                { predicate = flipped.Predicate; b = flipped.Constant; }
                var threshold = sat - negative;
                if (predicate == LlvmCmpPredicate.ICmpSlt && (SaturationSpecific(b, threshold) || SaturationSpecific(b, threshold + new LlvmAPInt(sat.BitWidth, 1))))
                    return SaturationIntrinsic(LlvmIntrinsicKind.SAddSat, a, LlvmConstantInt.Get(a.Context, negative), "sadd_negative_threshold");
            }
            if (predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge)
            { (a,b) = (b,a); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            if (predicate is not (LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle)) return null;
            if (isMax && a is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } maxSub &&
                SaturationSpecific(maxSub.Left, sat) && SaturationAdd(whenFalse, maxSub.Right, b, true))
                return SaturationIntrinsic(LlvmIntrinsicKind.SAddSat, maxSub.Right, b, "sadd_maximum_difference");
            if (!isMax && b is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } minSub &&
                SaturationSpecific(minSub.Left, sat) && SaturationAdd(whenFalse, minSub.Right, a, true))
                return SaturationIntrinsic(LlvmIntrinsicKind.SAddSat, minSub.Right, a, "sadd_minimum_difference");
            return null;
        }
    }
}
