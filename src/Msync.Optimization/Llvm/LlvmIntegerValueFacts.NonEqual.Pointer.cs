// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isNonEqualPointersWithRecursiveGEP; Value.cpp stripAndAccumulateConstantOffsets.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool IsNonEqualRecursiveGep(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics)
    {
        if (!left.Type.IsPointerTy() || !right.Type.IsPointerTy() ||
            left is not LlvmGetElementPtrInstruction { IndexCount: 1 } gep || gep.GetIndex(0) is not LlvmConstant ||
            gep.PointerOperand is not LlvmPhiInstruction { IncomingCount: 2 } phi) return false;
        LlvmValue start;
        if (phi.GetIncomingValue(0) == left) start = phi.GetIncomingValue(1);
        else if (phi.GetIncomingValue(1) == left) start = phi.GetIncomingValue(0);
        else return false;
        uint width = layout.GetIndexTypeSizeInBits(start.Type);
        LlvmValue step = left;
        var startOffset = StripNonEqualInBoundsOffsets(ref start, width, layout, metrics);
        var stepOffset = StripNonEqualInBoundsOffsets(ref step, width, layout, metrics);
        if (step != phi) return false;
        var otherOffset = StripNonEqualInBoundsOffsets(ref right, width, layout, metrics);
        return start == right &&
            ((startOffset.Sge(otherOffset) && !stepOffset.IsNegative && !stepOffset.IsZero) ||
             (startOffset.Sle(otherOffset) && stepOffset.IsNegative));
    }

    private static LlvmAPInt StripNonEqualInBoundsOffsets(ref LlvmValue value, uint width, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics)
    {
        var offset = LlvmAPInt.GetZero(width);
        value = LlvmValueTracking.StripAndAccumulateConstantOffsets(value, layout, ref offset,
            allowNonInbounds: false, metrics: metrics);
        return offset;
    }
}

public static partial class LlvmInstructionSimplify
{
    // Both source callers use GEPOperator::accumulateConstantOffset without an
    // ExternalAnalysis callback; keep its represented layout policy in one place.
    internal static bool AccumulateSourceGepOffset(LlvmGetElementPtrInstruction gep, LlvmDataLayout layout,
        ref LlvmAPInt offset) => AccumulateIntegerGepOffset(gep, layout, ref offset);
}
