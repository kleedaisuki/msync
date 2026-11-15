// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp getDemandedBitsLHSMask/foldICmpUsingKnownBits.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmAPInt GetDemandedBitsLHSMask(LlvmICmpInstruction compare, uint width)
        {
            if (ICmpAPInt(compare.Right) is not { Value: var rhs }) return LlvmAPInt.GetAllOnes(width);
            if (LlvmValueTracking.IsSignBitCheck(compare.Predicate, rhs, out _)) return LlvmAPInt.GetSignedMinValue(width);
            uint unused = compare.Predicate switch
            {
                LlvmCmpPredicate.ICmpUgt => rhs.CountTrailingOnes(),
                LlvmCmpPredicate.ICmpUlt => rhs.CountTrailingZeros(),
                _ => 0
            };
            return ~LlvmAPInt.GetLowBitsSet(width, unused);
        }

        private LlvmInstruction? FoldICmpUsingKnownBits(LlvmICmpInstruction compare)
        {
            Count("icmp.knownbits.queries");
            LlvmValue x = compare.Left, y = compare.Right;
            LlvmCmpPredicate pred = compare.Predicate;
            uint width = x.Type.IsIntOrIntVectorTy() ? x.Type.GetScalarSizeInBits() : DL.GetPointerTypeSizeInBits(x.Type.GetScalarType());
            if (width == 0) return null;
            LlvmKnownBits kx = new(width), ky = new(width);
            var query = Query(compare) with { DC = null };
            if (SimplifyDemandedBits(compare, 0, GetDemandedBitsLHSMask(compare, width), ref kx, query) ||
                SimplifyDemandedBits(compare, 1, LlvmAPInt.GetAllOnes(width), ref ky, query))
            { Count("icmp.knownbits.demanded-rewrite"); return compare; }
            if (compare.SameSign && ICmpIsUnsigned(pred))
            {
                ky = PropagateSign(kx, ky); kx = PropagateSign(ky, kx);
                Count("icmp.knownbits.samesign-propagation");
            }
            if (x is not LlvmConstant && kx.IsConstant) return Rewrite(pred, KnownConstant(x.Type, kx.One), y, "lhs-constant");
            if (y is not LlvmConstant && ky.IsConstant) return Rewrite(pred, x, KnownConstant(y.Type, ky.One), "rhs-constant");
            if (LlvmKnownBitsAnalysis.Compare(pred, kx, ky) is { } truth)
            {
                Count("icmp.knownbits.constant-result");
                return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Type, truth));
            }
            LlvmAPInt xmin = ICmpIsSigned(pred) ? kx.GetSignedMinValue() : kx.GetMinValue();
            LlvmAPInt xmax = ICmpIsSigned(pred) ? kx.GetSignedMaxValue() : kx.GetMaxValue();
            LlvmAPInt ymin = ICmpIsSigned(pred) ? ky.GetSignedMinValue() : ky.GetMinValue();
            LlvmAPInt ymax = ICmpIsSigned(pred) ? ky.GetSignedMaxValue() : ky.GetMaxValue();
            LlvmAPInt? c = ICmpAPInt(y)?.Value;
            if (!IsMinMaxCmp())
            {
                if (pred is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSlt)
                {
                    if (ymin == xmax) return Rewrite(LlvmCmpPredicate.ICmpNe, x, y, "strict-boundary");
                    if (c is { } cv && cv == xmin + new LlvmAPInt(width, 1))
                        return Rewrite(LlvmCmpPredicate.ICmpEq, x, KnownConstant(y.Type, cv - new LlvmAPInt(width, 1)), "lower-singleton");
                    if (pred == LlvmCmpPredicate.ICmpUlt && c is { } limit &&
                        kx.CountMinTrailingZeros() >= (limit.IsPowerOf2 ? limit.GetActiveBits() - 1 : limit.GetActiveBits()))
                        return Rewrite(LlvmCmpPredicate.ICmpEq, x, LlvmConstant.GetNullValue(y.Type), "ult-aligned-zero");
                }
                if (pred is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSgt)
                {
                    if (ymax == xmin) return Rewrite(LlvmCmpPredicate.ICmpNe, x, y, "strict-boundary");
                    if (c is { } cv && cv == xmax - new LlvmAPInt(width, 1))
                        return Rewrite(LlvmCmpPredicate.ICmpEq, x, KnownConstant(y.Type, cv + new LlvmAPInt(width, 1)), "upper-singleton");
                    if (pred == LlvmCmpPredicate.ICmpUgt && c is { } limit && kx.CountMinTrailingZeros() >= limit.GetActiveBits())
                        return Rewrite(LlvmCmpPredicate.ICmpNe, x, LlvmConstant.GetNullValue(y.Type), "ugt-aligned-zero");
                }
            }
            if (ICmpIsEquality(pred))
            {
                LlvmAPInt mask = ~kx.Zero;
                if (ky.IsZero)
                {
                    LlvmValue lhs = x;
                    if (x is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && ICmpAPInt(and.Right) is { Value: var andMask } && andMask == mask) lhs = and.Left;
                    if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shift && ICmpAPInt(shift.Left) is { Value: { IsPowerOf2: true } power })
                    {
                        LlvmAPInt sum = (mask & ~(power - new LlvmAPInt(width, 1))) + power;
                        if (sum.IsPowerOf2)
                            return Rewrite(pred == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpUlt,
                                shift.Right, KnownConstant(shift.Right.Type, new LlvmAPInt(shift.Right.Type.GetScalarSizeInBits(), sum.CountTrailingZeros() - power.CountTrailingZeros())), "shift-mask");
                    }
                }
                if (ky.IsConstant && ky.One.IsPowerOf2 && (kx & ky).Equals(kx))
                    return Rewrite(LlvmCmpInstruction.GetInversePredicate(pred), x, LlvmConstant.GetNullValue(y.Type), "single-bit");
            }
            if ((pred is LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpUge) && ymin == xmax ||
                (pred is LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpUle) && ymax == xmin)
                return Rewrite(LlvmCmpPredicate.ICmpEq, x, y, "inclusive-boundary");
            if ((ICmpIsSigned(pred) || ICmpIsUnsigned(pred) && !compare.SameSign) &&
                (kx.Zero.IsNegative && ky.Zero.IsNegative || kx.One.IsNegative && ky.One.IsNegative))
            {
                compare.Predicate = ICmpUnsignedPredicate(pred); compare.SameSign = true;
                Count("icmp.knownbits.signed-to-unsigned"); return compare;
            }
            return null;
            bool IsMinMaxCmp() => compare.HasOneUse && LlvmSelectPattern.Match(compare.Users.First(), Metrics).IsMinOrMax &&
                (IsMinMaxIntrinsic(x) || IsMinMaxIntrinsic(y));
            LlvmInstruction Rewrite(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right, string reason)
            { if (Metrics is not null) Count("icmp.knownbits." + reason); return new LlvmICmpInstruction(predicate, left, right); }
        }
        private static LlvmKnownBits PropagateSign(LlvmKnownBits from, LlvmKnownBits to)
        {
            if (to.IsNegative || to.IsNonNegative) return to;
            LlvmAPInt sign = LlvmAPInt.GetSignedMinValue(to.BitWidth);
            return from.IsNegative ? new(to.Zero, to.One | sign) : from.IsNonNegative ? new(to.Zero | sign, to.One) : to;
        }
        private static bool IsMinMaxIntrinsic(LlvmValue value) => value is LlvmCallInstruction call &&
            call.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsMinMax(descriptor.Kind);
        private static LlvmConstant KnownConstant(LlvmType type, LlvmAPInt value)
        {
            LlvmConstant constant = LlvmConstantInt.Get(type.Context, value);
            LlvmType scalar = type.GetScalarType();
            if (scalar.IsPointerTy()) constant = LlvmConstantExpr.GetIntToPtr(constant, scalar);
            if (scalar is LlvmByteType) constant = LlvmConstantExpr.GetBitCast(constant, scalar);
            return type is LlvmVectorType vector ? LlvmConstantVector.GetSplat(vector.ElementCount, constant) : constant;
        }
    }
}
