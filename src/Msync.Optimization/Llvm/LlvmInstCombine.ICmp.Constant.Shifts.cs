// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 2383-2760.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpShlLHSC(LlvmICmpInstruction compare, LlvmBinaryOperator shift, LlvmAPInt constant)
        {
            if (!shift.HasNoUnsignedWrap || shift.Left is not LlvmConstantInt lhs) return null;
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmAPInt value = lhs.Value;
            if (ICmpIsUnsigned(predicate))
            {
                if (value.IsZero || value.Ugt(constant)) return null;
                LlvmAPInt.UDivRem(constant, value, out var quotient, out var remainder);
                if (!remainder.IsZero || !quotient.IsPowerOf2)
                {
                    if (predicate == LlvmCmpPredicate.ICmpUlt) predicate = LlvmCmpPredicate.ICmpUle;
                    else if (predicate == LlvmCmpPredicate.ICmpUge) predicate = LlvmCmpPredicate.ICmpUgt;
                }
                Count("icmp.constant.shl.constant-lhs-unsigned");
                return IccCompare(predicate, shift.Right, IccConstant(shift, (ulong)IccLog2(quotient)));
            }
            if (ICmpIsSigned(predicate) && value.IsOne)
            {
                if (predicate == LlvmCmpPredicate.ICmpSgt && constant.Sle(LlvmAPInt.GetZero(constant.BitWidth))) predicate = LlvmCmpPredicate.ICmpNe;
                else if (predicate == LlvmCmpPredicate.ICmpSlt && (constant - IccInt(constant, 1)).Sle(LlvmAPInt.GetZero(constant.BitWidth))) predicate = LlvmCmpPredicate.ICmpEq;
                else return null;
                Count("icmp.constant.shl.constant-lhs-signed");
                return IccCompare(predicate, shift.Right, IccConstant(shift, (ulong)constant.BitWidth - 1));
            }
            return null;
        }

        private LlvmInstruction? FoldICmpShlConstant(LlvmICmpInstruction compare, LlvmBinaryOperator shift, LlvmAPInt constant)
        {
            Count("icmp.constant.shl.queries");
            if (ICmpIsEquality(compare.Predicate) && shift.Left is LlvmConstantInt inputConstant)
                return FoldICmpShlConstConst(compare, shift.Right, constant, inputConstant.Value);
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue input = shift.Left;
            var zero = LlvmAPInt.GetZero(constant.BitWidth);
            if (constant.Sle(zero) && shift.HasNoUnsignedWrap && shift.HasNoSignedWrap ||
                ICmpIsEquality(predicate) && constant.IsZero && (shift.HasNoUnsignedWrap || shift.HasNoSignedWrap) ||
                shift.HasNoSignedWrap && predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSlt &&
                    (constant.IsZero || (predicate == LlvmCmpPredicate.ICmpSgt ? constant.IsAllOnes : constant.IsOne)))
                return New(predicate, input, constant, "nowrap-sign-or-zero");
            if (shift.Right is not LlvmConstantInt amount) return FoldICmpShlLHSC(compare, shift, constant);
            uint width = constant.BitWidth;
            if (amount.Value.Uge(new LlvmAPInt(width, width))) return null;
            uint distance = (uint)amount.Value.GetZExtValue();
            var one = IccInt(constant, 1);
            if (shift.HasNoSignedWrap)
            {
                if (predicate == LlvmCmpPredicate.ICmpSgt) return New(predicate, input, constant.AShr(distance), "nsw-gt");
                if (ICmpIsEquality(predicate) && constant.AShr(distance).Shl(distance) == constant) return New(predicate, input, constant.AShr(distance), "nsw-equal");
                if (predicate == LlvmCmpPredicate.ICmpSlt)
                {
                    Debug.Assert(!constant.IsMinSignedValue);
                    return New(predicate, input, (constant - one).AShr(distance) + one, "nsw-lt");
                }
            }
            if (shift.HasNoUnsignedWrap)
            {
                if (predicate == LlvmCmpPredicate.ICmpUgt) return New(predicate, input, constant.LShr(distance), "nuw-gt");
                if (ICmpIsEquality(predicate) && constant.LShr(distance).Shl(distance) == constant) return New(predicate, input, constant.LShr(distance), "nuw-equal");
                if (predicate == LlvmCmpPredicate.ICmpUlt)
                {
                    Debug.Assert(!constant.IsZero);
                    return New(predicate, input, (constant - one).LShr(distance) + one, "nuw-lt");
                }
            }
            if (ICmpIsEquality(predicate) && shift.HasOneUse)
                return Mask(predicate, LlvmAPInt.GetLowBitsSet(width, width - distance), constant.LShr(distance), "equality-mask");
            if (shift.HasOneUse && LlvmValueTracking.IsSignBitCheck(predicate, constant, out bool negative))
                return Mask(negative ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq, LlvmAPInt.GetOneBitSet(width, width - distance - 1), zero, "sign-mask");
            if (ICmpIsUnsigned(predicate) && shift.HasOneUse)
            {
                if ((constant + one).IsPowerOf2 && predicate is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUgt)
                    return Mask(predicate == LlvmCmpPredicate.ICmpUle ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe, (~constant).LShr(distance), zero, "unsigned-high-mask");
                if (constant.IsPowerOf2 && predicate is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge)
                    return Mask(predicate == LlvmCmpPredicate.ICmpUlt ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe, (~(constant - one)).LShr(distance), zero, "unsigned-low-mask");
            }
            if (shift.HasOneUse && distance != 0 && ShouldChangeType(width, width - distance))
            {
                LlvmCmpPredicate newPredicate = predicate;
                LlvmAPInt rhs = constant;
                if (rhs.CountTrailingZeros() < distance && predicate is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSgt &&
                    LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, IccConstant(shift, constant), metrics: Metrics) is { } flipped)
                { newPredicate = flipped.Predicate; rhs = ICmpAPInt(flipped.Constant)!.Value; }
                if (rhs.CountTrailingZeros() >= distance)
                {
                    Count("icmp.constant.shl.narrow-trunc");
                    LlvmIntegerType type = LlvmIntegerType.Get(shift.Context, width - distance);
                    LlvmValue trunc = Builder.CreateTrunc(input, type);
                    if (trunc is LlvmCastInstruction cast) cast.HasNoSignedWrap = shift.HasNoSignedWrap;
                    return IccCompare(newPredicate, trunc, LlvmConstantInt.Get(shift.Context, rhs.AShr(distance).Trunc(width - distance)));
                }
            }
            return null;
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmValue value, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.shl." + reason); return IccCompare(pred, value, IccConstant(value, rhs)); }
            LlvmInstruction Mask(LlvmCmpPredicate pred, LlvmAPInt mask, LlvmAPInt rhs, string reason) => New(pred, Builder.CreateAnd(input, IccConstant(input, mask), shift.Name + ".mask"), rhs, reason);
        }

        private LlvmInstruction? FoldICmpShrConstant(LlvmICmpInstruction compare, LlvmBinaryOperator shift, LlvmAPInt constant)
        {
            Count("icmp.constant.shr.queries");
            LlvmValue input = shift.Left;
            LlvmCmpPredicate predicate = compare.Predicate;
            if (ICmpIsEquality(predicate) && shift.IsExact && constant.IsZero) return New(predicate, input, constant, "exact-zero");
            bool arithmetic = shift.Opcode == LlvmOpcode.AShr;
            if (input is LlvmConstantInt value)
            {
                if (ICmpIsEquality(predicate)) return FoldICmpShrConstConst(compare, shift.Right, constant, value.Value);
                if (!arithmetic && value.Value.IsNegative && LlvmValueTracking.IsSignBitCheck(predicate, constant, out bool negative))
                    return New(negative ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe, shift.Right, LlvmAPInt.GetZero(constant.BitWidth), "constant-sign");
                if (!arithmetic && value.Value.IsPowerOf2 && predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUlt)
                {
                    bool greater = predicate == LlvmCmpPredicate.ICmpUgt;
                    Debug.Assert(value.Value.Uge(constant) && (greater || !constant.IsZero));
                    uint leading = (greater ? constant : constant - IccInt(constant, 1)).CountLeadingZeros();
                    return New(greater ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge, shift.Right,
                        IccInt(constant, unchecked(leading - value.Value.CountLeadingZeros())), "constant-power");
                }
            }
            if (shift.Right is not LlvmConstantInt amount) return null;
            uint width = constant.BitWidth, distance = (uint)amount.Value.GetLimitedValue(width);
            if (distance >= width || distance == 0) return null;
            LlvmAPInt one = IccInt(constant, 1), next = constant + one;
            if (arithmetic && shift.HasOneUse)
            {
                if (shift.IsExact && predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpUlt && (constant - one).IsPowerOf2 && constant.CountLeadingZeros() > distance)
                    return New(predicate, input, (constant - one).Shl(distance) + one, "exact-near-power");
                if (shift.IsExact || predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpUlt)
                {
                    var rhs = constant.Shl(distance);
                    if (rhs.AShr(distance) == constant) return New(predicate, input, rhs, "arithmetic-lossless");
                }
                if (predicate == LlvmCmpPredicate.ICmpSgt)
                {
                    var rhs = next.Shl(distance) - one;
                    if (!constant.IsMaxSignedValue && !next.Shl(distance).IsMinSignedValue && (rhs + one).AShr(distance) == next)
                        return New(predicate, input, rhs, "arithmetic-signed-gt");
                }
                if (predicate == LlvmCmpPredicate.ICmpUgt)
                {
                    var rhs = next.Shl(distance) - one;
                    if ((rhs + one).AShr(distance) == next || next.Shl(distance).IsMinSignedValue)
                        return New(predicate, input, rhs, "arithmetic-unsigned-gt");
                }
                uint signBits = constant.IsNegative ? constant.CountLeadingOnes() : constant.CountLeadingZeros();
                if (width > 2 && signBits <= distance)
                {
                    if (predicate == LlvmCmpPredicate.ICmpUgt) return New(LlvmCmpPredicate.ICmpSlt, input, LlvmAPInt.GetZero(width), "arithmetic-sign-negative");
                    if (predicate == LlvmCmpPredicate.ICmpUlt) return New(LlvmCmpPredicate.ICmpSgt, input, LlvmAPInt.GetAllOnes(width), "arithmetic-sign-nonnegative");
                }
            }
            else if (!arithmetic)
            {
                if (predicate == LlvmCmpPredicate.ICmpUlt || predicate == LlvmCmpPredicate.ICmpUgt && shift.IsExact)
                {
                    var rhs = constant.Shl(distance);
                    if (rhs.LShr(distance) == constant) return New(predicate, input, rhs, "logical-lossless");
                }
                if (predicate == LlvmCmpPredicate.ICmpUgt)
                {
                    var rhs = next.Shl(distance) - one;
                    if ((rhs + one).LShr(distance) == next) return New(predicate, input, rhs, "logical-gt");
                }
            }
            if (!ICmpIsEquality(predicate)) return null;
            Debug.Assert(arithmetic ? constant.Shl(distance).AShr(distance) == constant : constant.Shl(distance).LShr(distance) == constant);
            if (shift.IsExact) return New(predicate, input, constant.Shl(distance), "exact-equality");
            if (shift.HasOneUse)
                return New(predicate, Builder.CreateAnd(input, IccConstant(input, LlvmAPInt.GetHighBitsSet(width, width - distance)), shift.Name + ".mask"), constant.Shl(distance), "equality-mask");
            return null;
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmValue value, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.shr." + reason); return IccCompare(pred, value, IccConstant(value, rhs)); }
        }
    }
}
