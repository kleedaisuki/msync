// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// stripAndComputeConstantOffsets/computePointerDifference; IR/Value.cpp
// stripAndAccumulateConstantOffsets; IR/Operator.cpp accumulateConstantOffset.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmConstantInt? ComputeIntegerPointerDifference(LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        var lo = StripIntegerPointerOffsets(ref lhs, q);
        var ro = StripIntegerPointerOffsets(ref rhs, q);
        return lhs == rhs ? LlvmConstantInt.Get(lhs.Type.Context, lo - ro) : null;
    }

    private static LlvmAPInt StripIntegerPointerOffsets(ref LlvmValue value, BinaryQuery q)
    {
        var offset = LlvmAPInt.GetZero(q.Layout.GetIndexTypeSizeInBits(value.Type));
        value = LlvmValueTracking.StripAndAccumulateConstantOffsets(value, q.Layout, ref offset, metrics: q.Metrics);
        return offset.SExtOrTrunc(q.Layout.GetIndexTypeSizeInBits(value.Type));
    }

    private static bool AccumulateIntegerGepOffset(LlvmGetElementPtrInstruction gep, LlvmDataLayout layout,
        ref LlvmAPInt offset) => LlvmValueTracking.AccumulateConstantOffset(gep, layout, ref offset);
}
