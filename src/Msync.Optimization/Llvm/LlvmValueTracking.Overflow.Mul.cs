// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeOverflowForUnsignedMul / computeOverflowForSignedMul.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmValueTracking
{
    public static LlvmOverflowResult ComputeOverflowForUnsignedMul(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, bool isNSW = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_tracking.overflow.unsigned_mul");
        var lhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(left, false, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions: conditions, condContext: condContext);
        var rhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(right, false, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions: conditions, condContext: condContext);
        if (isNSW && lhs.IsAllNonNegative && rhs.IsAllNonNegative)
        { Count(metrics, "value_tracking.overflow.unsigned_mul.nsw_nonnegative"); return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics); }
        return RecordOverflowResult(lhs.UnsignedMulMayOverflow(rhs), metrics);
    }
    public static LlvmOverflowResult ComputeOverflowForSignedMul(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_tracking.overflow.signed_mul");
        uint width = left.Type.GetScalarSizeInBits();
        uint signBits = LlvmKnownBitsAnalysis.ComputeNumSignBits(left, context: context, tree: tree, metrics: metrics,
            useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext) +
            LlvmKnownBitsAnalysis.ComputeNumSignBits(right, context: context, tree: tree, metrics: metrics,
            useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
        if (signBits > width + 1) return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics);
        if (signBits == width + 1)
        {
            var lhs = LlvmKnownBitsAnalysis.Compute(left, context: context, tree: tree, metrics: metrics,
                useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
            var rhs = LlvmKnownBitsAnalysis.Compute(right, context: context, tree: tree, metrics: metrics,
                useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
            if (lhs.IsNonNegative || rhs.IsNonNegative) return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics);
        }
        return RecordOverflowResult(LlvmOverflowResult.MayOverflow, metrics);
    }
}
