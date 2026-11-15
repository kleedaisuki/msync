// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp vector Call cases; VectorUtils.cpp scalar operands.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private void SimplifyDemandedVectorCall(LlvmCallInstruction instruction, LlvmAPInt demanded,
            ref LlvmAPInt poison, uint depth, ref bool changed)
        {
            if (instruction.CalledFunction?.IntrinsicDescriptor is not { } descriptor) return;
            uint width = demanded.BitWidth;
            var kind = descriptor.Kind;
            if (kind == LlvmIntrinsicKind.MaskedLoad || kind == LlvmIntrinsicKind.MaskedGather)
            {
                var pointers = LlvmAPInt.GetAllOnes(width);
                var passthrough = demanded;
                if (instruction.GetArgument(1) is LlvmConstant mask)
                    for (uint lane = 0; lane < width; ++lane)
                    {
                        var element = mask.GetAggregateElement(lane);
                        if (element?.IsNullValue == true) pointers = ClearDemandedElement(pointers, lane);
                        else if (element is LlvmConstantInt { Value.IsAllOnes: true }) passthrough = ClearDemandedElement(passthrough, lane);
                    }
                var pointerPoison = LlvmAPInt.GetZero(width);
                var passthroughPoison = LlvmAPInt.GetZero(width);
                if (kind == LlvmIntrinsicKind.MaskedGather)
                    changed |= SimplifyDemandedVectorOperand(instruction, 0, pointers, ref pointerPoison, depth);
                changed |= SimplifyDemandedVectorOperand(instruction, 2, passthrough, ref passthroughPoison, depth);
                poison = pointerPoison & passthroughPoison;
                return;
            }
            if (!LlvmIntrinsic.IsTriviallyVectorizable(kind)) return;
            var accumulated = LlvmAPInt.GetZero(width);
            for (int operand = 0; operand < instruction.ArgumentCount; ++operand)
            {
                if (IsDemandedElementwiseScalarArgument(kind, operand)) continue;
                var operandPoison = LlvmAPInt.GetZero(width);
                changed |= SimplifyDemandedVectorOperand(instruction, operand, demanded, ref operandPoison, depth);
                accumulated |= operandPoison;
            }
            if (descriptor.PropagatesPoison) poison = accumulated;
        }

        // The caller has already selected the isTriviallyVectorizable source domain.
        private static bool IsDemandedElementwiseScalarArgument(LlvmIntrinsicKind kind, int operand) => kind.Name switch
        {
            "llvm.abs" or "llvm.ctlz" or "llvm.cttz" or "llvm.is.fpclass" or "llvm.powi" => operand == 1,
            "llvm.smul.fix" or "llvm.smul.fix.sat" or "llvm.umul.fix" or "llvm.umul.fix.sat" => operand == 2,
            _ => false
        };
    }
}
