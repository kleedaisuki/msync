// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectICmpAndZeroShl/foldSelectICmpLshrAshr/foldSelectZeroOrOnes.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldSelectICmpAndZeroShl(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (!SelectIsZero(compare.Right)) return null;
            LlvmCmpPredicate predicate = compare.Predicate;
            if (predicate == LlvmCmpPredicate.ICmpNe) { predicate = LlvmCmpPredicate.ICmpEq; (whenTrue, whenFalse) = (whenFalse, whenTrue); }
            if (predicate != LlvmCmpPredicate.ICmpEq || compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and ||
                and.Right is not LlvmConstantInt mask || !SelectIsZero(whenTrue) ||
                whenFalse is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shift || !ReferenceEquals(and.Left, shift.Left) ||
                shift.Right is not LlvmConstantInt amount) return null;
            uint ones = mask.Value.CountTrailingOnes();
            if (ones == 0 || ones + mask.Value.CountLeadingZeros() != mask.Value.BitWidth ||
                mask.Value.CountLeadingZeros() != (uint)amount.Value.GetZExtValue()) return null;
            shift.HasNoSignedWrap = false; shift.HasNoUnsignedWrap = false;
            return shift;
        }
        private LlvmValue? FoldSelectICmpLshrAshr(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (compare.Right is not LlvmConstantInt constant) return null;
            LlvmCmpPredicate predicate = compare.Predicate;
            if ((predicate != LlvmCmpPredicate.ICmpSgt || !constant.Value.Sge(LlvmAPInt.GetAllOnes(constant.Value.BitWidth))) &&
                (predicate != LlvmCmpPredicate.ICmpSlt || constant.Value.IsNegative)) return null;
            if (predicate == LlvmCmpPredicate.ICmpSlt) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            if (whenTrue is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr } logical ||
                whenFalse is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr } arithmetic ||
                !ReferenceEquals(logical.Left, arithmetic.Left) || !ReferenceEquals(logical.Right, arithmetic.Right) ||
                !ReferenceEquals(compare.Left, logical.Left)) return null;
            return Builder.CreateBinary(LlvmOpcode.AShr, logical.Left, logical.Right, compare.Name, exact: logical.IsExact && arithmetic.IsExact);
        }
        private LlvmInstruction? FoldSelectZeroOrOnes(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (!compare.HasOneUse || compare.Right is not LlvmConstantInt constant) return null;
            bool matched = compare.Predicate == LlvmCmpPredicate.ICmpUlt && constant.Value == new LlvmAPInt(constant.Value.BitWidth, 2, implicitTrunc: true) &&
                SelectMatchNeg(whenTrue, out LlvmValue value) && ReferenceEquals(value, compare.Left) && SelectIsOnes(whenFalse);
            if (!matched) matched = compare.Predicate == LlvmCmpPredicate.ICmpUgt && constant.Value.IsOne &&
                SelectMatchNeg(whenFalse, out LlvmValue other) && ReferenceEquals(other, compare.Left) && SelectIsOnes(whenTrue);
            if (!matched) return null;
            LlvmValue nonzero = Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, compare.Left, LlvmConstant.GetNullValue(compare.Left.Type));
            return new LlvmCastInstruction(LlvmOpcode.SExt, nonzero, whenTrue.Type);
        }
        private LlvmInstruction? FoldSelectWithExtremeEqCond(LlvmValue left, LlvmValue right, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (!left.Type.IsIntegerTy() || whenFalse is not LlvmICmpInstruction compare) return null;
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue other;
            if (ReferenceEquals(compare.Left, left)) other = compare.Right;
            else if (ReferenceEquals(compare.Right, left)) { other = compare.Left; predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            else return null;
            if (whenTrue is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe } trueCompare || !ReferenceEquals(trueCompare.Left, other)) return null;
            uint width = left.Type.GetIntegerBitWidth();
            LlvmAPInt extreme;
            switch (predicate)
            {
                case LlvmCmpPredicate.ICmpSlt: extreme = LlvmAPInt.GetSignedMinValue(width); break;
                case LlvmCmpPredicate.ICmpUlt: extreme = LlvmAPInt.GetZero(width); break;
                case LlvmCmpPredicate.ICmpSgt: extreme = LlvmAPInt.GetSignedMaxValue(width); break;
                case LlvmCmpPredicate.ICmpUgt: extreme = LlvmAPInt.GetAllOnes(width); break;
                default: return null;
            }
            if (right is not LlvmConstantInt constant || constant.Value != extreme || trueCompare.Right is not LlvmConstantInt trueConstant || trueConstant.Value != extreme) return null;
            return new LlvmICmpInstruction(predicate, left, other) { SameSign = compare.SameSign };
        }
        private LlvmInstruction? FoldSelectICmpEq(LlvmSelectInstruction select, LlvmICmpInstruction compare)
        {
            if (compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)) return null;
            return compare.Predicate == LlvmCmpPredicate.ICmpNe ?
                FoldSelectWithExtremeEqCond(compare.Left, compare.Right, select.FalseValue, select.TrueValue) :
                FoldSelectWithExtremeEqCond(compare.Left, compare.Right, select.TrueValue, select.FalseValue);
        }
    }
}
