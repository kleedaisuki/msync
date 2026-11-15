// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp canonicalizeClampLike.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? CanonicalizeClampLike(LlvmSelectInstruction outer, LlvmICmpInstruction outerCompare)
        {
            LlvmValue value = outer.TrueValue, innerValue = outer.FalseValue;
            if (!outerCompare.HasOneUse || outerCompare.Right is not LlvmConstantInt outerConstant) return null;
            LlvmCmpPredicate predicate = outerCompare.Predicate;
            if (!SelectMatchLike(innerValue, out _, out _, out _))
            { predicate = LlvmCmpInstruction.GetInversePredicate(predicate); (value, innerValue) = (innerValue, value); }
            LlvmAPInt bound = outerConstant.Value, one = new(bound.BitWidth, 1, implicitTrunc: true);
            switch (predicate)
            {
                case LlvmCmpPredicate.ICmpUlt: case LlvmCmpPredicate.ICmpUge:
                    if (bound.IsZero) return null;
                    break;
                case LlvmCmpPredicate.ICmpUle: case LlvmCmpPredicate.ICmpUgt:
                    if (bound.IsAllOnes) return null;
                    predicate = predicate == LlvmCmpPredicate.ICmpUle ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge;
                    bound += one;
                    break;
                default: return null;
            }
            if (!innerValue.HasOneUse) return null;
            LlvmValue comparedValue = outerCompare.Left;
            if (!ReferenceEquals(comparedValue.Type, value.Type) && value.HasOneUse && value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc)
                value = trunc.Operand;
            LlvmAPInt offset;
            if (ReferenceEquals(comparedValue, value)) offset = LlvmAPInt.GetZero(value.Type.GetIntegerBitWidth());
            else if (comparedValue is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add && ReferenceEquals(add.Left, value) && add.Right is LlvmConstantInt c1)
                offset = c1.Value;
            else return null;
            if (!SelectMatchLike(innerValue, out LlvmValue innerCondition, out LlvmValue replaceLow, out LlvmValue replaceHigh) ||
                innerCondition is not LlvmICmpInstruction innerCompare ||
                !ReferenceEquals(innerCompare.Left, value) || innerCompare.Right is not LlvmConstantInt innerConstant) return null;
            if (!innerCompare.HasOneUse && (ReferenceEquals(comparedValue, value) || !comparedValue.HasOneUse)) return null;
            LlvmAPInt middle = innerConstant.Value;
            switch (innerCompare.Predicate)
            {
                case LlvmCmpPredicate.ICmpSlt: break;
                case LlvmCmpPredicate.ICmpSgt:
                    if (middle.IsMaxSignedValue) return null;
                    middle += one;
                    (replaceLow, replaceHigh) = (replaceHigh, replaceLow);
                    break;
                case LlvmCmpPredicate.ICmpSge: (replaceLow, replaceHigh) = (replaceHigh, replaceLow); break;
                default: return null;
            }
            LlvmAPInt low = -offset, high = bound - offset;
            if (predicate == LlvmCmpPredicate.ICmpUge) (low, high) = (high, low);
            if (!middle.Sge(low) || !middle.Sle(high)) return null;
            if (!ReferenceEquals(value.Type, outer.Type))
            {
                if (replaceLow is not LlvmConstant lowConstant || replaceHigh is not LlvmConstant highConstant) return null;
                replaceLow = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.SExt, lowConstant, value.Type, DL, Metrics)!;
                replaceHigh = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.SExt, highConstant, value.Type, DL, Metrics)!;
            }
            LlvmValue below = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, low));
            LlvmValue above = Builder.CreateICmp(LlvmCmpPredicate.ICmpSge, value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, high));
            LlvmValue replacedLow = Builder.CreateSelect(below, replaceLow, value);
            LlvmValue replacedHigh = Builder.CreateSelect(above, replaceHigh, replacedLow);
            Count("select.clamp.rewrites");
            return Builder.CreateTrunc(replacedHigh, outer.Type);
        }
    }
}
