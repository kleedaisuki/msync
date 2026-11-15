// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectToCmp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectToCmp(LlvmSelectInstruction select)
        {
            if (select.Condition is not LlvmICmpInstruction compare || !compare.Left.Type.IsIntegerTy()) return null;
            LlvmValue whenTrue = select.TrueValue, whenFalse = select.FalseValue, left = compare.Left, right = compare.Right;
            LlvmCmpPredicate predicate = compare.Predicate;
            if (whenTrue is not LlvmConstant)
            {
                if (whenFalse is not LlvmConstant) return null;
                predicate = LlvmCmpInstruction.GetInversePredicate(predicate); (whenTrue, whenFalse) = (whenFalse, whenTrue);
            }
            if (predicate is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSge)
            {
                if (right is not LlvmConstant constant) return null;
                var flipped = LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, constant, compare.SameSign);
                if (!flipped.HasValue) return null;
                predicate = flipped.Value.Predicate; right = flipped.Value.Constant;
            }
            static bool IsLess(LlvmCmpPredicate p) => p is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSlt;
            static bool IsGreater(LlvmCmpPredicate p) => p is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSgt;
            static bool IsSigned(LlvmCmpPredicate p) => p is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;
            if ((IsGreater(predicate) && SelectIsOnes(whenTrue)) || (IsLess(predicate) && SelectIsOne(whenTrue)))
            { predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); (left, right) = (right, left); }
            bool signed = IsSigned(predicate), replace = false;
            bool MatchComparison(LlvmValue value, out LlvmCmpPredicate matched)
            {
                matched = default;
                if (value is not LlvmICmpInstruction cmp) return false;
                if (ReferenceEquals(cmp.Left, left) && ReferenceEquals(cmp.Right, right)) { matched = cmp.Predicate; return true; }
                if (ReferenceEquals(cmp.Left, right) && ReferenceEquals(cmp.Right, left))
                { matched = LlvmCmpInstruction.GetSwappedPredicate(cmp.Predicate); return true; }
                return false;
            }
            if (IsLess(predicate) && SelectIsOnes(whenTrue) && whenFalse is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } zext &&
                MatchComparison(zext.Operand, out LlvmCmpPredicate extended) &&
                (extended == LlvmCmpPredicate.ICmpNe || LlvmCmpInstruction.GetSwappedPredicate(extended) == predicate)) replace = true;
            if (IsGreater(predicate) && SelectIsOne(whenTrue) && whenFalse is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext &&
                MatchComparison(sext.Operand, out extended) &&
                (extended == LlvmCmpPredicate.ICmpNe || LlvmCmpInstruction.GetSwappedPredicate(extended) == predicate)) replace = true;
            if (predicate == LlvmCmpPredicate.ICmpEq && SelectIsZero(whenTrue) && whenFalse is LlvmSelectInstruction inner &&
                MatchComparison(inner.Condition, out LlvmCmpPredicate innerPredicate) && inner.TrueValue is LlvmConstantInt tc && inner.FalseValue is LlvmConstantInt fc)
            {
                LlvmAPInt t = tc.Value, f = fc.Value;
                if (!IsGreater(innerPredicate)) { innerPredicate = LlvmCmpInstruction.GetSwappedPredicate(innerPredicate); (left, right) = (right, left); }
                if (!t.IsOne) { (t, f) = (f, t); (left, right) = (right, left); }
                if (IsGreater(innerPredicate) && t.IsOne && f.IsAllOnes) { signed = IsSigned(innerPredicate); replace = true; }
            }
            if (predicate == LlvmCmpPredicate.ICmpEq && SelectIsZero(whenTrue) && right is LlvmConstantInt c &&
                whenFalse is LlvmSelectInstruction constantInner && constantInner.Condition is LlvmICmpInstruction innerCmp &&
                ReferenceEquals(innerCmp.Left, left) && constantInner.TrueValue is LlvmConstantInt it && constantInner.FalseValue is LlvmConstantInt iff)
            {
                LlvmAPInt one = new(c.Value.BitWidth, 1, implicitTrunc: true);
                if (IsGreater(innerCmp.Predicate) && it.Value.IsOne && iff.Value.IsAllOnes)
                {
                    signed = IsSigned(innerCmp.Predicate);
                    bool canSubtract = signed ? !c.Value.IsMinSignedValue : !c.Value.IsMinValue;
                    if (canSubtract && innerCmp.Right is LlvmConstantInt bound && bound.Value == c.Value - one) replace = true;
                }
                if (IsLess(innerCmp.Predicate) && it.Value.IsAllOnes && iff.Value.IsOne)
                {
                    signed = IsSigned(innerCmp.Predicate);
                    bool canAdd = signed ? !c.Value.IsMaxSignedValue : !c.Value.IsMaxValue;
                    if (canAdd && innerCmp.Right is LlvmConstantInt bound && bound.Value == c.Value + one) replace = true;
                }
            }
            if (!replace) return null;
            LlvmValue result = Builder.CreateIntrinsic(signed ? LlvmIntrinsicKind.SCmp : LlvmIntrinsicKind.UCmp, [left, right], (LlvmIntegerType)select.Type);
            return ReplaceInstUsesWith(select, result);
        }
    }
}
