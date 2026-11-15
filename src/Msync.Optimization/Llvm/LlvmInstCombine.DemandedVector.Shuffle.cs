// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedVectorElts ShuffleVector.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyDemandedVectorShuffle(LlvmShuffleVectorInstruction shuffle, LlvmAPInt demanded,
            ref LlvmAPInt poison, uint depth, ref bool changed)
        {
            uint width = ((LlvmFixedVectorType)shuffle.Type).NumElements;
            uint inputWidth = ((LlvmFixedVectorType)shuffle.Left.Type).NumElements;
            if (shuffle.ShuffleMask.All(lane => lane == 0) && demanded.IsAllOnes)
            {
                if (shuffle.Right is not LlvmPoisonValue)
                { shuffle.Right = LlvmPoisonValue.Get(shuffle.Right.Type); changed = true; }
                var leftPoison = LlvmAPInt.GetZero(inputWidth);
                changed |= SimplifyDemandedVectorOperand(shuffle, 0, new LlvmAPInt(inputWidth, 1), ref leftPoison, depth);
                poison = DemandedElement(leftPoison, 0) ? LlvmAPInt.GetAllOnes(width) : LlvmAPInt.GetZero(width);
                return null;
            }
            var leftDemand = LlvmAPInt.GetZero(inputWidth);
            var rightDemand = LlvmAPInt.GetZero(inputWidth);
            for (uint lane = 0; lane < width; ++lane)
            {
                if (!DemandedElement(demanded, lane)) continue;
                int source = shuffle.GetMaskValue((int)lane);
                if (source == LlvmShuffleVectorInstruction.PoisonMaskElem) continue;
                if (source < inputWidth) leftDemand = SetDemandedElement(leftDemand, (uint)source);
                else rightDemand = SetDemandedElement(rightDemand, (uint)source - inputWidth);
            }
            var leftPoisonMask = LlvmAPInt.GetZero(inputWidth);
            var rightPoisonMask = LlvmAPInt.GetZero(inputWidth);
            changed |= SimplifyDemandedVectorOperand(shuffle, 0, leftDemand, ref leftPoisonMask, depth);
            changed |= SimplifyDemandedVectorOperand(shuffle, 1, rightDemand, ref rightPoisonMask, depth);
            if (width == inputWidth)
            {
                bool identity = true;
                for (uint lane = 0; lane < width; ++lane)
                    if (DemandedElement(demanded, lane) && shuffle.GetMaskValue((int)lane) != lane) { identity = false; break; }
                if (identity) return shuffle.Left;
            }
            bool newPoison = false, leftUniform = true, rightUniform = true;
            int leftLane = -1, leftValue = -1, rightLane = -1, rightValue = -1;
            for (uint lane = 0; lane < width; ++lane)
            {
                int source = shuffle.GetMaskValue((int)lane);
                if (source == LlvmShuffleVectorInstruction.PoisonMaskElem) poison = SetDemandedElement(poison, lane);
                else if (!DemandedElement(demanded, lane))
                { newPoison = true; poison = SetDemandedElement(poison, lane); }
                else if (source < inputWidth)
                {
                    if (DemandedElement(leftPoisonMask, (uint)source))
                    { newPoison = true; poison = SetDemandedElement(poison, lane); }
                    else
                    {
                        leftLane = leftLane == -1 ? (int)lane : (int)inputWidth;
                        leftValue = leftValue == -1 ? source : (int)inputWidth;
                        leftUniform &= source == lane;
                    }
                }
                else
                {
                    if (DemandedElement(rightPoisonMask, (uint)source - inputWidth))
                    { newPoison = true; poison = SetDemandedElement(poison, lane); }
                    else
                    {
                        rightLane = rightLane == -1 ? (int)lane : (int)inputWidth;
                        rightValue = rightValue == -1 ? source - (int)inputWidth : (int)inputWidth;
                        rightUniform &= source - inputWidth == lane;
                    }
                }
            }
            if (inputWidth == width)
            {
                LlvmValue? vector = null;
                LlvmConstant? element = null;
                int lane = -1;
                if ((uint)leftLane < inputWidth && rightUniform && shuffle.Left is LlvmConstantVector leftConstant)
                { vector = shuffle.Right; element = leftConstant.GetOperand(leftValue) as LlvmConstant; lane = leftLane; }
                if ((uint)rightLane < inputWidth && leftUniform && shuffle.Right is LlvmConstantVector rightConstant)
                { vector = shuffle.Left; element = rightConstant.GetOperand(rightValue) as LlvmConstant; lane = rightLane; }
                if (vector is not null && element is not null)
                {
                    var inserted = new LlvmInsertElementInstruction(vector, element, LlvmConstantInt.Get(LlvmType.GetInt64Ty(shuffle.Context), (ulong)lane));
                    inserted.SetName(shuffle.Name);
                    Count("demanded_elements.shuffle_to_insert");
                    return InsertNewInstWith(inserted, shuffle);
                }
            }
            if (newPoison)
            {
                int[] mask = new int[width];
                for (uint lane = 0; lane < width; ++lane)
                    mask[lane] = DemandedElement(poison, lane) ? LlvmShuffleVectorInstruction.PoisonMaskElem : shuffle.GetMaskValue((int)lane);
                shuffle.SetShuffleMask(mask);
                changed = true;
                Count("demanded_elements.shuffle_lanes_poisoned");
            }
            return null;
        }
    }
}
