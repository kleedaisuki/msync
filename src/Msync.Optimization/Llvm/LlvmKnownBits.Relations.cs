// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// getKnownBitsFromAndXorOr, lerp and Euclidean remainder relationships.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static LlvmKnownBits RefineBitwise(LlvmBinaryOperator binary, LlvmKnownBits left, LlvmKnownBits right,
        LlvmKnownBits result, BitsQuery query)
    {
        uint width = result.BitWidth;
        bool knownOne = left.IsNonZero || right.IsNonZero;
        if (binary.Opcode == LlvmOpcode.And && knownOne && IsKnownNegation(binary.Left, binary.Right))
        {
            LlvmKnownBits bits = left.CountMaxTrailingZeros() <= right.CountMaxTrailingZeros() ? left : right;
            uint max = bits.CountMaxTrailingZeros(), min = bits.CountMinTrailingZeros();
            result = new(bits.Zero | LlvmAPInt.GetBitsSetFrom(width, Math.Min(max + 1, width)), max == min && max < width ? LlvmAPInt.GetOneBitSet(width, max) : LlvmAPInt.GetZero(width));
        }
        if (binary.Opcode == LlvmOpcode.Xor && knownOne)
        {
            LlvmKnownBits? bits = MatchesDecrement(binary.Left, binary.Right) ? left : MatchesDecrement(binary.Right, binary.Left) ? right : null;
            if (bits is LlvmKnownBits known)
                result = new(LlvmAPInt.GetBitsSetFrom(width, Math.Min(known.CountMaxTrailingZeros() + 1, width)), LlvmAPInt.GetLowBitsSet(width, Math.Min(known.CountMinTrailingZeros() + 1, width)));
        }
        LlvmAPInt bit0 = new(width, 1);
        if (((result.Zero | result.One) & bit0).IsZero)
        {
            LlvmValue? y = MatchRelated(binary.Left, binary.Right) ?? MatchRelated(binary.Right, binary.Left);
            if (y is not null && query.Recurse(y).CountMinTrailingOnes() != 0)
                result = binary.Opcode == LlvmOpcode.And ? new(result.Zero | bit0, result.One) : new(result.Zero, result.One | bit0);
        }
        return result;
    }
    private static bool MatchesDecrement(LlvmValue x, LlvmValue expression) => expression is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add
        && ReferenceEquals(add.Left, x) && GetIntegerSplat(add.Right) is { Value.IsAllOnes: true };
    private static LlvmValue? MatchRelated(LlvmValue x, LlvmValue expression)
    {
        if (expression is not LlvmBinaryOperator binary) return null;
        if (binary.Opcode is not (LlvmOpcode.Add or LlvmOpcode.Sub)) return null;
        if (ReferenceEquals(binary.Left, x)) return binary.Right;
        if (ReferenceEquals(binary.Right, x)) return binary.Left;
        return null;
    }
    private static LlvmKnownBits RefineAdd(LlvmValue left, LlvmValue right, LlvmKnownBits result,
        BitsQuery query, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool lerp = true)
    {
        if (left.Type is LlvmVectorType) return result;
        if (lerp) result = RefineLerp(left, right, result, query);
        if (!MatchRemainder(left, right, out LlvmValue? divisor) && !MatchRemainder(right, left, out divisor)) return result;
        LlvmValue rem = left is LlvmBinaryOperator { Opcode: LlvmOpcode.SRem } ? left : right;
        if (!query.Recurse(divisor!).IsNonNegative) return result;
        if (!LlvmValueTracking.IsGuaranteedNotToBeUndef(rem, tree, context, metrics, depth + 1, query.Assumptions, query.AllowEphemerals, query.Conditions)
            || !LlvmValueTracking.IsGuaranteedNotToBeUndef(divisor!, tree, context, metrics, depth + 1, query.Assumptions, query.AllowEphemerals, query.Conditions)) return result;
        Count(metrics, "known_bits.add.euclidean_remainder");
        return result.WithNonNegative();
    }
    private static bool MatchRemainder(LlvmValue remainder, LlvmValue adjustment, out LlvmValue? divisor)
    {
        divisor = null;
        if (remainder is not LlvmBinaryOperator { Opcode: LlvmOpcode.SRem } rem
            || adjustment is not LlvmSelectInstruction { FalseValue: LlvmConstantInt { IsZero: true } } select
            || !ReferenceEquals(select.TrueValue, rem.Right)
            || select.Condition is not LlvmICmpInstruction cmp || !ReferenceEquals(cmp.Left, remainder)
            || cmp.Right is not LlvmConstantInt threshold) return false;
        bool negativeCheck = (cmp.Predicate == LlvmCmpPredicate.ICmpSlt && threshold.Value.IsZero)
            || (cmp.Predicate == LlvmCmpPredicate.ICmpSle && threshold.Value.IsAllOnes)
            || (cmp.Predicate == LlvmCmpPredicate.ICmpUgt && threshold.Value == LlvmAPInt.GetSignedMaxValue(threshold.Value.BitWidth))
            || (cmp.Predicate == LlvmCmpPredicate.ICmpUge && threshold.Value == LlvmAPInt.GetSignedMinValue(threshold.Value.BitWidth));
        if (!negativeCheck) return false;
        divisor = rem.Right;
        return true;
    }
    private static LlvmKnownBits RefineLerp(LlvmValue left, LlvmValue right, LlvmKnownBits result, BitsQuery query)
    {
        if (!MatchLerp(left, right, out LlvmValue? a, out LlvmValue? b, out LlvmValue? c, out LlvmValue? d, out bool xor)
            && !MatchLerp(right, left, out a, out b, out c, out d, out xor)) return result;
        LlvmKnownBits one = LlvmKnownBits.MakeConstant(new(result.BitWidth, 1));
        LlvmKnownBits ka = a is null ? one : query.Recurse(a), kd = d is null ? one : query.Recurse(d);
        if (!ka.IsNonNegative || !kd.IsNonNegative) return result;
        LlvmKnownBits kb = query.Recurse(b!), kc = query.Recurse(c!);
        if (!kb.IsNonNegative || !kc.IsNonNegative) return result;
        if (xor && (kc.GetMaxValue() & kb.One) != kc.GetMaxValue()) return result;
        LlvmAPInt max = ka.GetMaxValue().Ugt(kd.GetMaxValue()) ? ka.GetMaxValue() : kd.GetMaxValue();
        LlvmAPInt bound = max.UMulOverflow(kb.GetMaxValue(), out bool overflow);
        return overflow ? result : new(result.Zero | LlvmAPInt.GetHighBitsSet(result.BitWidth, bound.CountLeadingZeros()), result.One);
    }
    private static bool MatchLerp(LlvmValue left, LlvmValue right, out LlvmValue? a, out LlvmValue? b,
        out LlvmValue? c, out LlvmValue? d, out bool xor)
    {
        a = b = c = d = null; xor = false;
        LlvmValue sub = left;
        if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } mul)
        {
            if (IsSubBC(mul.Right)) { sub = mul.Right; a = mul.Left; }
            else if (IsSubBC(mul.Left)) { sub = mul.Left; a = mul.Right; }
            else return false;
        }
        if (sub is not LlvmBinaryOperator binary || !IsSubBC(sub)) return false;
        xor = binary.Opcode == LlvmOpcode.Xor;
        b = xor ? binary.Right : binary.Left; c = xor ? binary.Left : binary.Right;
        if (ReferenceEquals(right, c)) return true;
        if (right is not LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } product) return false;
        if (ReferenceEquals(product.Right, c)) { d = product.Left; return true; }
        if (ReferenceEquals(product.Left, c)) { d = product.Right; return true; }
        return false;
    }
    private static bool IsSubBC(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true } or LlvmBinaryOperator { Opcode: LlvmOpcode.Xor };
}
