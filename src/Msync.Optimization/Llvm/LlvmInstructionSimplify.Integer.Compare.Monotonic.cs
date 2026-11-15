// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// getUnsignedMonotonicValues and simplifyICmpUsingMonotonicValues.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyIntegerCompareMonotonic(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        if (p is not (LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUlt)) return null;
        HashSet<LlvmValue> greater = new(ReferenceEqualityComparer.Instance);
        HashSet<LlvmValue> lower = new(ReferenceEqualityComparer.Instance);
        GetIntegerMonotonicValues(greater, lhs, true, q);
        GetIntegerMonotonicValues(lower, rhs, false, q);
        foreach (var value in greater)
            if (lower.Contains(value)) return CompareBool(lhs, p == LlvmCmpPredicate.ICmpUge);
        return null;
    }

    private static void GetIntegerMonotonicValues(HashSet<LlvmValue> result, LlvmValue value, bool greater, BinaryQuery q, uint depth = 0)
    {
        if (!result.Add(value) || ++depth > 1 || value is not LlvmBinaryOperator b) return;
        Count(q.Metrics, "simplify.icmp.monotonic_steps");
        if (greater)
        {
            if (b.Opcode == LlvmOpcode.Or)
            {
                GetIntegerMonotonicValues(result, b.Left, greater, q, depth);
                GetIntegerMonotonicValues(result, b.Right, greater, q, depth);
            }
            if (q.UseInstructionInfo && b.Opcode == LlvmOpcode.Mul && b.HasNoUnsignedWrap)
            {
                if (LlvmKnownBitsAnalysis.IsKnownNonZero(b.Left, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext))
                    GetIntegerMonotonicValues(result, b.Right, greater, q, depth);
                if (LlvmKnownBitsAnalysis.IsKnownNonZero(b.Right, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext))
                    GetIntegerMonotonicValues(result, b.Left, greater, q, depth);
            }
            return;
        }
        if (b.Opcode == LlvmOpcode.And)
        {
            GetIntegerMonotonicValues(result, b.Left, greater, q, depth);
            GetIntegerMonotonicValues(result, b.Right, greater, q, depth);
        }
        if (b.Opcode is LlvmOpcode.URem or LlvmOpcode.UDiv or LlvmOpcode.LShr)
            GetIntegerMonotonicValues(result, b.Left, greater, q, depth);
    }
}
