// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeNumSignBitsVectorConstant and ComputeNumSignBitsImpl ShuffleVector.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmKnownBitsAnalysis
{
    private static uint? ComputeVectorConstantSignBits(LlvmValue value, LlvmAPInt demand, uint width,
        Dictionary<string, ulong>? metrics)
    {
        if (value is not LlvmConstant constant || value.Type is not LlvmFixedVectorType vector) return null;
        uint minimum = width;
        for (uint lane = 0; lane < vector.NumElements; ++lane)
        {
            if (!IsDemanded(demand, lane)) continue;
            if (constant.GetAggregateElement(lane) is not LlvmConstantInt integer) return null;
            Count(metrics, "sign_bits.vector.constant_lane");
            minimum = Math.Min(minimum, SignBitsOf(integer.Value));
        }
        return minimum;
    }

    private static uint? ComputeShuffleSignBits(LlvmShuffleVectorInstruction shuffle, LlvmAPInt demand,
        uint depth, LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics,
        bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals,
        LlvmDomConditionCache? conditions, LlvmCondContext? condContext, LlvmDataLayout layout)
    {
        Count(metrics, "sign_bits.vector.shuffle");
        if (shuffle.Left.Type is not LlvmFixedVectorType input) return null;
        uint lanes = input.NumElements;
        LlvmAPInt left = LlvmAPInt.GetZero(lanes), right = LlvmAPInt.GetZero(lanes);
        for (uint lane = 0; lane < demand.BitWidth; ++lane)
        {
            if (!IsDemanded(demand, lane)) continue;
            int selected = shuffle.GetMaskValue((int)lane);
            if (selected < 0) return 1;
            if ((uint)selected < lanes) left |= LlvmAPInt.GetOneBitSet(lanes, (uint)selected);
            else right |= LlvmAPInt.GetOneBitSet(lanes, (uint)selected - lanes);
        }
        uint bits = uint.MaxValue;
        if (!left.IsZero) bits = ComputeNumSignBits(shuffle.Left, left, depth + 1, context, tree, metrics,
            useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        if (bits == 1) return null;
        if (!right.IsZero) bits = Math.Min(bits, ComputeNumSignBits(shuffle.Right, right, depth + 1, context, tree, metrics,
            useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
        return bits is 1 or uint.MaxValue ? null : bits;
    }
}
