// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// computeOverflowFor{Unsigned,Signed}{Add,Sub}, signed AddOperator overload.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static LlvmOverflowResult ComputeOverflowForUnsignedAdd(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_tracking.overflow.unsigned_add");
        var lhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(left, false, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        var rhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(right, false, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        return RecordOverflowResult(lhs.UnsignedAddMayOverflow(rhs), metrics);
    }

    public static LlvmOverflowResult ComputeOverflowForSignedAdd(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) => SignedAddOverflow(left, right, null, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);

    public static LlvmOverflowResult ComputeOverflowForSignedAdd(LlvmBinaryOperator add, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Debug.Assert(add.Opcode == LlvmOpcode.Add);
        return SignedAddOverflow(add.Left, add.Right, add, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
    }

    private static LlvmOverflowResult SignedAddOverflow(LlvmValue left, LlvmValue right, LlvmBinaryOperator? add,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_tracking.overflow.signed_add");
        // The source AddOperator overload directly reads NSW, not Q.IIQ.
        if (add is { HasNoSignedWrap: true })
        { Count(metrics, "value_tracking.overflow.signed_add.nsw"); return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics); }
        if (LlvmKnownBitsAnalysis.ComputeNumSignBits(left, context: context, tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext) > 1 &&
            LlvmKnownBitsAnalysis.ComputeNumSignBits(right, context: context, tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext) > 1)
        { Count(metrics, "value_tracking.overflow.signed_add.sign_bits"); return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics); }
        var lhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(left, true, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        var rhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(right, true, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        var result = lhs.SignedAddMayOverflow(rhs);
        if (result != LlvmOverflowResult.MayOverflow) return RecordOverflowResult(result, metrics);
        if (add is null) return RecordOverflowResult(result, metrics);
        if (lhs.IsAllNonNegative || rhs.IsAllNonNegative || lhs.IsAllNegative || rhs.IsAllNegative)
        {
            var addKnown = LlvmKnownBitsAnalysis.ComputeFromContext(add, new(lhs.BitWidth), context: context,
                tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
            if ((addKnown.IsNonNegative && (lhs.IsAllNonNegative || rhs.IsAllNonNegative)) ||
                (addKnown.IsNegative && (lhs.IsAllNegative || rhs.IsAllNegative)))
                return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics);
        }
        return RecordOverflowResult(result, metrics);
    }

    public static LlvmOverflowResult ComputeOverflowForUnsignedSub(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_tracking.overflow.unsigned_sub");
        if (right is LlvmBinaryOperator binary && ReferenceEquals(binary.Left, left) &&
            (binary.Opcode == LlvmOpcode.URem || binary.Opcode == LlvmOpcode.Sub && binary.HasNoUnsignedWrap) &&
            IsGuaranteedNotToBeUndef(left, tree, context, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions))
        { Count(metrics, "value_tracking.overflow.unsigned_sub.remainder_or_nuw_sub"); return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics); }
        bool? implied = LlvmInstructionSimplify.IsImpliedByDomCondition(LlvmCmpPredicate.ICmpUge, left, right,
            new(layout, context, metrics, tree, UseInstructionInfo: useInstructionInfo));
        if (implied.HasValue)
        {
            Count(metrics, "value_tracking.overflow.unsigned_sub.dom_condition");
            return RecordOverflowResult(implied.Value ? LlvmOverflowResult.NeverOverflows : LlvmOverflowResult.AlwaysOverflowsLow, metrics);
        }
        var lhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(left, false, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        var rhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(right, false, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        return RecordOverflowResult(lhs.UnsignedSubMayOverflow(rhs), metrics);
    }

    public static LlvmOverflowResult ComputeOverflowForSignedSub(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_tracking.overflow.signed_sub");
        if (right is LlvmBinaryOperator binary && ReferenceEquals(binary.Left, left) &&
            (binary.Opcode == LlvmOpcode.SRem || binary.Opcode == LlvmOpcode.Sub && binary.HasNoSignedWrap) &&
            IsGuaranteedNotToBeUndef(left, tree, context, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions))
        { Count(metrics, "value_tracking.overflow.signed_sub.remainder_or_nsw_sub"); return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics); }
        if (LlvmKnownBitsAnalysis.ComputeNumSignBits(left, context: context, tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext) > 1 &&
            LlvmKnownBitsAnalysis.ComputeNumSignBits(right, context: context, tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext) > 1)
        { Count(metrics, "value_tracking.overflow.signed_sub.sign_bits"); return RecordOverflowResult(LlvmOverflowResult.NeverOverflows, metrics); }
        var lhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(left, true, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        var rhs = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(right, true, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        return RecordOverflowResult(lhs.SignedSubMayOverflow(rhs), metrics);
    }

    private static LlvmOverflowResult RecordOverflowResult(LlvmOverflowResult result, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, result switch
        {
            LlvmOverflowResult.AlwaysOverflowsLow => "value_tracking.overflow.always_low",
            LlvmOverflowResult.AlwaysOverflowsHigh => "value_tracking.overflow.always_high",
            LlvmOverflowResult.NeverOverflows => "value_tracking.overflow.never",
            _ => "value_tracking.overflow.may"
        });
        return result;
    }
}
