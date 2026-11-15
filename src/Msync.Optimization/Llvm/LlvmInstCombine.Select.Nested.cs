// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSelect.cpp foldSelectOfSymmetricSelect/foldNestedSelects.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectOfSymmetricSelect(LlvmSelectInstruction outer)
        {
            if (outer.TrueValue is not LlvmSelectInstruction whenTrue || !whenTrue.HasOneUse ||
                outer.FalseValue is not LlvmSelectInstruction whenFalse || !whenFalse.HasOneUse ||
                !ReferenceEquals(whenTrue.Condition, whenFalse.Condition) ||
                !ReferenceEquals(whenTrue.TrueValue, whenFalse.FalseValue) ||
                !ReferenceEquals(whenTrue.FalseValue, whenFalse.TrueValue) ||
                !ReferenceEquals(outer.Condition.Type, whenTrue.Condition.Type)) return null;
            LlvmValue xor = Builder.CreateXor(whenTrue.Condition, outer.Condition);
            Count("select.symmetric.rewrites");
            return new LlvmSelectInstruction(xor, whenTrue.FalseValue, whenTrue.TrueValue);
        }
        private LlvmInstruction? FoldNestedSelects(LlvmSelectInstruction outer)
        {
            LlvmValue outerCondition = outer.Condition, outerTrue = outer.TrueValue, outerFalse = outer.FalseValue;
            if (SelectMatchNot(outerCondition, out LlvmValue uninverted))
            { outerCondition = uninverted; (outerTrue, outerFalse) = (outerFalse, outerTrue); }
            bool isAnd = SelectMatchLogical(outerCondition, true, out LlvmValue left, out LlvmValue right);
            if (!isAnd && !SelectMatchLogical(outerCondition, false, out left, out right)) return null;
            LlvmValue innerValue = isAnd ? outerFalse : outerTrue;
            if (!outer.Condition.HasOneUse && !innerValue.HasOneUse) return null;
            if (innerValue is not LlvmSelectInstruction inner) return null;
            LlvmValue innerCondition = inner.Condition, innerTrue = inner.TrueValue, innerFalse = inner.FalseValue;
            bool swappedInner = false;
            if (SelectMatchNot(innerCondition, out uninverted))
            { innerCondition = uninverted; (innerTrue, innerFalse) = (innerFalse, innerTrue); swappedInner = true; }
            LlvmValue alternative;
            if (ReferenceEquals(left, innerCondition)) alternative = right;
            else if (ReferenceEquals(right, innerCondition)) alternative = left;
            else if (SelectMatchNot(left, out LlvmValue inverse) && ReferenceEquals(inverse, innerCondition))
            {
                alternative = right; (innerTrue, innerFalse) = (innerFalse, innerTrue); innerCondition = left; swappedInner = !swappedInner;
            }
            else if (SelectMatchNot(right, out inverse) && ReferenceEquals(inverse, innerCondition))
            {
                alternative = left; (innerTrue, innerFalse) = (innerFalse, innerTrue); innerCondition = right; swappedInner = !swappedInner;
            }
            else return null;
            LlvmValue newInner = Builder.CreateSelect(alternative, isAnd ? outerTrue : innerFalse, isAnd ? innerTrue : outerFalse);
            newInner.TakeName(innerValue);
            Count("select.nested.rewrites");
            // Branch profile metadata is not represented; swappedInner records
            // the source choice without inventing an independent probability.
            _ = swappedInner;
            return new LlvmSelectInstruction(innerCondition, isAnd ? newInner : innerTrue, isAnd ? innerFalse : newInner);
        }
    }
}
