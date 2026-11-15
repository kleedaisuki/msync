// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineInternal.h CommonPointerBase; InstCombineAddSub.cpp compute/isExpensive.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmCommonPointerBase
{
    private readonly List<LlvmGepOperator> leftGeps = [], rightGeps = [];
    private LlvmCommonPointerBase()
    {
        LHSGEPs = leftGeps.AsReadOnly();
        RHSGEPs = rightGeps.AsReadOnly();
    }

    public LlvmValue? Ptr { get; private set; }
    public IReadOnlyList<LlvmGepOperator> LHSGEPs { get; }
    public IReadOnlyList<LlvmGepOperator> RHSGEPs { get; }
    public LlvmGepNoWrapFlags LHSNW { get; private set; } = LlvmGepNoWrapFlags.All;
    public LlvmGepNoWrapFlags RHSNW { get; private set; } = LlvmGepNoWrapFlags.All;

    public static LlvmCommonPointerBase Compute(LlvmValue left, LlvmValue right,
        Dictionary<string, ulong>? metrics = null)
    {
        var result = new LlvmCommonPointerBase();
        if (!ReferenceEquals(left.Type, right.Type)) return result;
        HashSet<LlvmValue> pointers = [];
        LlvmValue pointer = left;
        while (true)
        {
            pointers.Add(pointer);
            Count(metrics, "left_ancestors");
            if (!LlvmGepOperator.TryGet(pointer, out var gep)) break;
            pointer = gep.PointerOperand;
        }

        bool first = true;
        while (true)
        {
            Count(metrics, "right_ancestors");
            if (pointers.Contains(right))
            {
                result.Ptr = right;
                break;
            }
            if (!LlvmGepOperator.TryGet(right, out var gep)) return result;
            result.rightGeps.Add(gep);
            result.RHSNW = first ? gep.NoWrapFlags : result.RHSNW.IntersectForOffsetAdd(gep.NoWrapFlags);
            first = false;
            right = gep.PointerOperand;
        }

        first = true;
        while (!ReferenceEquals(left, result.Ptr))
        {
            _ = LlvmGepOperator.TryGet(left, out var gep);
            result.leftGeps.Add(gep);
            result.LHSNW = first ? gep.NoWrapFlags : result.LHSNW.IntersectForOffsetAdd(gep.NoWrapFlags);
            first = false;
            left = gep.PointerOperand;
        }
        Count(metrics, "common_base");
        return result;
    }

    public bool IsExpensive()
    {
        uint count = CountMultiUse(leftGeps) + CountMultiUse(rightGeps);
        return count > 2;
    }

    private static uint CountMultiUse(IEnumerable<LlvmGepOperator> geps)
    {
        uint count = 0;
        bool seen = false;
        foreach (LlvmGepOperator gep in geps)
        {
            if (gep.HasOneUse) continue;
            if (seen) ++count;
            seen = true;
        }
        return count;
    }

    private static void Count(Dictionary<string, ulong>? metrics, string key)
    {
        if (metrics is null) return;
        key = "common_pointer_base." + key;
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }
}
