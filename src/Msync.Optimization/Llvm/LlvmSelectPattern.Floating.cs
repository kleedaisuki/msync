// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isKnownNonNaN/isKnownNonZero/matchFastFloatClamp/matchSelectPattern FP path;
// PatternMatch.h FMaxMin_match (ordered/unordered scalar select forms).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public enum LlvmSelectPatternNaNBehavior : byte { NotApplicable = 0, ReturnsNaN = 1, ReturnsOther = 2, ReturnsAny = 3 }

public static partial class LlvmSelectPattern
{
    private static bool IsSelectPatternEquality(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe or
        LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUeq or LlvmCmpPredicate.FCmpUne;

    private static LlvmSelectPatternResult MatchCore(LlvmCmpPredicate predicate, LlvmFastMathFlags flags,
        LlvmValue cmpLeft, LlvmValue cmpRight, LlvmValue whenTrue, LlvmValue whenFalse, Dictionary<string, ulong>? metrics) =>
        (byte)predicate <= 15 ? MatchFloating(predicate, flags, cmpLeft, cmpRight, whenTrue, whenFalse, metrics) :
            MatchInteger(predicate, cmpLeft, cmpRight, whenTrue, whenFalse, metrics);

    private static bool PatternKnownNonNaN(LlvmValue value, LlvmFastMathFlags flags) =>
        (flags & LlvmFastMathFlags.NoNaNs) != 0 || value is LlvmConstantFP { IsNaN: false } or LlvmConstantAggregateZero;
    private static bool PatternKnownNonZero(LlvmValue value) => value is LlvmConstantFP { IsZero: false };
    private static bool PatternIsOrdered(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.FCmpOeq and <= LlvmCmpPredicate.FCmpOrd;

    private static LlvmSelectPatternResult FloatingPatternResult(LlvmSelectPatternFlavor flavor,
        LlvmValue left, LlvmValue right, LlvmSelectPatternNaNBehavior behavior, bool ordered) =>
        new(flavor, left, right) { NaNBehavior = behavior, Ordered = ordered };

    public static LlvmSelectPatternResult GetSelectPattern(LlvmCmpPredicate predicate, LlvmSelectPatternNaNBehavior behavior, bool ordered)
    {
        LlvmSelectPatternFlavor flavor = predicate switch
        {
            LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge => LlvmSelectPatternFlavor.FMaxNum,
            LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle or LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle => LlvmSelectPatternFlavor.FMinNum,
            _ => GetSelectPattern(predicate)
        };
        return new(flavor, null, null)
        {
            NaNBehavior = flavor is LlvmSelectPatternFlavor.FMinNum or LlvmSelectPatternFlavor.FMaxNum ? behavior : LlvmSelectPatternNaNBehavior.NotApplicable,
            Ordered = (flavor is LlvmSelectPatternFlavor.FMinNum or LlvmSelectPatternFlavor.FMaxNum) && ordered
        };
    }

    private static LlvmSelectPatternResult MatchFloating(LlvmCmpPredicate predicate, LlvmFastMathFlags flags,
        LlvmValue cmpLeft, LlvmValue cmpRight, LlvmValue whenTrue, LlvmValue whenFalse, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "select_pattern.floating.queries");
        LlvmValue? outputZero = null;
        if (whenTrue is LlvmConstantFP { IsZero: true } && whenFalse is not LlvmConstantFP { IsZero: true }) outputZero = whenTrue;
        else if (whenFalse is LlvmConstantFP { IsZero: true } && whenTrue is not LlvmConstantFP { IsZero: true }) outputZero = whenFalse;
        if (outputZero is not null)
        {
            if (cmpLeft is LlvmConstantFP { IsZero: true }) cmpLeft = outputZero;
            if (cmpRight is LlvmConstantFP { IsZero: true }) cmpRight = outputZero;
        }
        LlvmValue left = cmpLeft, right = cmpRight;
        if ((flags & LlvmFastMathFlags.NoSignedZeros) == 0 && !PatternKnownNonZero(cmpLeft) && !PatternKnownNonZero(cmpRight))
        { Count(metrics, "select_pattern.floating.signed_zero_sensitive"); return new(LlvmSelectPatternFlavor.Unknown, left, right); }
        bool leftSafe = PatternKnownNonNaN(cmpLeft, flags), rightSafe = PatternKnownNonNaN(cmpRight, flags);
        bool ordered;
        LlvmSelectPatternNaNBehavior behavior;
        if (leftSafe && rightSafe)
        { behavior = LlvmSelectPatternNaNBehavior.ReturnsAny; ordered = PatternIsOrdered(predicate); }
        else if (PatternIsOrdered(predicate))
        {
            ordered = true;
            if (leftSafe) behavior = LlvmSelectPatternNaNBehavior.ReturnsNaN;
            else if (rightSafe) behavior = LlvmSelectPatternNaNBehavior.ReturnsOther;
            else { Count(metrics, "select_pattern.floating.unknown_nan_behavior"); return new(LlvmSelectPatternFlavor.Unknown, left, right); }
        }
        else
        {
            ordered = false;
            if (leftSafe) behavior = LlvmSelectPatternNaNBehavior.ReturnsOther;
            else if (rightSafe) behavior = LlvmSelectPatternNaNBehavior.ReturnsNaN;
            else { Count(metrics, "select_pattern.floating.unknown_nan_behavior"); return new(LlvmSelectPatternFlavor.Unknown, left, right); }
        }
        if (whenTrue == cmpRight && whenFalse == cmpLeft)
        {
            (cmpLeft, cmpRight) = (cmpRight, cmpLeft);
            predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            if (behavior == LlvmSelectPatternNaNBehavior.ReturnsNaN) behavior = LlvmSelectPatternNaNBehavior.ReturnsOther;
            else if (behavior == LlvmSelectPatternNaNBehavior.ReturnsOther) behavior = LlvmSelectPatternNaNBehavior.ReturnsNaN;
            ordered = !ordered;
        }
        if (whenTrue == cmpLeft && whenFalse == cmpRight)
        {
            LlvmSelectPatternResult result = GetSelectPattern(predicate, behavior, ordered);
            Count(metrics, "select_pattern.floating.direct_comparison_form");
            return result with { LHS = left, RHS = right };
        }
        // Integer negation/ABS forms in the shared source cannot bind floating
        // comparison values. The remaining FP form is the fast clamp pattern.
        if (behavior != LlvmSelectPatternNaNBehavior.ReturnsAny ||
            (flags & LlvmFastMathFlags.NoSignedZeros) == 0 && !PatternKnownNonZero(cmpLeft) && !PatternKnownNonZero(cmpRight))
            return new(LlvmSelectPatternFlavor.Unknown, left, right);
        return MatchFastFloatClamp(predicate, cmpLeft, cmpRight, whenTrue, whenFalse, metrics);
    }

    public static bool TryMatchOrdOrUnordFMin(LlvmValue value, out LlvmValue? left, out LlvmValue? right,
        Dictionary<string, ulong>? metrics = null) => TryMatchFloatingMinMaxSelect(value, false, out left, out right, metrics);
    public static bool TryMatchOrdOrUnordFMax(LlvmValue value, out LlvmValue? left, out LlvmValue? right,
        Dictionary<string, ulong>? metrics = null) => TryMatchFloatingMinMaxSelect(value, true, out left, out right, metrics);

    private static bool TryMatchFloatingMinMaxSelect(LlvmValue value, bool maximum, out LlvmValue? left, out LlvmValue? right,
        Dictionary<string, ulong>? metrics)
    {
        left = right = null;
        Count(metrics, "select_pattern.floating.raw_minmax.queries");
        if (value is not LlvmSelectInstruction { Condition: LlvmFCmpInstruction compare } select ||
            !((select.TrueValue == compare.Left && select.FalseValue == compare.Right) || (select.TrueValue == compare.Right && select.FalseValue == compare.Left))) return false;
        LlvmCmpPredicate predicate = compare.Left == select.TrueValue ? compare.Predicate : LlvmCmpInstruction.GetInversePredicate(compare.Predicate);
        bool matches = maximum
            ? predicate is LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUge
            : predicate is LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle;
        if (!matches) return false;
        left = compare.Left; right = compare.Right;
        Count(metrics, "select_pattern.floating.raw_minmax.matched");
        return true;
    }

    private static bool MatchFloatingMinMaxSelect(LlvmValue value, bool maximum, LlvmValue expectedLeft, out LlvmAPFloat constant)
    {
        constant = default;
        if (!TryMatchFloatingMinMaxSelect(value, maximum, out LlvmValue? left, out LlvmValue? right, null) ||
            left != expectedLeft || right is not LlvmConstantFP floating) return false;
        constant = floating.Value; return true;
    }

    private static bool MatchFloatingMinMaxIntrinsic(LlvmValue value, LlvmIntrinsicKind kind, LlvmValue expectedLeft, out LlvmAPFloat constant)
    {
        constant = default;
        if (value is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call || descriptor.Kind != kind ||
            call.GetArgument(0) != expectedLeft || call.GetArgument(1) is not LlvmConstantFP floating) return false;
        constant = floating.Value; return true;
    }

    private static LlvmSelectPatternResult MatchFastFloatClamp(LlvmCmpPredicate predicate, LlvmValue cmpLeft,
        LlvmValue cmpRight, LlvmValue whenTrue, LlvmValue whenFalse, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "select_pattern.floating.clamp.queries");
        if (cmpRight == whenFalse) { (whenTrue, whenFalse) = (whenFalse, whenTrue); predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
        if (cmpRight != whenTrue || cmpRight is not LlvmConstantFP first || !first.Value.IsFinite)
            return new(LlvmSelectPatternFlavor.Unknown, whenTrue, whenFalse);
        bool less = predicate is LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle;
        bool greater = predicate is LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUge;
        if (!less && !greater) return new(LlvmSelectPatternFlavor.Unknown, whenTrue, whenFalse);
        if ((MatchFloatingMinMaxSelect(whenFalse, greater, cmpLeft, out LlvmAPFloat second) ||
             MatchFloatingMinMaxIntrinsic(whenFalse, greater ? LlvmIntrinsicKind.MaxNum : LlvmIntrinsicKind.MinNum, cmpLeft, out second)) &&
            first.Value.Compare(second) == (less ? LlvmFloatCompare.LessThan : LlvmFloatCompare.GreaterThan))
        {
            Count(metrics, "select_pattern.floating.clamp.matched");
            return FloatingPatternResult(less ? LlvmSelectPatternFlavor.FMaxNum : LlvmSelectPatternFlavor.FMinNum,
                whenTrue, whenFalse, LlvmSelectPatternNaNBehavior.ReturnsAny, false);
        }
        return new(LlvmSelectPatternFlavor.Unknown, whenTrue, whenFalse);
    }
}
