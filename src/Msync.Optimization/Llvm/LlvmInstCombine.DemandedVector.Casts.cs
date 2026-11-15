// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedVectorElts Select/BitCast.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private void SimplifyDemandedVectorSelect(LlvmSelectInstruction select, LlvmAPInt demanded,
            ref LlvmAPInt poison, uint depth, ref bool changed)
        {
            uint width = demanded.BitWidth;
            if (select.Condition.Type.IsVectorTy()) changed |= SimplifyDemandedVectorOperand(select, 0, demanded, ref poison, depth);
            var leftDemand = demanded;
            var rightDemand = demanded;
            if (select.Condition is LlvmConstantVector condition)
                for (uint lane = 0; lane < width; ++lane)
                {
                    var value = condition.GetAggregateElement(lane)!;
                    if (value.IsNullValue) leftDemand = ClearDemandedElement(leftDemand, lane);
                    else if (value is LlvmConstantInt { IsOne: true }) rightDemand = ClearDemandedElement(rightDemand, lane);
                }
            var leftPoison = LlvmAPInt.GetZero(width);
            var rightPoison = LlvmAPInt.GetZero(width);
            changed |= SimplifyDemandedVectorOperand(select, 1, leftDemand, ref leftPoison, depth);
            changed |= SimplifyDemandedVectorOperand(select, 2, rightDemand, ref rightPoison, depth);
            poison = leftPoison & rightPoison;
        }

        private void SimplifyDemandedVectorBitCast(LlvmCastInstruction cast, LlvmAPInt demanded,
            ref LlvmAPInt poison, uint depth, ref bool changed)
        {
            if (cast.Operand.Type is not LlvmVectorType input) return;
            uint width = demanded.BitWidth, inputWidth = ((LlvmFixedVectorType)input).NumElements;
            var inputDemand = LlvmAPInt.GetZero(inputWidth);
            uint ratio;
            if (width == inputWidth) { ratio = 1; inputDemand = demanded; }
            else if (width % inputWidth == 0)
            {
                ratio = width / inputWidth;
                for (uint lane = 0; lane < width; ++lane)
                    if (DemandedElement(demanded, lane)) inputDemand = SetDemandedElement(inputDemand, lane / ratio);
            }
            else if (inputWidth % width == 0)
            {
                ratio = inputWidth / width;
                for (uint lane = 0; lane < inputWidth; ++lane)
                    if (DemandedElement(demanded, lane / ratio)) inputDemand = SetDemandedElement(inputDemand, lane);
            }
            else return;
            var inputPoison = LlvmAPInt.GetZero(inputWidth);
            changed |= SimplifyDemandedVectorOperand(cast, 0, inputDemand, ref inputPoison, depth);
            if (width == inputWidth) poison = inputPoison;
            else if (width % inputWidth == 0)
            {
                for (uint lane = 0; lane < width; ++lane)
                    if (DemandedElement(inputPoison, lane / ratio)) poison = SetDemandedElement(poison, lane);
            }
            else
                for (uint lane = 0; lane < width; ++lane)
                    if (inputPoison.LShr(lane * ratio).ZExtOrTrunc(ratio).PopCount() == ratio) poison = SetDemandedElement(poison, lane);
        }
    }
}
