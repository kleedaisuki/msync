// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// GenericFloatingPointPredicateUtils.h fcmpToClassTest/fcmpImpliesClass;
// FloatingPointPredicateUtils.cpp SSA matchers; FloatingPointMode.cpp inverse_fabs.
using System.Diagnostics;
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
using P = Msync.Core.Llvm.LlvmCmpPredicate;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownFPClasses
{
    public static bool LookThroughFAbs(LlvmValue value, out LlvmValue source)
    {
        if (value is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.FAbs)
        { source = call.GetArgument(0); return true; }
        source = value; return false;
    }
    public static C InverseFAbs(C mask)
    {
        C result = mask & C.NaN;
        if ((mask & C.PosZero) != 0) result |= C.Zero;
        if ((mask & C.PosSubnormal) != 0) result |= C.Subnormal;
        if ((mask & C.PosNormal) != 0) result |= C.Normal;
        if ((mask & C.PosInfinity) != 0) result |= C.Infinity;
        return result;
    }
    public static (LlvmValue? Value, C Classes) FCmpToClassTest(P predicate, LlvmFunction function,
        LlvmValue left, LlvmValue right, bool lookThroughSource = true, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "fp_predicate.queries");
        if (right is not LlvmConstantFP constant)
        { Count(metrics, "fp_predicate.nonconstant_rhs"); return (null, C.All); }
        var (source, ifTrue, ifFalse) = FPPredicateImpliesClass(predicate, function, left, constant.Value, lookThroughSource, metrics);
        bool exact = source is not null && ifTrue == (C.All & ~ifFalse);
        Count(metrics, exact ? "fp_predicate.exact" : "fp_predicate.not_exact");
        return exact ? (source, ifTrue) : (null, C.All);
    }
    public static (LlvmValue? Value, C True, C False) FCmpImpliesClass(P predicate, LlvmFunction function,
        LlvmValue left, LlvmValue right, bool lookThroughSource = true, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "fp_predicate.implied_queries");
        return right is LlvmConstantFP constant ? FPPredicateImpliesClass(predicate, function, left, constant.Value, lookThroughSource, metrics) : (null, C.All, C.All);
    }

    private static (LlvmValue? Value, C True, C False) FPPredicateExact(LlvmValue source, C classes)
        => (source, classes & C.All, C.All & ~classes);
    private static (LlvmValue? Value, C True, C False) FPPredicateImpliesClass(P predicate, LlvmFunction? function,
        LlvmValue left, LlvmAPFloat right, bool lookThroughSource, Dictionary<string, ulong>? metrics = null)
    {
        if (!right.IsNegative && right.IsSmallestNormalized)
        {
            LlvmValue source = left;
            bool abs = lookThroughSource && LookThroughFAbs(left, out source);
            if (abs) Count(metrics, "fp_predicate.fabs_lookthrough");
            Count(metrics, "fp_predicate.smallest_normal");
            C mask;
            switch (predicate)
            {
                case P.FCmpOlt: case P.FCmpUge:
                    mask = C.Zero | C.Subnormal;
                    if (!abs) mask |= C.NegNormal | C.NegInfinity;
                    break;
                case P.FCmpOge: case P.FCmpUlt:
                    mask = C.PosInfinity | C.PosNormal;
                    if (abs) mask |= C.NegInfinity | C.NegNormal;
                    break;
                default:
                    return FPPredicateImpliesClass(predicate, function, left, new LlvmKnownFPClass(right).GetKnownFPClasses(), lookThroughSource, metrics);
            }
            if (IsUnordered(predicate)) mask = C.All & ~mask;
            return FPPredicateExact(source, mask);
        }
        return FPPredicateImpliesClass(predicate, function, left, new LlvmKnownFPClass(right).GetKnownFPClasses(), lookThroughSource, metrics);
    }
    private static (LlvmValue? Value, C True, C False) FPPredicateImpliesClass(P predicate, LlvmFunction? function,
        LlvmValue left, C rightClass, bool lookThroughSource, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(rightClass != C.None);
        LlvmValue source = left;
        if (predicate == P.FCmpTrue) return FPPredicateExact(source, C.All);
        if (predicate == P.FCmpFalse) return FPPredicateExact(source, C.None);
        C originalClass = rightClass;
        bool negative = (rightClass & C.Negative) == rightClass, positive = (rightClass & C.Positive) == rightClass;
        bool nan = (rightClass & ~C.NaN) == C.None;
        if (nan) return FPPredicateExact(source, IsOrdered(predicate) ? C.None : C.All);
        if (predicate == P.FCmpOrd) return FPPredicateExact(source, C.All & ~C.NaN);
        if (predicate == P.FCmpUno) return FPPredicateExact(source, C.NaN);
        bool abs = lookThroughSource && LookThroughFAbs(left, out source);
        if (abs) Count(metrics, "fp_predicate.fabs_lookthrough");
        if (abs) rightClass = InverseFAbs(rightClass);
        bool zero = (originalClass & C.Zero) == originalClass;
        if (zero)
        {
            if (function is not null && function.GetDenormalMode(left.Type).Input != LlvmDenormalMode.IEEE)
            { Count(metrics, "fp_predicate.zero.non_ieee_inputs"); return (null, C.All, C.All); }
            C mask = predicate switch
            {
                P.FCmpOeq => C.Zero,
                P.FCmpUeq => C.Zero | C.NaN,
                P.FCmpUne => C.All & ~C.Zero,
                P.FCmpOne => C.All & ~(C.NaN | C.Zero),
                P.FCmpOgt => C.PosSubnormal | C.PosNormal | C.PosInfinity,
                P.FCmpUgt => C.PosSubnormal | C.PosNormal | C.PosInfinity | C.NaN,
                P.FCmpOge => C.Positive | C.NegZero,
                P.FCmpUge => C.Positive | C.NegZero | C.NaN,
                P.FCmpOlt => C.NegSubnormal | C.NegNormal | C.NegInfinity,
                P.FCmpUlt => C.NegSubnormal | C.NegNormal | C.NegInfinity | C.NaN,
                P.FCmpOle => C.Negative | C.PosZero,
                P.FCmpUle => C.Negative | C.PosZero | C.NaN,
                _ => throw new UnreachableException("Unexpected floating predicate")
            };
            if (abs && predicate is not (P.FCmpOeq or P.FCmpUeq or P.FCmpUne or P.FCmpOne)) mask = InverseFAbs(mask);
            return FPPredicateExact(source, mask);
        }
        bool denormal = (originalClass & C.Subnormal) == originalClass;
        bool infinity = (originalClass & C.Infinity) == originalClass;
        if (infinity)
        {
            C mask;
            switch (predicate)
            {
                case P.FCmpOeq: case P.FCmpUne:
                    mask = negative ? abs ? C.None : C.NegInfinity : abs ? C.Infinity : C.PosInfinity;
                    break;
                case P.FCmpOne: case P.FCmpUeq:
                    mask = negative ? C.All & ~(abs ? C.NaN : C.NegInfinity | C.NaN) :
                        C.All & ~(abs ? C.Infinity | C.NaN : C.PosInfinity | C.NaN);
                    break;
                case P.FCmpOlt: case P.FCmpUge:
                    mask = negative ? C.None : C.Finite | (abs ? C.None : C.NegInfinity);
                    break;
                case P.FCmpOge: case P.FCmpUlt:
                    mask = negative ? C.All & ~C.NaN : abs ? C.Infinity : C.PosInfinity;
                    break;
                case P.FCmpOgt: case P.FCmpUle:
                    mask = negative ? C.All & ~(abs ? C.NaN : C.NegInfinity | C.NaN) : C.None;
                    break;
                case P.FCmpOle: case P.FCmpUgt:
                    mask = negative ? abs ? C.None : C.NegInfinity : C.All & ~C.NaN;
                    break;
                default: throw new UnreachableException("Unexpected floating predicate");
            }
            if (IsUnordered(predicate)) mask = C.All & ~mask;
            return FPPredicateExact(source, mask);
        }
        if (predicate == P.FCmpOeq) return (source, rightClass, C.All);
        if (predicate == P.FCmpUeq) return (source, rightClass | C.NaN, C.All & ~C.NaN);
        if (predicate == P.FCmpOne) return (source, C.All & ~C.NaN, rightClass | C.NaN);
        if (predicate == P.FCmpUne) return (source, C.All, rightClass);
        if (negative)
        {
            if (abs)
            {
                return predicate switch
                {
                    P.FCmpOgt or P.FCmpOge => FPPredicateExact(source, C.All & ~C.NaN),
                    P.FCmpUgt or P.FCmpUge => FPPredicateExact(source, C.All),
                    P.FCmpOlt or P.FCmpOle => FPPredicateExact(source, C.None),
                    P.FCmpUlt or P.FCmpUle => FPPredicateExact(source, C.NaN),
                    _ => (null, C.All, C.All)
                };
            }
            C less = C.NegInfinity | C.NegNormal, greater = C.Positive | C.NegZero | C.NegSubnormal;
            if (denormal) less |= C.NegSubnormal;
            else greater |= C.NegNormal;
            return FPPredicateRelational(predicate, source, rightClass, less, greater);
        }
        if (positive)
        {
            C greater = C.PosNormal | C.PosInfinity, less = C.Negative | C.PosZero | C.PosSubnormal;
            if (denormal) greater |= C.PosSubnormal;
            else less |= C.PosNormal;
            if (abs) { greater = InverseFAbs(greater); less = InverseFAbs(less); }
            return FPPredicateRelational(predicate, source, rightClass, less, greater);
        }
        return (null, C.All, C.All);
    }
    private static (LlvmValue? Value, C True, C False) FPPredicateRelational(P predicate, LlvmValue source,
        C rightClass, C less, C greater) => predicate switch
    {
        P.FCmpOgt or P.FCmpOge => (source, greater, C.All & ~greater | rightClass),
        P.FCmpUgt or P.FCmpUge => (source, greater | C.NaN, C.All & ~(greater | C.NaN) | rightClass),
        P.FCmpOlt or P.FCmpOle => (source, less, C.All & ~less | rightClass),
        P.FCmpUlt or P.FCmpUle => (source, less | C.NaN, C.All & ~(less | C.NaN) | rightClass),
        _ => (null, C.All, C.All)
    };
}
