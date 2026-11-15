// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectICmpAndAnd/foldSelectAndOrPowerOfTwo.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectICmpAndAnd(LlvmType type, LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            LlvmValue x, y; LlvmCmpPredicate predicate;
            uint replaced = 1U + (condition.HasOneUse ? 1U : 0U);
            if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc)
            { x = trunc.Operand; y = LlvmConstantInt.Get((LlvmIntegerType)x.Type, 1); predicate = LlvmCmpPredicate.ICmpNe; }
            else if (condition is LlvmICmpInstruction compare && compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe &&
                compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && SelectIsZero(compare.Right))
            { x = and.Left; y = and.Right; predicate = compare.Predicate; replaced += condition.HasOneUse && and.HasOneUse ? 1U : 0U; }
            else return null;
            if (predicate == LlvmCmpPredicate.ICmpNe) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            if (!SelectIsOne(whenFalse) || whenTrue is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } trueAnd || !SelectIsOne(trueAnd.Right)) return null;
            LlvmValue a = trueAnd.Left; LlvmValue? z = null;
            bool Match(LlvmValue input)
            {
                if (ReferenceEquals(a, input)) return true;
                if (a is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt amount } shift ||
                    !ReferenceEquals(shift.Left, input) || !amount.Value.Ult(new(amount.Value.BitWidth, type.GetScalarSizeInBits(), implicitTrunc: true))) return false;
                z = shift.Right; return true;
            }
            if (!Match(x)) { (x, y) = (y, x); if (!Match(x)) return null; }
            bool hasShift = !ReferenceEquals(a, x);
            if (whenTrue.HasOneUse) replaced += 1U + (hasShift && a.HasOneUse ? 1U : 0U);
            if (replaced < 4U - (y is LlvmConstant ? 1U : 0U)) return null;
            LlvmValue one = LlvmConstantInt.Get((LlvmIntegerType)type, 1);
            LlvmValue mask = hasShift ? Builder.CreateBinary(LlvmOpcode.Shl, one, z!) : one;
            LlvmValue fullMask = Builder.CreateOr(y, mask);
            LlvmValue masked = Builder.CreateAnd(x, fullMask);
            LlvmValue nonzero = Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, masked, LlvmConstant.GetNullValue(masked.Type));
            return new LlvmCastInstruction(LlvmOpcode.ZExt, nonzero, type);
        }
        private LlvmInstruction? FoldSelectAndOrPowerOfTwo(LlvmSelectInstruction select)
        {
            if (!select.Condition.HasOneUse || select.Condition is not LlvmBinaryOperator condition) return null;
            LlvmValue shift; bool isAnd;
            if (SelectIsZero(select.TrueValue) && condition.Opcode == LlvmOpcode.Or) { shift = select.FalseValue; isAnd = false; }
            else if (SelectIsZero(select.FalseValue) && condition.Opcode == LlvmOpcode.And) { shift = select.TrueValue; isAnd = true; }
            else return null;
            LlvmValue? Match(LlvmValue value)
            {
                if (value is not LlvmICmpInstruction compare || compare.Predicate != (isAnd ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq) ||
                    !SelectIsZero(compare.Right) || compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and) return null;
                if (ReferenceEquals(and.Left, shift)) return and.Right;
                return ReferenceEquals(and.Right, shift) ? and.Left : null;
            }
            LlvmValue? a = Match(condition.Left), b = Match(condition.Right);
            if (a is null || b is null || !LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(shift, true, context: select, tree: DT, metrics: Metrics, assumptions: AC, conditions: SQ.DC)) return null;
            return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAnd(shift, a), b);
        }
    }
}
