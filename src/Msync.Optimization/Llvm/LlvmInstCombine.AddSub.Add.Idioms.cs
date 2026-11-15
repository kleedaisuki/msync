// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp visitAdd scalar idioms.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldAddSubAlignUp(LlvmValue left, LlvmValue right, LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                var masked = side == 0 ? left : right; var value = side == 0 ? right : left;
                if (masked is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and && ReferenceEquals(AddSubNegated(and.Left), value) && MatchAddSubLowMask(and.Right, out var mask))
                    return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAdd(value, LlvmConstantInt.Get(value.Context, mask)), LlvmConstantInt.Get(value.Context, ~mask));
            }
            if (right is LlvmConstantInt { IsOne: true } && left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true,
                    Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsMinusOne: true } } add } or && MatchAddSubLowMask(or.Right, out var lowMask))
                return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAdd(add.Left, LlvmConstantInt.Get(add.Context, lowMask)), LlvmConstantInt.Get(add.Context, ~lowMask));
            return null;
        }
        private LlvmInstruction? FoldAddSubLowBit(LlvmBinaryOperator instruction)
        {
            if (instruction.Right is not LlvmConstantInt { IsMinusOne: true } || instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and) return null;
            LlvmValue? value = and.Left.HasOneUse && ReferenceEquals(AddSubNegated(and.Left), and.Right) ? and.Right :
                and.Right.HasOneUse && ReferenceEquals(AddSubNegated(and.Right), and.Left) ? and.Left : null;
            if (value is null) return null;
            return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAdd(value, instruction.Right), Builder.CreateXor(value, instruction.Right));
        }
        private LlvmInstruction? FoldAddSubNotProduct(LlvmValue candidate, LlvmValue value, LlvmBinaryOperator instruction)
        {
            if (!candidate.HasOneUse || !Negator.MatchNot(candidate, out var not) || not is not LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true, Right: LlvmConstantInt coefficient } product ||
                !ReferenceEquals(product.Left, value)) return null;
            var one = new LlvmAPInt(coefficient.Value.BitWidth, 1);
            return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateMul(value, LlvmConstantInt.Get(value.Context, one - coefficient.Value)), LlvmConstantInt.Get(value.Context, LlvmAPInt.GetAllOnes(coefficient.Value.BitWidth)));
        }
        private LlvmInstruction? FoldAddSubNegativePowerProduct(LlvmValue candidate, LlvmValue other, LlvmBinaryOperator instruction)
        {
            if (candidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true, Right: LlvmConstantInt coefficient } product || !(-coefficient.Value).IsPowerOf2) return null;
            return new LlvmBinaryOperator(LlvmOpcode.Sub, other, Builder.CreateBinary(LlvmOpcode.Shl, product.Left, AddSubInt(instruction, coefficient.Value.CountTrailingZeros())));
        }
        private LlvmInstruction? FoldAddSubSignum(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt shift } arithmetic ||
                shift.Value != new LlvmAPInt(shift.Value.BitWidth, instruction.Type.GetScalarSizeInBits() - 1) ||
                instruction.Right is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true,
                    Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpSgt, HasOneUse: true, Right: LlvmConstantInt { IsZero: true } } compare } ||
                !ReferenceEquals(compare.Left, arithmetic.Left)) return null;
            var nonzero = Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, arithmetic.Left, AddSubInt(arithmetic.Left, 0));
            return new LlvmBinaryOperator(LlvmOpcode.Or, arithmetic, Builder.CreateZExt(nonzero, instruction.Type));
        }
        private LlvmInstruction? FoldAddSubConditionalIncrement(LlvmValue value, LlvmValue extended, LlvmBinaryOperator instruction)
        {
            if (extended is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt, HasOneUse: true,
                    Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq, Right: LlvmConstant constant } condition } cast ||
                !ReferenceEquals(condition.Left, value) || !IsAddSubImmediateConstant(constant)) return null;
            var one = AddSubInt(value, 1);
            var increment = cast.Opcode == LlvmOpcode.ZExt ? LlvmConstantExpr.GetAdd(constant, one) : LlvmConstantExpr.GetSub(constant, one);
            return ReplaceInstUsesWith(instruction, Builder.CreateSelect(condition, increment, value));
        }
        private LlvmInstruction? FoldAddSubToUMax(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsOne: true } } add ||
                instruction.Right is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true,
                    Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe, HasOneUse: true, Right: LlvmConstantInt { IsZero: true } } compare } ||
                !ReferenceEquals(add.Left, compare.Left)) return null;
            return ReplaceInstUsesWith(instruction, Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.UMax, add.Left, AddSubInt(add.Left, 1)));
        }
        private LlvmInstruction? FoldAddSubShiftCeiling(LlvmValue extended, LlvmValue shifted, LlvmBinaryOperator instruction)
        {
            if (extended is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt,
                    Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And } and, Right: LlvmConstantInt { IsZero: true } } } ||
                !MatchAddSubLowMask(and.Right, out var mask) || !shifted.HasOneUse ||
                SkipAddSubCast(shifted, LlvmOpcode.ZExt) is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt amount } shift ||
                !ReferenceEquals(and.Left, shift.Left) || new LlvmAPInt(amount.Value.BitWidth, mask.PopCount()) != amount.Value) return null;
            var value = shift.Left;
            uint valueWidth = value.Type.GetScalarSizeInBits(), width = instruction.Type.GetScalarSizeInBits();
            bool extend = width > valueWidth;
            var maskConstant = LlvmConstantInt.Get(instruction.Context, mask.ZExt(width));
            if (!extend && !WillNotOverflowUnsignedAdd(value, maskConstant, instruction)) return null;
            if (extend) value = Builder.CreateZExt(value, instruction.Type);
            var add = Builder.CreateAdd(value, maskConstant, nuw: true);
            return new LlvmBinaryOperator(LlvmOpcode.LShr, add, LlvmConstantInt.Get(instruction.Context, amount.Value.ZExt(width)));
        }
        private LlvmInstruction? FoldAddSubOrNegativeTwo(LlvmValue candidate, LlvmValue value, LlvmBinaryOperator instruction)
        {
            var negativeTwo = new LlvmAPInt(instruction.Type.GetScalarSizeInBits(), unchecked((ulong)-2L), isSigned: true);
            if (candidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true, Right: LlvmConstantInt constant } or ||
                !ReferenceEquals(or.Left, value) || constant.Value != negativeTwo) return null;
            return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAdd(value, LlvmConstantInt.Get(value.Context, LlvmAPInt.GetAllOnes(negativeTwo.BitWidth))), constant);
        }
    }
}
