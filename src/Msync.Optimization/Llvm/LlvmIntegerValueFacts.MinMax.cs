// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isKnownNonZeroFromOperator: umin/umax/smin/smax intrinsic cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool IsMinMaxIntrinsicKind(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.SMin ||
        kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMin || kind == LlvmIntrinsicKind.UMax;

    private static bool IsKnownNonZeroMinMax(LlvmCallInstruction call, LlvmIntrinsicKind kind, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_facts.nonzero.minmax_intrinsic");
        var left = call.GetArgument(0); var right = call.GetArgument(1);
        bool NonZero(LlvmValue value) => IsKnownNonZero(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        if (kind == LlvmIntrinsicKind.UMax)
            return MatchOpWithOpEqZero(left, right) || NonZero(right) || NonZero(left);
        if (kind == LlvmIntrinsicKind.SMax)
        {
            bool? leftNonZero = null, rightNonZero = null;
            var rhs = Compute(right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            if (rhs.IsNonNegative && IsNonZero(right, ref rightNonZero, rhs)) return true;
            var lhs = Compute(left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            if (lhs.IsNonNegative && IsNonZero(left, ref leftNonZero, lhs)) return true;
            return IsNonZero(right, ref rightNonZero, rhs) && IsNonZero(left, ref leftNonZero, lhs);
            bool IsNonZero(LlvmValue value, ref bool? cached, LlvmKnownBits known)
            {
                cached ??= known.IsNonZero || NonZero(value);
                return cached.Value;
            }
        }
        if (kind == LlvmIntrinsicKind.SMin)
        {
            var rhs = Compute(right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            if (rhs.IsNegative) return true;
            var lhs = Compute(left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            if (lhs.IsNegative || rhs.IsNonZero && lhs.IsNonZero) return true;
        }
        return NonZero(left) && NonZero(right);
    }
}
