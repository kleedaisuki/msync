// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectInstWithICmp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectInstWithICmp(LlvmSelectInstruction select, LlvmICmpInstruction compare)
        {
            if (CanonicalizeSPF(compare, select.TrueValue, select.FalseValue) is { } pattern) return ReplaceInstUsesWith(select, pattern);
            if (FoldSelectInstWithICmpConst(select, compare) is { } constant) return ReplaceInstUsesWith(select, constant);
            if (CanonicalizeClampLike(select, compare) is { } clamp) return ReplaceInstUsesWith(select, clamp);
            if (TryToReuseConstantFromSelectInComparison(select, compare) is { } reused) return reused;
            if (FoldICmpUSubSatWithAndForMostSignificantBitCmp(select, compare) is { } saturated) return saturated;
            LlvmValue whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            if (FoldSelectICmpEq(select, compare) is { } equality) return equality;
            if (compare.Predicate == LlvmCmpPredicate.ICmpSgt && SelectIsOnes(compare.Right) &&
                whenTrue is not LlvmConstant && whenFalse is not LlvmConstant && compare.HasOneUse)
            {
                LlvmTransformBuilder.InsertionPoint saved = Builder.SaveInsertionPoint();
                try
                {
                    Builder.SetInsertPoint(select);
                    LlvmValue negative = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, compare.Left, LlvmConstant.GetNullValue(compare.Left.Type), compare.Name);
                    ReplaceOperand(select, 0, negative); ReplaceOperand(select, 1, whenFalse); ReplaceOperand(select, 2, whenTrue);
                    return select;
                }
                finally { Builder.RestoreInsertionPoint(saved); }
            }
            if (FoldSelectICmpMinMax(compare, whenTrue, whenFalse, Query(select)) is { } minmax) return ReplaceInstUsesWith(select, minmax);
            if (FoldSelectICmpAndZeroShl(compare, whenTrue, whenFalse) is { } shift) return ReplaceInstUsesWith(select, shift);
            if (FoldSelectCtlzToCttz(compare, whenTrue, whenFalse) is { } count) return count;
            if (FoldSelectZeroOrOnes(compare, whenTrue, whenFalse) is { } zeroOnes) return zeroOnes;
            if (FoldSelectICmpLshrAshr(compare, whenTrue, whenFalse) is { } signShift) return ReplaceInstUsesWith(select, signShift);
            if (FoldSelectCttzCtlz(compare, whenTrue, whenFalse) is { } zeroCount) return ReplaceInstUsesWith(select, zeroCount);
            if (CanonicalizeSaturatedSubtract(compare, whenTrue, whenFalse) is { } satSub) return ReplaceInstUsesWith(select, satSub);
            if (CanonicalizeSaturatedAdd(compare, whenTrue, whenFalse) is { } satAdd) return ReplaceInstUsesWith(select, satAdd);
            if (FoldAbsDiff(compare, whenTrue, whenFalse) is { } difference) return ReplaceInstUsesWith(select, difference);
            if (FoldSelectWithConstOpToBinOp(compare, whenTrue, whenFalse) is { } operation) return ReplaceInstUsesWith(select, operation);
            return null;
        }
    }
}
