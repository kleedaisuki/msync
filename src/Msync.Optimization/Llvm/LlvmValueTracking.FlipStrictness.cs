// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// getFlippedStrictnessPredicateAndConstant, scalar, fixed-vector and scalable-splat constants.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static (LlvmCmpPredicate Predicate, LlvmConstant Constant)? GetFlippedStrictnessPredicateAndConstant(
        LlvmCmpPredicate predicate, LlvmConstant constant, bool sameSign = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.flip_strictness.queries");
        if (constant is LlvmUndefValue) return null;
        bool signed = predicate is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;
        var unsigned = signed ? (LlvmCmpPredicate)((byte)predicate - 4) : predicate;
        bool increment = unsigned is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUgt;
        bool ConstantIsOk(LlvmConstantInt integer)
        {
            var value = integer.Value;
            bool atLimit = increment ? signed ? value.IsMaxSignedValue : value.IsAllOnes : signed ? value.IsMinSignedValue : value.IsZero;
            if (atLimit) { Count(metrics, "value_tracking.flip_strictness.boundary"); return false; }
            if (sameSign && (increment ? signed ? value.IsAllOnes : value.IsMaxSignedValue : signed ? value.IsZero : value.IsMinSignedValue))
            { Count(metrics, "value_tracking.flip_strictness.sign_boundary"); return false; }
            return true;
        }
        LlvmConstantInt? replacement = null;
        if (constant is LlvmConstantInt integer)
        {
            if (!ConstantIsOk(integer)) return null;
        }
        else if (constant.Type is LlvmFixedVectorType vector)
        {
            for (uint lane = 0; lane < vector.NumElements; ++lane)
            {
                Count(metrics, "value_tracking.flip_strictness.lanes");
                var element = constant.GetAggregateElement(lane);
                if (element is LlvmUndefValue) continue;
                if (element is not LlvmConstantInt laneInteger || !ConstantIsOk(laneInteger)) return null;
                replacement ??= laneInteger;
            }
        }
        else if (constant.Type is LlvmVectorType)
        {
            if (constant.GetSplatValue() is not LlvmConstantInt splat || !ConstantIsOk(splat)) return null;
        }
        else return null;
        if (constant.ContainsUndefOrPoisonElement())
        {
            System.Diagnostics.Debug.Assert(replacement is not null);
            constant = LlvmConstant.ReplaceUndefsWith(constant, replacement!);
        }
        var result = predicate switch
        {
            LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUlt,
            LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUle,
            LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUgt,
            LlvmCmpPredicate.ICmpUgt => LlvmCmpPredicate.ICmpUge,
            LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSlt,
            LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSle,
            LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSgt,
            LlvmCmpPredicate.ICmpSgt => LlvmCmpPredicate.ICmpSge,
            _ => throw new InvalidOperationException("Strictness inversion requires a relational integer predicate.")
        };
        Count(metrics, "value_tracking.flip_strictness.changed");
        uint width = constant.Type.GetScalarSizeInBits();
        var step = LlvmConstantInt.Get(constant.Type, increment ? new LlvmAPInt(width, 1) : LlvmAPInt.GetAllOnes(width));
        return (result, LlvmConstantExpr.GetAdd(constant, step));
    }
}
