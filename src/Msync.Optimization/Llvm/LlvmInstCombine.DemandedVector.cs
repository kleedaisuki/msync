// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedVectorElts.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool DemandedElement(LlvmAPInt mask, uint index) => !(mask & LlvmAPInt.GetOneBitSet(mask.BitWidth, index)).IsZero;
        private static LlvmAPInt SetDemandedElement(LlvmAPInt mask, uint index) => mask | LlvmAPInt.GetOneBitSet(mask.BitWidth, index);
        private static LlvmAPInt ClearDemandedElement(LlvmAPInt mask, uint index) => mask & ~LlvmAPInt.GetOneBitSet(mask.BitWidth, index);

        private bool SimplifyDemandedVectorOperand(LlvmInstruction instruction, int operand, LlvmAPInt demanded,
            ref LlvmAPInt poison, uint depth)
        {
            LlvmValue value = instruction is LlvmCallBase call ? call.GetArgument(operand) : instruction.GetOperand(operand)!;
            var replacement = SimplifyDemandedVectorElts(value, demanded, ref poison, depth + 1);
            if (replacement is null) return false;
            ReplaceOperand(instruction, operand, replacement);
            Count("demanded_elements.operand_rewrites");
            return true;
        }

        internal LlvmValue? SimplifyDemandedVectorElts(LlvmValue value, LlvmAPInt demanded,
            ref LlvmAPInt poison, uint depth = 0, bool allowMultipleUsers = false)
        {
            Count("demanded_elements.queries");
            if (value.Type is LlvmScalableVectorType) { Count("demanded_elements.scalable"); return null; }
            uint width = ((LlvmFixedVectorType)value.Type).NumElements;
            var all = LlvmAPInt.GetAllOnes(width);
            Debug.Assert((demanded & ~all).IsZero);
            if (value is LlvmPoisonValue) { poison = all; return null; }
            if (demanded.IsZero) { poison = all; Count("demanded_elements.undemanded"); return LlvmPoisonValue.Get(value.Type); }
            poison = LlvmAPInt.GetZero(width);
            if (value is LlvmConstant constant)
            {
                if (demanded.IsAllOnes) return null;
                var elements = new LlvmConstant[width];
                var poisonElement = LlvmPoisonValue.Get(((LlvmVectorType)value.Type).ElementType);
                for (uint index = 0; index < width; ++index)
                {
                    if (!DemandedElement(demanded, index))
                    { elements[index] = poisonElement; poison = SetDemandedElement(poison, index); continue; }
                    if (constant.GetAggregateElement(index) is not { } element) return null;
                    elements[index] = element;
                    if (element is LlvmPoisonValue) poison = SetDemandedElement(poison, index);
                }
                var replacement = LlvmConstantVector.Get(elements);
                if (replacement == constant) return null;
                Count("demanded_elements.constant_rewrites"); return replacement;
            }
            if (depth == CLOpts.SimplifyVectorEltsDepth) { Count("demanded_elements.depth_limit"); return null; }
            if (!allowMultipleUsers && !value.HasOneUse)
            {
                if (depth != 0) { Count("demanded_elements.multiple_nonroot_users"); return null; }
                demanded = all;
            }
            if (value is not LlvmInstruction instruction) return null;
            bool changed = false;
            switch (instruction)
            {
                case LlvmGetElementPtrInstruction gep:
                    if (DemandedGepMayIndexStruct(gep)) break;
                    for (int operand = 0; operand < gep.OperandCount; ++operand)
                    {
                        var input = gep.GetOperand(operand)!;
                        if (operand == 0 ? input is LlvmUndefValue : input is LlvmPoisonValue)
                        { poison = all; return null; }
                        if (!input.Type.IsVectorTy()) continue;
                        var inputPoison = LlvmAPInt.GetZero(width);
                        changed |= SimplifyDemandedVectorOperand(gep, operand, demanded, ref inputPoison, depth);
                        if (operand == 0) poison |= inputPoison;
                    }
                    break;
                case LlvmInsertElementInstruction insert:
                    if (depth == 0 && demanded.IsAllOnes && width > CLOpts.SimplifyVectorEltsDepth &&
                        CanSkipDemandedEltsInInsertChain(insert, width, CLOpts.SimplifyVectorEltsDepth)) return null;
                    if (insert.IndexOperand is not LlvmConstantInt index)
                    {
                        var ignored = LlvmAPInt.GetZero(width);
                        changed |= SimplifyDemandedVectorOperand(insert, 0, demanded, ref ignored, depth);
                        break;
                    }
                    uint lane = unchecked((uint)index.Value.GetZExtValue());
                    var beforeInsert = lane < width ? ClearDemandedElement(demanded, lane) : demanded;
                    if (beforeInsert.IsZero && insert.InsertedElementOperand is LlvmExtractElementInstruction extract &&
                        extract.IndexOperand is LlvmConstantInt extractedIndex && extractedIndex.Value.GetLimitedValue() == lane &&
                        extract.VectorOperand.Type == insert.Type) return extract.VectorOperand;
                    changed |= SimplifyDemandedVectorOperand(insert, 0, beforeInsert, ref poison, depth);
                    if (lane >= width || !DemandedElement(demanded, lane))
                    { Worklist.Push(insert); Count("demanded_elements.insert_removed"); return insert.VectorOperand; }
                    poison = ClearDemandedElement(poison, lane);
                    break;
                case LlvmShuffleVectorInstruction shuffle:
                    if (SimplifyDemandedVectorShuffle(shuffle, demanded, ref poison, depth, ref changed) is { } shuffled) return shuffled;
                    break;
                case LlvmSelectInstruction select:
                    SimplifyDemandedVectorSelect(select, demanded, ref poison, depth, ref changed);
                    break;
                case LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast:
                    SimplifyDemandedVectorBitCast(cast, demanded, ref poison, depth, ref changed);
                    break;
                case LlvmCastInstruction { Opcode: LlvmOpcode.FPTrunc or LlvmOpcode.FPExt } cast:
                    changed |= SimplifyDemandedVectorOperand(cast, 0, demanded, ref poison, depth);
                    break;
                case LlvmCallInstruction call:
                    SimplifyDemandedVectorCall(call, demanded, ref poison, depth, ref changed);
                    break;
            }
            if (instruction is LlvmBinaryOperator binary && binary.Opcode is not (LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem or LlvmOpcode.Shl or LlvmOpcode.AShr or LlvmOpcode.LShr))
            {
                if (demanded.IsOne && !binary.Left.HasOneUse && !binary.Right.HasOneUse && binary.HasOneUse &&
                    FindDemandedShuffleBinop(binary) is { } existing)
                { existing.AndIRFlags(binary); Worklist.Add(existing); Count("demanded_elements.existing_binop_reused"); return existing; }
                changed |= SimplifyDemandedVectorOperand(binary, 0, demanded, ref poison, depth);
                var otherPoison = LlvmAPInt.GetZero(width);
                changed |= SimplifyDemandedVectorOperand(binary, 1, demanded, ref otherPoison, depth);
                poison &= otherPoison;
            }
            if (poison.IsAllOnes) return LlvmPoisonValue.Get(instruction.Type);
            return changed ? instruction : null;
        }

        private static bool DemandedGepMayIndexStruct(LlvmGetElementPtrInstruction gep)
        {
            LlvmType indexed = gep.SourceElementType;
            for (int operand = 1; operand < gep.IndexCount; ++operand)
            {
                if (indexed is LlvmStructType) return true;
                indexed = indexed switch { LlvmArrayType array => array.ElementType, LlvmVectorType vector => vector.ElementType, _ => indexed };
            }
            return false;
        }

        private static bool CanSkipDemandedEltsInInsertChain(LlvmInsertElementInstruction insert, uint width, uint limit)
        {
            if (!insert.HasOneUse || insert.Users.First() is not LlvmInsertElementInstruction user || user.VectorOperand != insert) return false;
            var indices = LlvmAPInt.GetZero(width);
            var current = insert;
            for (uint depth = 0; depth != limit; ++depth)
            {
                if (current.IndexOperand is not LlvmConstantInt index || index.Value.GetLimitedValue() >= width) return false;
                uint lane = (uint)index.Value.GetZExtValue();
                if (DemandedElement(indices, lane)) return false;
                indices = SetDemandedElement(indices, lane);
                if (current.VectorOperand is LlvmPoisonValue) return true;
                if (current.VectorOperand is not LlvmInsertElementInstruction next || !next.HasOneUse) return false;
                current = next;
            }
            return true;
        }

        private LlvmBinaryOperator? FindDemandedShuffleBinop(LlvmBinaryOperator instruction) =>
            FindDemandedShuffleBinop(instruction, true) ?? FindDemandedShuffleBinop(instruction, false);

        private LlvmBinaryOperator? FindDemandedShuffleBinop(LlvmBinaryOperator instruction, bool shuffleLeft)
        {
            var other = shuffleLeft ? instruction.Right : instruction.Left;
            var shuffled = shuffleLeft ? instruction.Left : instruction.Right;
            if (!other.HasUseList) return null;
            foreach (var user in other.Users)
            {
                if (user is not LlvmBinaryOperator binary || binary.Opcode != instruction.Opcode) continue;
                bool Matches(LlvmValue maybeShuffle, LlvmValue maybeOther) => maybeOther == other &&
                    maybeShuffle is LlvmShuffleVectorInstruction shuffle && shuffle.Left == shuffled &&
                    shuffle.ShuffleMask.All(lane => lane is 0 or LlvmShuffleVectorInstruction.PoisonMaskElem) && shuffle.GetMaskValue(0) != LlvmShuffleVectorInstruction.PoisonMaskElem;
                bool matches = instruction.IsCommutative
                    ? Matches(binary.Left, binary.Right) || Matches(binary.Right, binary.Left)
                    : shuffleLeft ? Matches(binary.Left, binary.Right) : Matches(binary.Right, binary.Left);
                if (matches && DT.Dominates(binary, instruction)) return binary;
            }
            return null;
        }
    }
}
