// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectICmpAnd, foldSelectICmpAndBinOp, foldSelectBitTest.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue SelectShift(LlvmOpcode opcode, LlvmValue value, uint amount) =>
            Builder.CreateBinary(opcode, value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, amount));
        private LlvmValue? FoldSelectICmpAnd(LlvmSelectInstruction select, LlvmValue condition, LlvmValue whenTrue,
            LlvmValue whenFalse, LlvmValue value, LlvmAPInt andMask, bool createAnd)
        {
            if (whenTrue is not LlvmConstantInt trueConstant || whenFalse is not LlvmConstantInt falseConstant) return null;
            LlvmAPInt tc = trueConstant.Value, fc = falseConstant.Value;
            if (!tc.IsZero && !fc.IsZero)
            {
                if (tc.BitWidth != andMask.BitWidth || (createAnd && !condition.HasOneUse)) return null;
                LlvmConstant mask = LlvmConstantInt.Get((LlvmIntegerType)select.Type, andMask);
                foreach (LlvmOpcode opcode in new[] { LlvmOpcode.Or, LlvmOpcode.Xor, LlvmOpcode.Add, LlvmOpcode.Sub })
                    if (ReferenceEquals(LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, trueConstant, mask, DL, Metrics), falseConstant))
                    {
                        if (createAnd) value = Builder.CreateAnd(value, mask);
                        return Builder.CreateBinOp(opcode, trueConstant, value);
                    }
                return null;
            }
            if (!tc.IsPowerOf2 && !fc.IsPowerOf2) return null;
            LlvmAPInt selected = !tc.IsZero ? tc : fc;
            uint valueZeros = selected.GetActiveBits() - 1, andZeros = andMask.GetActiveBits() - 1;
            bool invert = !tc.IsZero, needShift = valueZeros != andZeros;
            bool cast = select.Type.GetScalarSizeInBits() != value.Type.GetScalarSizeInBits();
            if ((createAnd ? 1 : 0) + (invert ? 1 : 0) + (needShift ? 1 : 0) + (cast ? 1 : 0) > 1 + (condition.HasOneUse ? 1 : 0)) return null;
            if (createAnd) value = Builder.CreateAnd(value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, andMask));
            if (valueZeros > andZeros)
            { value = Builder.CreateZExtOrTrunc(value, select.Type); value = SelectShift(LlvmOpcode.Shl, value, valueZeros - andZeros); }
            else if (valueZeros < andZeros)
            { value = SelectShift(LlvmOpcode.LShr, value, andZeros - valueZeros); value = Builder.CreateZExtOrTrunc(value, select.Type); }
            else value = Builder.CreateZExtOrTrunc(value, select.Type);
            return invert ? Builder.CreateXor(value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, selected)) : value;
        }
        private LlvmValue? FoldSelectICmpAndBinOp(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse,
            LlvmValue value, LlvmAPInt andMask, bool createAnd)
        {
            if (!whenTrue.Type.IsIntegerTy()) return null;
            LlvmBinaryOperator binary; LlvmValue other; LlvmAPInt constant; bool invert;
            if (whenFalse is LlvmBinaryOperator falseOp && ReferenceEquals(falseOp.Left, whenTrue) &&
                falseOp.Right is LlvmConstantInt fc && fc.Value.IsPowerOf2)
            { binary = falseOp; other = whenTrue; constant = fc.Value; invert = false; }
            else if (whenTrue is LlvmBinaryOperator trueOp && ReferenceEquals(trueOp.Left, whenFalse) &&
                trueOp.Right is LlvmConstantInt tc && tc.Value.IsPowerOf2)
            { binary = trueOp; other = whenFalse; constant = tc.Value; invert = true; }
            else return null;
            if (LlvmConstantFolding.BinOpIdentity(binary.Opcode, binary.Type, true) is not LlvmConstantInt { IsZero: true }) return null;
            uint c1Log = andMask.GetActiveBits() - 1, c2Log = constant.GetActiveBits() - 1;
            bool shift = c1Log != c2Log, cast = other.Type.GetScalarSizeInBits() != value.Type.GetScalarSizeInBits();
            if (andMask.IsOne && constant.IsNegative) createAnd = false;
            if ((shift ? 1 : 0) + (invert ? 1 : 0) + (cast ? 1 : 0) + (createAnd ? 1 : 0) >
                (condition.HasOneUse ? 1 : 0) + (binary.HasOneUse ? 1 : 0)) return null;
            if (createAnd) value = Builder.CreateAnd(value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, andMask));
            if (c2Log > c1Log)
            { value = Builder.CreateZExtOrTrunc(value, other.Type); value = SelectShift(LlvmOpcode.Shl, value, c2Log - c1Log); }
            else if (c1Log > c2Log)
            { value = SelectShift(LlvmOpcode.LShr, value, c1Log - c2Log); value = Builder.CreateZExtOrTrunc(value, other.Type); }
            else value = Builder.CreateZExtOrTrunc(value, other.Type);
            if (invert) value = Builder.CreateXor(value, LlvmConstantInt.Get((LlvmIntegerType)value.Type, constant));
            LlvmValue result = Builder.CreateBinOp(binary.Opcode, other, value);
            if (result is LlvmBinaryOperator folded) folded.CopyIRFlags(binary);
            return result;
        }
        private LlvmValue? FoldSelectBitTest(LlvmSelectInstruction select, LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            LlvmValue value; LlvmAPInt mask; bool createAnd = false; LlvmCmpPredicate predicate;
            if (condition is LlvmICmpInstruction compare)
            {
                predicate = compare.Predicate;
                if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)
                {
                    if (!SelectIsZero(compare.Right) || compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and ||
                        and.Right is not LlvmConstantInt c || !c.Value.IsPowerOf2) return null;
                    value = compare.Left; mask = c.Value;
                }
                else if (LlvmCmpInstAnalysis.DecomposeBitTestICmp(compare.Left, compare.Right, predicate) is { } test)
                {
                    value = test.X; mask = test.Mask;
                    LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(value, context: select, tree: DT, metrics: Metrics,
                        assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: SQ.DC);
                    mask &= known.GetMaxValue();
                    if (!mask.IsPowerOf2) return null;
                    predicate = test.Pred; createAnd = true;
                }
                else return null;
            }
            else if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc)
            {
                value = trunc.Operand; mask = new(value.Type.GetScalarSizeInBits(), 1);
                predicate = LlvmCmpPredicate.ICmpNe; createAnd = !trunc.HasNoUnsignedWrap;
            }
            else return null;
            if (predicate == LlvmCmpPredicate.ICmpNe) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            return FoldSelectICmpAnd(select, condition, whenTrue, whenFalse, value, mask, createAnd) ??
                FoldSelectICmpAndBinOp(condition, whenTrue, whenFalse, value, mask, createAnd);
        }
    }
}
