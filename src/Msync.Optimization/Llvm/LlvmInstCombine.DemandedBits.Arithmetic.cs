// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedUseBits Add/Sub/Mul.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmInstruction DisableDemandedWrapFlags(LlvmBinaryOperator instruction, uint leadingZeros)
        {
            if (leadingZeros != 0) { instruction.HasNoSignedWrap = false; instruction.HasNoUnsignedWrap = false; }
            return instruction;
        }

        private LlvmValue? SimplifyDemandedArithmetic(LlvmBinaryOperator instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            uint width = demanded.BitWidth, leading = demanded.CountLeadingZeros();
            var fromOperands = LlvmAPInt.GetLowBitsSet(width, width - leading);
            LlvmKnownBits left = new(width), right = new(width);
            if (instruction.Opcode == LlvmOpcode.Add)
            {
                if ((demanded & new LlvmAPInt(width, 1)).IsZero && SimplifyDemandedBooleanAdd(instruction) is { } booleanResult) return booleanResult;
                if (ShrinkDemandedConstant(instruction, 1, fromOperands) ||
                    SimplifyDemandedBits(instruction, 1, fromOperands, ref right, query, depth + 1)) return DisableDemandedWrapFlags(instruction, leading);
                uint trailing = (~demanded & right.Zero).CountTrailingOnes();
                var fromLeft = fromOperands.ClearBits(0, trailing);
                if (ShrinkDemandedConstant(instruction, 0, fromLeft) ||
                    SimplifyDemandedBits(instruction, 0, fromLeft, ref left, query, depth + 1)) return DisableDemandedWrapFlags(instruction, leading);
                uint leftTrailing = (~demanded & left.Zero).CountTrailingOnes();
                if (ShrinkDemandedConstant(instruction, 1, fromOperands.ClearBits(0, leftTrailing))) return DisableDemandedWrapFlags(instruction, leading);
                if (DemandedSubset(fromOperands, right.Zero)) return instruction.Left;
                if (DemandedSubset(fromOperands, left.Zero)) return instruction.Right;
                if (instruction.Right is LlvmConstantInt constant && constant.Value == LlvmAPInt.GetOneBitSet(width, demanded.GetActiveBits() - 1))
                {
                    using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                    Builder.SetInsertPoint(instruction);
                    return Builder.CreateXor(instruction.Left, constant);
                }
                known = LlvmKnownBits.AddSub(true, instruction.HasNoSignedWrap, instruction.HasNoUnsignedWrap, left, right);
                return null;
            }
            if (instruction.Opcode == LlvmOpcode.Sub)
            {
                if (ShrinkDemandedConstant(instruction, 1, fromOperands) ||
                    SimplifyDemandedBits(instruction, 1, fromOperands, ref right, query, depth + 1) ||
                    ShrinkDemandedConstant(instruction, 0, fromOperands) ||
                    SimplifyDemandedBits(instruction, 0, fromOperands, ref left, query, depth + 1)) return DisableDemandedWrapFlags(instruction, leading);
                if (DemandedSubset(fromOperands, right.Zero)) return instruction.Left;
                if (fromOperands.IsOne && DemandedSubset(fromOperands, left.Zero)) return instruction.Right;
                if (instruction.Left is LlvmConstantInt mask && mask.Value.CountTrailingOnes() == mask.Value.GetActiveBits() &&
                    !mask.Value.IsZero && DemandedSubset(fromOperands, mask.Value))
                {
                    using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                    Builder.SetInsertPoint(instruction);
                    return Builder.CreateNot(instruction.Right);
                }
                known = LlvmKnownBits.AddSub(false, instruction.HasNoSignedWrap, instruction.HasNoUnsignedWrap, left, right);
                return null;
            }
            if (ShrinkDemandedConstant(instruction, 0, fromOperands) ||
                SimplifyDemandedBits(instruction, 0, fromOperands, ref left, query, depth + 1) ||
                ShrinkDemandedConstant(instruction, 1, fromOperands) ||
                SimplifyDemandedBits(instruction, 1, fromOperands, ref right, query, depth + 1)) return DisableDemandedWrapFlags(instruction, leading);
            if (demanded.IsPowerOf2 && instruction.Right is LlvmConstantInt factor && factor.Value.CountTrailingZeros() == demanded.CountTrailingZeros())
                return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.Shl, instruction.Left,
                    LlvmConstantInt.Get((LlvmIntegerType)instruction.Type, demanded.CountTrailingZeros())), instruction);
            if (instruction.Left == instruction.Right && demanded.GetLimitedValue() < 4)
                return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.And, instruction.Left,
                    LlvmConstantInt.Get((LlvmIntegerType)instruction.Type, 1)), instruction);
            known = ComputeDemandedKnownBits(instruction, query, depth);
            return null;
        }

        private LlvmValue? SimplifyDemandedBooleanAdd(LlvmBinaryOperator instruction)
        {
            LlvmCastInstruction? zero = instruction.Left as LlvmCastInstruction;
            LlvmCastInstruction? sign = instruction.Right as LlvmCastInstruction;
            if (zero?.Opcode != LlvmOpcode.ZExt || sign?.Opcode != LlvmOpcode.SExt) (zero, sign) = (sign, zero);
            if (zero is { Opcode: LlvmOpcode.ZExt, HasOneUse: true } && sign is { Opcode: LlvmOpcode.SExt, HasOneUse: true } &&
                zero.Operand.Type.IsIntegerTy(1) && zero.Operand.Type == sign.Operand.Type)
            {
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                Builder.SetInsertPoint(instruction);
                return Builder.CreateSExt(Builder.CreateAnd(Builder.CreateNot(zero.Operand), sign.Operand), instruction.Type);
            }
            if (instruction.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt } left ||
                instruction.Right is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt } right ||
                !left.Operand.Type.IsIntegerTy(1) || left.Operand.Type != right.Operand.Type || (!left.HasOneUse && !right.HasOneUse)) return null;
            using var secondGuard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            Builder.SetInsertPoint(instruction);
            return Builder.CreateSExt(Builder.CreateOr(left.Operand, right.Operand), instruction.Type);
        }

        private LlvmValue? SimplifyDemandedDivision(LlvmBinaryOperator instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            uint width = demanded.BitWidth;
            LlvmKnownBits left = new(width);
            if (instruction.Right is LlvmConstantInt constant)
            {
                if (instruction.Opcode == LlvmOpcode.UDiv)
                {
                    var input = LlvmAPInt.GetHighBitsSet(width, width - constant.Value.CountTrailingZeros());
                    if (SimplifyDemandedBits(instruction, 0, input, ref left, query, depth + 1))
                    { instruction.DropPoisonGeneratingFlags(); return instruction; }
                    known = LlvmKnownBits.UDiv(left, LlvmKnownBits.MakeConstant(constant.Value), instruction.IsExact);
                    return null;
                }
                if (constant.Value.IsPowerOf2)
                {
                    if (demanded.Ult(constant.Value)) return instruction.Left;
                    var input = (constant.Value - new LlvmAPInt(width, 1)) | LlvmAPInt.GetSignedMinValue(width);
                    if (SimplifyDemandedBits(instruction, 0, input, ref left, query, depth + 1)) return instruction;
                    known = LlvmKnownBits.SRem(left, LlvmKnownBits.MakeConstant(constant.Value));
                    return null;
                }
            }
            known = ComputeDemandedKnownBits(instruction, query, depth);
            return null;
        }
    }
}
