// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.h/.cpp
// matchSelectPattern/matchDecomposedSelectPattern (CastOp=null, integer path),
// getSelectPattern, matchMinMax/getNotValue, isKnownNegation,
// isSignedMinMaxClamp. PatternMatch.h supplies the exact scalar match predicates.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public enum LlvmSelectPatternFlavor : byte
{
    Unknown = 0, SMin = 1, UMin = 2, SMax = 3, UMax = 4,
    FMinNum = 5, FMaxNum = 6, Abs = 7, NAbs = 8
}

public readonly record struct LlvmSelectPatternResult(LlvmSelectPatternFlavor Flavor, LlvmValue? LHS, LlvmValue? RHS, LlvmOpcode? CastOpcode = null)
{
    public LlvmSelectPatternNaNBehavior NaNBehavior { get; init; }
    public bool Ordered { get; init; }
    public bool IsMinOrMax => Flavor is not (LlvmSelectPatternFlavor.Unknown or LlvmSelectPatternFlavor.Abs or LlvmSelectPatternFlavor.NAbs);
}

public static partial class LlvmSelectPattern
{
    public static LlvmSelectPatternResult Match(LlvmValue value, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "select_pattern.queries");
        if (value is not LlvmSelectInstruction select || select.Condition is not LlvmCmpInstruction compare)
        { Count(metrics, "select_pattern.not_integer_cmp_select"); return default; }
        if (select.Type.IsVectorTy() || compare.Left.Type.IsVectorTy())
        { Count(metrics, "select_pattern.unported_vector_or_fp"); return default; }
        if (IsSelectPatternEquality(compare.Predicate))
        { Count(metrics, "select_pattern.equality"); return default; }
        LlvmSelectPatternResult result = MatchDecomposed(compare, select.TrueValue, select.FalseValue,
            select.FastMathFlags, metrics: metrics);
        if (metrics is not null) Count(metrics, "select_pattern.result." + result.Flavor);
        return result;
    }

    private static LlvmSelectPatternResult MatchInteger(LlvmCmpPredicate predicate, LlvmValue cmpLeft, LlvmValue cmpRight,
        LlvmValue whenTrue, LlvmValue whenFalse, Dictionary<string, ulong>? metrics)
    {
        // Source initializes outputs before commuting the local cmp operands.
        LlvmValue left = cmpLeft, right = cmpRight;
        if (ReferenceEquals(whenTrue, cmpRight) && ReferenceEquals(whenFalse, cmpLeft))
        {
            (cmpLeft, cmpRight) = (cmpRight, cmpLeft);
            predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
        }
        if (ReferenceEquals(whenTrue, cmpLeft) && ReferenceEquals(whenFalse, cmpRight))
            return new(GetSelectPattern(predicate), left, right);

        if (IsKnownNegation(whenTrue, whenFalse))
        {
            if (MaybeSignPreservingValue(whenTrue, cmpLeft))
            {
                left = whenTrue; right = whenFalse;
                if (IsNegationOf(cmpLeft, whenFalse)) (left, right) = (right, left);
                if (predicate == LlvmCmpPredicate.ICmpSgt && IsZeroOrAllOnes(cmpRight))
                    return new(LlvmSelectPatternFlavor.Abs, left, right);
                if (predicate == LlvmCmpPredicate.ICmpSge && IsZeroOrOne(cmpRight))
                    return new(LlvmSelectPatternFlavor.Abs, left, right);
                if (predicate == LlvmCmpPredicate.ICmpSlt && IsZeroOrOne(cmpRight))
                    return new(LlvmSelectPatternFlavor.NAbs, left, right);
            }
            else if (MaybeSignPreservingValue(whenFalse, cmpLeft))
            {
                left = whenFalse; right = whenTrue;
                if (IsNegationOf(cmpLeft, whenTrue)) (left, right) = (right, left);
                if (predicate == LlvmCmpPredicate.ICmpSgt && IsZeroOrAllOnes(cmpRight))
                    return new(LlvmSelectPatternFlavor.NAbs, left, right);
                if (predicate == LlvmCmpPredicate.ICmpSlt && IsZeroOrOne(cmpRight))
                    return new(LlvmSelectPatternFlavor.Abs, left, right);
            }
        }
        return MatchMinMax(predicate, cmpLeft, cmpRight, whenTrue, whenFalse, metrics);
    }

    private static LlvmSelectPatternResult MatchMinMax(LlvmCmpPredicate predicate, LlvmValue cmpLeft, LlvmValue cmpRight,
        LlvmValue whenTrue, LlvmValue whenFalse, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "select_pattern.minmax.queries");
        LlvmSelectPatternFlavor clamp = MatchClamp(predicate, cmpLeft, cmpRight, whenTrue, whenFalse, metrics);
        if (clamp != LlvmSelectPatternFlavor.Unknown)
        {
            Count(metrics, "select_pattern.clamp.matched");
            return new(clamp, whenTrue, whenFalse);
        }
        if (ReferenceEquals(cmpLeft, GetNotValue(whenTrue)) && ReferenceEquals(cmpRight, GetNotValue(whenFalse)))
        {
            LlvmSelectPatternFlavor flavor = predicate switch
            {
                LlvmCmpPredicate.ICmpSgt => LlvmSelectPatternFlavor.SMin,
                LlvmCmpPredicate.ICmpSlt => LlvmSelectPatternFlavor.SMax,
                LlvmCmpPredicate.ICmpUgt => LlvmSelectPatternFlavor.UMin,
                LlvmCmpPredicate.ICmpUlt => LlvmSelectPatternFlavor.UMax,
                _ => LlvmSelectPatternFlavor.Unknown
            };
            if (flavor != LlvmSelectPatternFlavor.Unknown) return new(flavor, whenTrue, whenFalse);
        }
        if (ReferenceEquals(cmpLeft, GetNotValue(whenFalse)) && ReferenceEquals(cmpRight, GetNotValue(whenTrue)))
        {
            LlvmSelectPatternFlavor flavor = predicate switch
            {
                LlvmCmpPredicate.ICmpSgt => LlvmSelectPatternFlavor.SMax,
                LlvmCmpPredicate.ICmpSlt => LlvmSelectPatternFlavor.SMin,
                LlvmCmpPredicate.ICmpUgt => LlvmSelectPatternFlavor.UMax,
                LlvmCmpPredicate.ICmpUlt => LlvmSelectPatternFlavor.UMin,
                _ => LlvmSelectPatternFlavor.Unknown
            };
            if (flavor != LlvmSelectPatternFlavor.Unknown) return new(flavor, whenTrue, whenFalse);
        }
        if (predicate is not (LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSlt) || cmpRight is not LlvmConstantInt bound)
            return new(LlvmSelectPatternFlavor.Unknown, whenTrue, whenFalse);
        LlvmConstantInt? armBound = ReferenceEquals(cmpLeft, whenTrue) ? whenFalse as LlvmConstantInt :
            ReferenceEquals(cmpLeft, whenFalse) ? whenTrue as LlvmConstantInt : null;
        if (armBound is not null)
        {
            if (predicate == LlvmCmpPredicate.ICmpSlt && bound.Value.IsZero && armBound.Value.IsMaxSignedValue)
                return new(ReferenceEquals(cmpLeft, whenTrue) ? LlvmSelectPatternFlavor.UMax : LlvmSelectPatternFlavor.UMin, whenTrue, whenFalse);
            if (predicate == LlvmCmpPredicate.ICmpSgt && bound.Value.IsAllOnes && armBound.Value.IsMinSignedValue)
                return new(ReferenceEquals(cmpLeft, whenFalse) ? LlvmSelectPatternFlavor.UMax : LlvmSelectPatternFlavor.UMin, whenTrue, whenFalse);
        }
        return new(LlvmSelectPatternFlavor.Unknown, whenTrue, whenFalse);
    }
    public static LlvmSelectPatternFlavor GetSelectPattern(LlvmCmpPredicate predicate) => predicate switch
    {
        LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUge => LlvmSelectPatternFlavor.FMaxNum,
        LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle => LlvmSelectPatternFlavor.FMinNum,
        LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge => LlvmSelectPatternFlavor.UMax,
        LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge => LlvmSelectPatternFlavor.SMax,
        LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle => LlvmSelectPatternFlavor.UMin,
        LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle => LlvmSelectPatternFlavor.SMin,
        _ => LlvmSelectPatternFlavor.Unknown
    };
    private static LlvmValue? GetNotValue(LlvmValue value)
    {
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
        {
            if (xor.Left is LlvmConstantInt { Value.IsAllOnes: true }) return xor.Right;
            if (xor.Right is LlvmConstantInt { Value.IsAllOnes: true }) return xor.Left;
        }
        return value is LlvmConstantInt constant ? LlvmConstantInt.Get(value.Context, ~constant.Value) : null;
    }
    private static bool CmpValueOrSExt(LlvmValue value, LlvmValue cmpLeft) => ReferenceEquals(value, cmpLeft) ||
        value is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } cast && ReferenceEquals(cast.Operand, cmpLeft);
    private static bool MaybeSignPreservingValue(LlvmValue value, LlvmValue cmpLeft)
    {
        if (CmpValueOrSExt(value, cmpLeft)) return true;
        if (value is not LlvmBinaryOperator { HasNoSignedWrap: true } binary || !CmpValueOrSExt(binary.Left, cmpLeft)) return false;
        return binary.Opcode == LlvmOpcode.Shl || binary.Opcode == LlvmOpcode.Mul &&
            binary.Right is LlvmConstantInt { Value.IsStrictlyPositive: true };
    }
    private static bool IsZeroOrAllOnes(LlvmValue value) => value is LlvmConstantInt integer &&
        (integer.Value.IsZero || integer.Value.IsAllOnes);
    private static bool IsZeroOrOne(LlvmValue value) => value is LlvmConstantInt integer &&
        (integer.Value.IsZero || integer.Value.IsOne);
    private static bool IsNegationOf(LlvmValue value, LlvmValue operand, bool needNSW = false, bool allowPoison = true)
    {
        if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } binary ||
            binary.Left is not LlvmConstantInt { IsZero: true } || !ReferenceEquals(binary.Right, operand)) return false;
        return !needNSW || binary.HasNoSignedWrap;
    }
    public static bool IsKnownNegation(LlvmValue left, LlvmValue right, bool needNSW = false, bool allowPoison = true)
    {
        if (IsNegationOf(left, right, needNSW, allowPoison) || IsNegationOf(right, left, needNSW, allowPoison)) return true;
        if (left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } a || right is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } b)
            return false;
        return (!needNSW || a.HasNoSignedWrap && b.HasNoSignedWrap) &&
            ReferenceEquals(a.Left, b.Right) && ReferenceEquals(a.Right, b.Left);
    }
    public static bool TrySignedMinMaxClamp(LlvmValue value, out LlvmValue? input, out LlvmAPInt low, out LlvmAPInt high,
        Dictionary<string, ulong>? metrics = null)
    {
        input = null; low = high = default;
        LlvmSelectPatternResult outer = Match(value, metrics);
        if (outer.Flavor is not (LlvmSelectPatternFlavor.SMin or LlvmSelectPatternFlavor.SMax) || outer.RHS is not LlvmConstantInt lower) return false;
        LlvmSelectPatternResult inner = Match(outer.LHS!, metrics);
        if (inner.Flavor != (outer.Flavor == LlvmSelectPatternFlavor.SMin ? LlvmSelectPatternFlavor.SMax : LlvmSelectPatternFlavor.SMin) ||
            inner.RHS is not LlvmConstantInt upper) return false;
        low = lower.Value; high = upper.Value;
        if (outer.Flavor == LlvmSelectPatternFlavor.SMin) (low, high) = (high, low);
        input = inner.LHS;
        return low.Sle(high);
    }
    private static void Count(Dictionary<string, ulong>? metrics, string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
