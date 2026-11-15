// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// ValueTracking.cpp computeKnownFPClassFromCond / adjustKnownFPClassForSelectArm;
// IR/GenericFloatingPointPredicateUtils.h fcmpImpliesClass (no fabs intrinsics).
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
using P = Msync.Core.Llvm.LlvmCmpPredicate;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownFPClasses
{
    public static (LlvmValue? Value, C Classes) FCmpToClassTest(P predicate,
        LlvmValue left, LlvmValue right, bool lookThroughSource = true)
    {
        if (right is not LlvmConstantFP constant) return (null, C.All);
        LlvmFunction? function = left switch
        { LlvmInstruction instruction => instruction.Parent?.Parent, LlvmArgument argument => argument.Parent, _ => null };
        var (source, ifTrue, ifFalse) = FPPredicateImpliesClass(predicate, function, left, constant.Value, lookThroughSource);
        return source is not null && ifTrue == (C.All & ~ifFalse) ? (source, ifTrue) : (null, C.All);
    }

    private static LlvmKnownFPClass SelectArm(LlvmValue arm, LlvmValue condition, bool invert, C interested,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        var known = Compute(arm, interested, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext);
        var conditional = new LlvmKnownFPClass();
        FromCondition(arm, condition, !invert, ref conditional, metrics, depth + 1);
        conditional = conditional.UnionWith(known);
        if (!conditional.IsUnknown && IsGuaranteedNotToBeUndef(arm, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions))
        {
            Count(metrics, conditional.GetKnownFPClasses() != known.GetKnownFPClasses() || conditional.SignBit != known.SignBit
                ? "known_fp.select.condition_refined" : "known_fp.select.condition_unchanged");
            return conditional;
        }
        return known;
    }

    private static void FromCondition(LlvmValue value, LlvmValue condition, bool whenTrue,
        ref LlvmKnownFPClass known, Dictionary<string, ulong>? metrics, int depth)
    {
        Count(metrics, "known_fp.condition.visited");
        if (depth < MaxDepth && LogicalOperands(condition, whenTrue, out var left, out var right))
        {
            FromCondition(value, left!, whenTrue, ref known, metrics, depth + 1);
            FromCondition(value, right!, whenTrue, ref known, metrics, depth + 1);
            return;
        }
        if (depth < MaxDepth && condition is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } not)
        {
            LlvmValue? inner = not.Right is LlvmConstantInt { Value.IsAllOnes: true } ? not.Left
                : not.Left is LlvmConstantInt { Value.IsAllOnes: true } ? not.Right : null;
            if (inner is not null)
            { FromCondition(value, inner, !whenTrue, ref known, metrics, depth + 1); return; }
        }
        if (condition is LlvmFCmpInstruction { Right: LlvmConstantFP rhs } compare && ReferenceEquals(compare.Left, value))
        {
            var (ifTrue, ifFalse) = CompareImpliesClass(compare.Predicate, rhs.Value);
            known.KnownNot(~(whenTrue ? ifTrue : ifFalse));
        }
        else if (condition is LlvmICmpInstruction { Left: LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast, Right: LlvmConstantInt rhsInt } integer && ReferenceEquals(cast.Operand, value))
        {
            bool? sign = SignBitCheck(integer.Predicate, rhsInt.Value);
            if (sign.HasValue) known.SetSignBit(sign.Value == whenTrue);
        }
    }

    private static bool LogicalOperands(LlvmValue condition, bool and, out LlvmValue? left, out LlvmValue? right)
    {
        left = right = null;
        if (!condition.Type.IsIntegerTy(1)) return false;
        if (condition is LlvmBinaryOperator binary && binary.Opcode == (and ? LlvmOpcode.And : LlvmOpcode.Or))
        { left = binary.Left; right = binary.Right; return true; }
        if (condition is not LlvmSelectInstruction select || !ReferenceEquals(select.Condition.Type, select.Type)) return false;
        if (and && select.FalseValue is LlvmConstantInt { Value.IsZero: true })
        { left = select.Condition; right = select.TrueValue; return true; }
        if (!and && select.TrueValue is LlvmConstantInt { Value.IsAllOnes: true })
        { left = select.Condition; right = select.FalseValue; return true; }
        return false;
    }

    private static bool? SignBitCheck(P predicate, LlvmAPInt rhs) => predicate switch
    {
        P.ICmpSlt when rhs.IsZero => true,
        P.ICmpSle when rhs.IsAllOnes => true,
        P.ICmpSgt when rhs.IsAllOnes => false,
        P.ICmpSge when rhs.IsZero => false,
        P.ICmpUgt when rhs.IsMaxSignedValue => true,
        P.ICmpUge when rhs.IsMinSignedValue => true,
        P.ICmpUlt when rhs.IsMinSignedValue => false,
        P.ICmpUle when rhs.IsMaxSignedValue => false,
        _ => null
    };

    private static (C True, C False) Exact(C classes) => (classes & C.All, ~classes & C.All);
    private static bool IsOrdered(P predicate) => predicate is >= P.FCmpOeq and <= P.FCmpOrd;
    private static bool IsUnordered(P predicate) => predicate is >= P.FCmpUno and <= P.FCmpUne;

    private static (C True, C False) CompareImpliesClass(P predicate, LlvmAPFloat rhs)
    {
        if (predicate == P.FCmpTrue) return Exact(C.All);
        if (predicate == P.FCmpFalse) return Exact(C.None);
        C rhsClass = new LlvmKnownFPClass(rhs).GetKnownFPClasses();
        if (rhs.IsNaN) return Exact(IsOrdered(predicate) ? C.None : C.All);
        if (predicate == P.FCmpOrd) return Exact(~C.NaN);
        if (predicate == P.FCmpUno) return Exact(C.NaN);
        if (!rhs.IsNegative && rhs.BitcastToAPInt().GetZExtValue() == 0x0010000000000000UL)
        {
            C? smallest = predicate switch
            {
                P.FCmpOlt or P.FCmpUge => C.Zero | C.Subnormal | C.NegNormal | C.NegInfinity,
                P.FCmpOge or P.FCmpUlt => C.PosInfinity | C.PosNormal,
                _ => null
            };
            if (smallest.HasValue) return Exact(IsUnordered(predicate) ? ~smallest.Value : smallest.Value);
        }
        if (rhs.IsZero)
            return Exact(predicate switch
            {
                P.FCmpOeq => C.Zero, P.FCmpUeq => C.Zero | C.NaN,
                P.FCmpUne => ~C.Zero, P.FCmpOne => ~C.NaN & ~C.Zero,
                P.FCmpOgt => LlvmKnownFPClass.OrderedGreaterThanZeroMask,
                P.FCmpUgt => LlvmKnownFPClass.OrderedGreaterThanZeroMask | C.NaN,
                P.FCmpOge => C.Positive | C.NegZero, P.FCmpUge => C.Positive | C.NegZero | C.NaN,
                P.FCmpOlt => LlvmKnownFPClass.OrderedLessThanZeroMask,
                P.FCmpUlt => LlvmKnownFPClass.OrderedLessThanZeroMask | C.NaN,
                P.FCmpOle => C.Negative | C.PosZero, P.FCmpUle => C.Negative | C.PosZero | C.NaN,
                _ => throw new InvalidOperationException("Non-comparison predicate in fcmpImpliesClass.")
            });
        if (rhs.IsInfinity)
        {
            C mask = predicate switch
            {
                P.FCmpOeq or P.FCmpUne => rhs.IsNegative ? C.NegInfinity : C.PosInfinity,
                P.FCmpOne or P.FCmpUeq => rhs.IsNegative ? ~C.NegInfinity & ~C.NaN : ~C.PosInfinity & ~C.NaN,
                P.FCmpOlt or P.FCmpUge => rhs.IsNegative ? C.None : C.Finite | C.NegInfinity,
                P.FCmpOge or P.FCmpUlt => rhs.IsNegative ? ~C.NaN : C.PosInfinity,
                P.FCmpOgt or P.FCmpUle => rhs.IsNegative ? ~(C.NegInfinity | C.NaN) : C.None,
                P.FCmpOle or P.FCmpUgt => rhs.IsNegative ? C.NegInfinity : ~C.NaN,
                _ => throw new InvalidOperationException("Non-comparison predicate in fcmpImpliesClass.")
            };
            return Exact(IsUnordered(predicate) ? ~mask : mask);
        }
        if (predicate == P.FCmpOeq) return (rhsClass, C.All);
        if (predicate == P.FCmpUeq) return (rhsClass | C.NaN, C.All & ~C.NaN);
        if (predicate == P.FCmpOne) return (C.All & ~C.NaN, rhsClass | C.NaN);
        if (predicate == P.FCmpUne) return (C.All, rhsClass);
        bool subnormal = (rhsClass & C.Subnormal) != 0;
        C le = rhs.IsNegative ? C.NegInfinity | C.NegNormal : C.Negative | C.PosZero | C.PosSubnormal;
        C ge = rhs.IsNegative ? C.Positive | C.NegZero | C.NegSubnormal : C.PosNormal | C.PosInfinity;
        if (rhs.IsNegative) { if (subnormal) le |= C.NegSubnormal; else ge |= C.NegNormal; }
        else { if (subnormal) ge |= C.PosSubnormal; else le |= C.PosNormal; }
        return predicate switch
        {
            P.FCmpOgt or P.FCmpOge => (ge, C.All & (~ge | rhsClass)),
            P.FCmpUgt or P.FCmpUge => (ge | C.NaN, C.All & (~(ge | C.NaN) | rhsClass)),
            P.FCmpOlt or P.FCmpOle => (le, C.All & (~le | rhsClass)),
            P.FCmpUlt or P.FCmpUle => (le | C.NaN, C.All & (~(le | C.NaN) | rhsClass)),
            _ => (C.All, C.All)
        };
    }
}
