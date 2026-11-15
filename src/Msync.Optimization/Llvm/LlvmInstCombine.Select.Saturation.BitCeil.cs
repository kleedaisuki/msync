// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp 4148–4302
// isSafeToRemoveBitCeilSelect/foldBitCeil.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool IsSafeToRemoveBitCeilSelect(LlvmCmpPredicate predicate, LlvmValue conditionLeft,
            LlvmAPInt conditionRight, LlvmValue ctlzOperand, uint bitWidth, out bool shouldDropNoWrap)
        {
            var range = LlvmConstantRange.MakeExactICmpRegion(LlvmCmpInstruction.GetInversePredicate(predicate), conditionRight);
            bool drop = false;
            bool MatchForward(LlvmValue ancestor)
            {
                Count("select.bitceil.forward_match");
                if (ctlzOperand == ancestor) return true;
                if (ctlzOperand is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt addConstant } add && add.Left == ancestor)
                { drop = true; range = range.Add(new(addConstant.Value)); return true; }
                if (ctlzOperand is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt subConstant } sub && sub.Right == ancestor)
                { drop = true; range = new LlvmConstantRange(subConstant.Value).Sub(range); return true; }
                if (SelectMatchNot(ctlzOperand, out var inner) && inner == ancestor)
                { range = range.BinaryNot(); return true; }
                return false;
            }
            bool matched = MatchForward(conditionLeft);
            if (!matched && conditionLeft is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt constant } conditionAdd)
            {
                range = range.Sub(new(constant.Value));
                matched = MatchForward(conditionAdd.Left);
            }
            shouldDropNoWrap = drop;
            if (!matched) { Count("select.bitceil.no_common_ancestor"); return false; }
            var one = new LlvmAPInt(bitWidth, 1);
            var max = LlvmAPInt.GetSignedMinValue(bitWidth) - one;
            range = range.Sub(new(one));
            bool safe = range.ICmp(LlvmCmpPredicate.ICmpUge, new LlvmConstantRange(max));
            Count(safe ? "select.bitceil.range_covers_zero_and_negative" : "select.bitceil.range_not_sufficient"); return safe;
        }
        private LlvmInstruction? FoldBitCeil(LlvmSelectInstruction select)
        {
            Count("select.bitceil.candidates"); uint width = select.Type.GetScalarSizeInBits();
            if (!System.Numerics.BitOperations.IsPow2(width)) return null;
            if (select.Condition is not LlvmICmpInstruction { Right: LlvmConstantInt conditionConstant } compare) return null;
            var whenTrue = select.TrueValue; var whenFalse = select.FalseValue; var predicate = compare.Predicate;
            if (SelectIsOne(whenTrue)) { (whenFalse,whenTrue) = (whenTrue,whenFalse); predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
            if (!SelectIsOne(whenFalse) || whenTrue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true } shift || !SelectIsOne(shift.Left) ||
                shift.Right is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true, Left: LlvmConstantInt subConstant } sub ||
                subConstant.Value != new LlvmAPInt(subConstant.Value.BitWidth, width, implicitTrunc: true) ||
                sub.Right is not LlvmCallInstruction count || count.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.CTLZ ||
                !IsSafeToRemoveBitCeilSelect(predicate, compare.Left, conditionConstant.Value, count.GetArgument(0), width, out bool drop)) return null;
            if (drop)
            {
                var operand = (LlvmBinaryOperator)count.GetArgument(0);
                operand.HasNoUnsignedWrap = false; operand.HasNoSignedWrap = false;
                Count("select.bitceil.nowrap_dropped");
            }
            count.DropPoisonGeneratingAnnotations(); Worklist.Add(count); Count("select.bitceil.count_annotations_dropped");
            var negative = Builder.CreateNeg(count);
            var masked = Builder.CreateAnd(negative, LlvmConstantInt.Get((LlvmIntegerType)select.Type, width - 1));
            Count("select.bitceil.rewritten");
            return new LlvmBinaryOperator(LlvmOpcode.Shl, LlvmConstantInt.Get((LlvmIntegerType)select.Type, 1), masked);
        }
    }
}
