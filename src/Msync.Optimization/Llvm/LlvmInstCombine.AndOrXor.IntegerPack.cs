// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineAndOrXor.cpp matchSubIntegerPackFromVector/foldIntegerPackFromVector.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldIntegerPackFromVector(LlvmInstruction instruction)
        {
            Count("integer_pack.queries");
            LlvmAPInt? mask = null;
            if (!MatchSubIntegerPackFromVector(instruction.GetOperand(0)!, out var left, out long leftOffset, ref mask) ||
                !MatchSubIntegerPackFromVector(instruction.GetOperand(1)!, out var right, out long rightOffset, ref mask) ||
                left != right || leftOffset != rightOffset) return null;
            uint zeroIndex = ((LlvmFixedVectorType)left!.Type).NumElements;
            int[] shuffle = new int[mask!.Value.BitWidth];
            for (uint index = 0; index < shuffle.Length; ++index)
                shuffle[index] = DemandedElement(mask.Value, index) ? unchecked((int)(leftOffset + index)) : (int)zeroIndex;
            var packed = Builder.CreateShuffleVector(left, LlvmConstant.GetNullValue(left.Type), shuffle, instruction.Name + ".v");
            Count("integer_pack.rewrites");
            return new LlvmCastInstruction(LlvmOpcode.BitCast, packed, instruction.Type);
        }

        private bool MatchSubIntegerPackFromVector(LlvmValue value, out LlvmValue? vector, out long vectorOffset, ref LlvmAPInt? mask)
        {
            vector = null; vectorOffset = 0;
            LlvmValue extracted = value;
            ulong shiftAmount = 0;
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt shifted } shift && shifted.Value.TryZExtValue() is { } shiftValue)
            { extracted = shift.Left; shiftAmount = shiftValue; }
            if (extracted is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extension) extracted = extension.Operand;
            if (extracted is LlvmExtractElementInstruction { IndexOperand: LlvmConstantInt index } extract && index.Value.TryZExtValue() is { } vectorIndex)
            {
                vector = extract.VectorOperand;
                if (vector.Type is not LlvmFixedVectorType type || type.ElementType is not LlvmIntegerType element) return false;
                uint elementWidth = element.BitWidth, targetWidth = value.Type.GetIntegerBitWidth();
                if (targetWidth % elementWidth != 0 || shiftAmount % elementWidth != 0) return false;
                uint count = targetWidth / elementWidth;
                uint shiftedElements = (uint)(shiftAmount / elementWidth);
                uint position = DL.IsLittleEndian ? shiftedElements : count - shiftedElements - 1;
                vectorOffset = unchecked((long)vectorIndex - (long)position);
                mask = SetDemandedElement(mask?.ZExtOrTrunc(count) ?? LlvmAPInt.GetZero(count), position);
                return true;
            }
            if (value is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, Operand: LlvmInstruction source } || source.Type is not LlvmFixedVectorType sourceType) return false;
            mask = mask?.ZExtOrTrunc(sourceType.NumElements) ?? LlvmAPInt.GetZero(sourceType.NumElements);
            if (source is LlvmShuffleVectorInstruction { Right: LlvmConstant constant } shuffle)
            {
                vector = shuffle.Left;
                if (vector.Type is not LlvmFixedVectorType type) return false;
                bool foundOffset = false;
                for (uint lane = 0; lane < shuffle.ShuffleMask.Count; ++lane)
                {
                    int selected = shuffle.GetMaskValue((int)lane);
                    if (selected == LlvmShuffleVectorInstruction.PoisonMaskElem) return false;
                    if (selected >= type.NumElements)
                    {
                        if (constant.GetAggregateElement((uint)selected - type.NumElements) is not LlvmConstantInt { IsZero: true }) return false;
                        continue;
                    }
                    if (foundOffset)
                    { if (vectorOffset + lane != selected) return false; }
                    else
                    {
                        if (selected < lane) return false;
                        vectorOffset = selected - (long)lane;
                        foundOffset = true;
                    }
                    mask = SetDemandedElement(mask.Value, lane);
                }
                return foundOffset;
            }
            if (source is not LlvmInsertElementInstruction { InsertedElementOperand: LlvmConstant { IsNullValue: true }, IndexOperand: LlvmConstantInt insertedIndex } insert ||
                insertedIndex.Value.TryZExtValue() is not { } inserted || insert.VectorOperand.Type is not LlvmFixedVectorType) return false;
            vector = insert.VectorOperand;
            bool alreadyIncluded = DemandedElement(mask.Value, (uint)inserted);
            mask = LlvmAPInt.GetAllOnes(mask.Value.BitWidth);
            if (!alreadyIncluded) mask = ClearDemandedElement(mask.Value, (uint)inserted);
            return true;
        }
    }
}
