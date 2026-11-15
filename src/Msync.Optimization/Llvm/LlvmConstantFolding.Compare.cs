// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantFold.cpp
// ConstantFoldCompareInstruction and scalar select; Instructions.cpp ICmp/FCmp compare.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldCompareInstOperands(LlvmCmpPredicate predicate, LlvmConstant left,
        LlvmConstant right, LlvmDataLayout layout, LlvmFunction? contextFunction = null,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.compare.queries");
        // Analysis/ConstantFolding.cpp FlushFPConstant has Dynamic mode without
        // function context; represented functions have no DenormalFPEnv attribute
        // and therefore use Function::getDenormalMode's default IEEE mode.
        if ((byte)predicate <= 15 && contextFunction is null && (IsDenormal(left) || IsDenormal(right)))
        { Count(metrics, "constant_fold.compare.dynamic_denormal"); return null; }
        LlvmConstant? result = FoldExpressionCompare(predicate, left, right, layout, contextFunction, metrics);
        result ??= ConstantFoldCompareInstruction(predicate, left, right);
        RecordResult(metrics, "constant_fold.compare", result);
        return result;
    }
    public static LlvmConstant? ConstantFoldCompareInstruction(LlvmCmpPredicate predicate, LlvmConstant left, LlvmConstant right)
    {
        LlvmType resultType = LlvmType.GetInt1Ty(left.Context);
        if (left.Type.IsVectorTy()) return FoldVectorCompare(predicate, left, right);
        if (predicate == LlvmCmpPredicate.FCmpFalse) return LlvmConstantInt.GetFalse(left.Context);
        if (predicate == LlvmCmpPredicate.FCmpTrue) return LlvmConstantInt.GetTrue(left.Context);
        if (left is LlvmPoisonValue || right is LlvmPoisonValue) return LlvmPoisonValue.Get(resultType);
        if (left is LlvmUndefValue || right is LlvmUndefValue)
        {
            bool isInteger = (byte)predicate >= 32;
            if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe || (isInteger && ReferenceEquals(left, right)))
                return LlvmUndefValue.Get(resultType);
            bool value = isInteger ? predicate is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUle or
                LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle : (byte)predicate >= 8;
            return LlvmConstantInt.GetBool(left.Context, value);
        }
        if (right.IsNullValue)
        {
            if (predicate == LlvmCmpPredicate.ICmpUge) return LlvmConstantInt.GetTrue(left.Context);
            if (predicate == LlvmCmpPredicate.ICmpUlt) return LlvmConstantInt.GetFalse(left.Context);
        }
        if (left.Type.IsIntegerTy(1))
        {
            if (predicate == LlvmCmpPredicate.ICmpEq)
                return left is LlvmConstantExpr ? LlvmConstantExpr.GetXor(left, LlvmConstantExpr.GetNot(right))
                    : LlvmConstantExpr.GetXor(LlvmConstantExpr.GetNot(left), right);
            if (predicate == LlvmCmpPredicate.ICmpNe) return LlvmConstantExpr.GetXor(left, right);
        }
        if (left is LlvmConstantInt a && right is LlvmConstantInt b)
            return LlvmConstantInt.GetBool(left.Context, CompareInteger(a.Value, b.Value, predicate));
        if (left is LlvmConstantFP x && right is LlvmConstantFP y)
            return LlvmConstantInt.GetBool(left.Context, CompareFloat(x.Value, y.Value, predicate));
        if (ReferenceEquals(left, right))
        {
            if (left.Type.IsFPOrFPVectorTy())
            {
                if (predicate == LlvmCmpPredicate.FCmpOne) return LlvmConstantInt.GetFalse(left.Context);
                if (predicate == LlvmCmpPredicate.FCmpUeq) return LlvmConstantInt.GetTrue(left.Context);
            }
            else return LlvmConstantInt.GetBool(left.Context, predicate is LlvmCmpPredicate.ICmpEq or
                LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle);
        }
        if (left.Type.IsPointerTy())
        {
            LlvmCmpPredicate? relation = EvaluatePointerRelation(left, right);
            if (relation is { } rel)
            {
                if (RelationImplies(rel, predicate)) return LlvmConstantInt.GetTrue(left.Context);
                if (RelationImplies(rel, LlvmCmpInstruction.GetInversePredicate(predicate))) return LlvmConstantInt.GetFalse(left.Context);
            }
        }
        return null;
    }
    private static LlvmCmpPredicate? EvaluatePointerRelation(LlvmConstant left, LlvmConstant right)
    {
        if (ReferenceEquals(left, right)) return LlvmCmpPredicate.ICmpEq;
        if (left is not LlvmGlobalValue && right is LlvmGlobalValue)
        {
            LlvmCmpPredicate? swapped = EvaluatePointerRelation(right, left);
            return swapped is { } rel ? LlvmCmpInstruction.GetSwappedPredicate(rel) : null;
        }
        if (left is not LlvmGlobalValue global) return null;
        if (right is LlvmGlobalValue other)
            return !GlobalUnsafeForEquality(global) && !GlobalUnsafeForEquality(other) ? LlvmCmpPredicate.ICmpNe : null;
        if (right is LlvmConstantPointerNull && global.Linkage != LlvmLinkage.ExternalWeakLinkage &&
            ((LlvmPointerType)global.Type).AddressSpace == 0) return LlvmCmpPredicate.ICmpUgt;
        return null;
    }
    private static bool GlobalUnsafeForEquality(LlvmGlobalValue value)
    {
        // Globals.cpp::isInterposable(false): NoIPA is deliberately ignored;
        // Module has no flags, so getSemanticInterposition's default is false.
        if (value.Linkage is LlvmLinkage.WeakAnyLinkage or LlvmLinkage.LinkOnceAnyLinkage or
            LlvmLinkage.CommonLinkage or LlvmLinkage.ExternalWeakLinkage || value.UnnamedAddr == LlvmUnnamedAddr.Global) return true;
        return value is LlvmGlobalVariable global && (!global.ValueType.IsSized() || global.ValueType.IsEmptyTy());
    }
    private static bool RelationImplies(LlvmCmpPredicate relation, LlvmCmpPredicate predicate)
    {
        // Instructions.cpp::isImpliedTrueByMatchingCmp, no SameSign annotation.
        if (relation == predicate) return true;
        return relation switch
        {
            LlvmCmpPredicate.ICmpEq => predicate is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle,
            LlvmCmpPredicate.ICmpUgt => predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUge,
            LlvmCmpPredicate.ICmpUlt => predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUle,
            LlvmCmpPredicate.ICmpSgt => predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSge,
            LlvmCmpPredicate.ICmpSlt => predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSle,
            _ => false
        };
    }
    public static bool CompareInteger(LlvmAPInt left, LlvmAPInt right, LlvmCmpPredicate predicate) => predicate switch
    {
        LlvmCmpPredicate.ICmpEq => left == right, LlvmCmpPredicate.ICmpNe => left != right,
        LlvmCmpPredicate.ICmpUgt => left.Ugt(right), LlvmCmpPredicate.ICmpUge => left.Uge(right),
        LlvmCmpPredicate.ICmpUlt => left.Ult(right), LlvmCmpPredicate.ICmpUle => left.Ule(right),
        LlvmCmpPredicate.ICmpSgt => left.Sgt(right), LlvmCmpPredicate.ICmpSge => left.Sge(right),
        LlvmCmpPredicate.ICmpSlt => left.Slt(right), LlvmCmpPredicate.ICmpSle => left.Sle(right),
        _ => throw new UnreachableException("Unexpected non-integer predicate")
    };
    public static bool CompareFloat(LlvmAPFloat left, LlvmAPFloat right, LlvmCmpPredicate predicate)
    {
        LlvmFloatCompare result = left.Compare(right);
        return predicate switch
        {
            LlvmCmpPredicate.FCmpFalse => false, LlvmCmpPredicate.FCmpTrue => true,
            LlvmCmpPredicate.FCmpUno => result == LlvmFloatCompare.Unordered,
            LlvmCmpPredicate.FCmpOrd => result != LlvmFloatCompare.Unordered,
            LlvmCmpPredicate.FCmpUeq => result is LlvmFloatCompare.Unordered or LlvmFloatCompare.Equal,
            LlvmCmpPredicate.FCmpOeq => result == LlvmFloatCompare.Equal,
            LlvmCmpPredicate.FCmpUne => result != LlvmFloatCompare.Equal,
            LlvmCmpPredicate.FCmpOne => result is LlvmFloatCompare.LessThan or LlvmFloatCompare.GreaterThan,
            LlvmCmpPredicate.FCmpUlt => result is LlvmFloatCompare.Unordered or LlvmFloatCompare.LessThan,
            LlvmCmpPredicate.FCmpOlt => result == LlvmFloatCompare.LessThan,
            LlvmCmpPredicate.FCmpUgt => result is LlvmFloatCompare.Unordered or LlvmFloatCompare.GreaterThan,
            LlvmCmpPredicate.FCmpOgt => result == LlvmFloatCompare.GreaterThan,
            LlvmCmpPredicate.FCmpUle => result != LlvmFloatCompare.GreaterThan,
            LlvmCmpPredicate.FCmpOle => result is LlvmFloatCompare.LessThan or LlvmFloatCompare.Equal,
            LlvmCmpPredicate.FCmpUge => result != LlvmFloatCompare.LessThan,
            LlvmCmpPredicate.FCmpOge => result is LlvmFloatCompare.GreaterThan or LlvmFloatCompare.Equal,
            _ => throw new UnreachableException("Invalid FCmp predicate")
        };
    }
    public static LlvmConstant? ConstantFoldSelectInstruction(LlvmConstant condition, LlvmConstant whenTrue,
        LlvmConstant whenFalse, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.select.queries");
        if (condition.IsNullValue) return whenFalse;
        if (condition is LlvmConstantInt { Value.IsAllOnes: true }) return whenTrue;
        if (condition.Type.IsVectorTy()) return null;
        if (condition is LlvmPoisonValue) return LlvmPoisonValue.Get(whenTrue.Type);
        if (condition is LlvmUndefValue) return whenTrue is LlvmUndefValue ? whenTrue : whenFalse;
        if (ReferenceEquals(whenTrue, whenFalse)) return whenTrue;
        if (whenTrue is LlvmPoisonValue) return whenFalse;
        if (whenFalse is LlvmPoisonValue) return whenTrue;
        if (whenTrue is LlvmUndefValue && SelectNotPoison(whenFalse)) return whenFalse;
        if (whenFalse is LlvmUndefValue && SelectNotPoison(whenTrue)) return whenTrue;
        return null;
    }
    private static bool SelectNotPoison(LlvmConstant value) => value is LlvmConstantInt or LlvmGlobalVariable or
        LlvmConstantFP or LlvmConstantPointerNull or LlvmFunction;
    private static bool IsDenormal(LlvmConstant value) => value is LlvmConstantFP fp &&
        (fp.Value.BitcastToAPInt().GetZExtValue() & 0x7ff0000000000000UL) == 0 && !fp.Value.IsZero;
}
