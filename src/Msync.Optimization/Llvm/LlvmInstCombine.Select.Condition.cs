// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// visitSelectInst FoldSelectWithAndOrCond and trunc/sign canonicalizations.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectWithAndOrCondition(LlvmSelectInstruction select)
        {
            LlvmInstruction? Fold(bool isAnd, LlvmValue a, LlvmValue b)
            {
                if (LlvmInstructionSimplify.SimplifySelect(b, select.TrueValue, select.FalseValue, select.FastMathFlags, Query(select)) is { } simplified)
                    return new LlvmSelectInstruction(a, isAnd ? simplified : select.TrueValue, isAnd ? select.FalseValue : simplified);
                if (select.Condition.HasOneUse && select.Type.IsIntegerTy() && b is LlvmICmpInstruction compare &&
                    CanonicalizeSPF(compare, select.TrueValue, select.FalseValue) is { } pattern)
                    return new LlvmSelectInstruction(a, isAnd ? pattern : select.TrueValue, isAnd ? select.FalseValue : pattern);
                return null;
            }
            if (select.Condition is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and)
                return Fold(true, and.Left, and.Right) ?? Fold(true, and.Right, and.Left);
            if (select.Condition is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or)
                return Fold(false, or.Left, or.Right) ?? Fold(false, or.Right, or.Left);
            if (SelectMatchLogical(select.Condition, true, out LlvmValue left, out LlvmValue right)) return Fold(true, left, right);
            if (SelectMatchLogical(select.Condition, false, out left, out right)) return Fold(false, left, right);
            return null;
        }
        private LlvmInstruction? FoldSelectTruncCondition(LlvmSelectInstruction select)
        {
            LlvmValue condition = select.Condition, whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc)
            {
                LlvmValue operand = trunc.Operand;
                if (trunc.HasNoUnsignedWrap && operand is not LlvmConstant)
                {
                    if (ReferenceEquals(whenTrue, operand)) return ReplaceOperand(select, 1, LlvmConstantInt.Get((LlvmIntegerType)whenTrue.Type, 1));
                    if (ReferenceEquals(whenFalse, operand)) return ReplaceOperand(select, 2, LlvmConstant.GetNullValue(whenFalse.Type));
                }
                if (trunc.HasNoSignedWrap && operand is not LlvmConstant)
                {
                    if (ReferenceEquals(whenTrue, operand)) return ReplaceOperand(select, 1, LlvmConstantInt.Get((LlvmIntegerType)whenTrue.Type, LlvmAPInt.GetAllOnes(whenTrue.Type.GetIntegerBitWidth())));
                    if (ReferenceEquals(whenFalse, operand)) return ReplaceOperand(select, 2, LlvmConstant.GetNullValue(whenFalse.Type));
                }
                if (ReferenceEquals(operand.Type, select.Type))
                {
                    if (SelectIsZero(whenFalse) && LlvmValueTracking.ImpliesPoison(whenTrue, condition, Metrics) &&
                        LlvmKnownBitsAnalysis.Compute(whenTrue, context: select, tree: DT, metrics: Metrics, assumptions: AC, conditions: SQ.DC).CountMaxActiveBits() == 1)
                        return new LlvmBinaryOperator(LlvmOpcode.And, operand, whenTrue);
                    if (trunc.HasNoUnsignedWrap && SelectIsOne(whenTrue) && LlvmValueTracking.ImpliesPoison(whenFalse, condition, Metrics) &&
                        LlvmKnownBitsAnalysis.Compute(whenFalse, context: select, tree: DT, metrics: Metrics, assumptions: AC, conditions: SQ.DC).CountMaxActiveBits() == 1)
                        return new LlvmBinaryOperator(LlvmOpcode.Or, operand, whenFalse);
                }
            }
            if (condition is not LlvmICmpInstruction compare) return null;
            uint width = select.Type.GetScalarSizeInBits();
            bool MatchShift(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt amount } shift &&
                ReferenceEquals(shift.Left, compare.Left) && amount.Value == new LlvmAPInt(amount.Value.BitWidth, width - 1, implicitTrunc: true);
            if ((compare.Predicate == LlvmCmpPredicate.ICmpSlt && SelectIsOne(compare.Right) && MatchShift(whenTrue) && SelectIsOne(whenFalse)) ||
                (compare.Predicate == LlvmCmpPredicate.ICmpSgt && SelectIsZero(compare.Right) && SelectIsOne(whenTrue) && MatchShift(whenFalse)))
                return LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.SCmp, [compare.Left, LlvmConstant.GetNullValue(select.Type)], (LlvmIntegerType)select.Type, F.Parent!, Metrics);
            return null;
        }
    }
}
