// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Value.cpp uses
// SmallPtrSet<const Value *, 4> for both pointer-stripping walks.
using System.Runtime.CompilerServices;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

internal struct LlvmPointerVisitedSet
{
    [InlineArray(4)]
    private struct InlineEntries
    {
        private LlvmValue? first;
    }

    private InlineEntries entries;
    private int count;
    private HashSet<LlvmValue>? overflow;

    internal LlvmPointerVisitedSet(LlvmValue first, Dictionary<string, ulong>? metrics) : this()
    {
        entries[0] = first;
        count = 1;
        if (metrics is not null) Count(metrics, "value_tracking.pointer_visited.inline_inserts");
    }

    internal bool Add(LlvmValue value, Dictionary<string, ulong>? metrics)
    {
        if (overflow is not null)
        {
            bool inserted = overflow.Add(value);
            if (metrics is not null)
            {
                Count(metrics, "value_tracking.pointer_visited.overflow_queries");
                Count(metrics, inserted ? "value_tracking.pointer_visited.overflow_inserts" : "value_tracking.pointer_visited.overflow_hits");
            }
            return inserted;
        }

        for (int index = 0; index < count; ++index)
        {
            if (!ReferenceEquals(entries[index], value)) continue;
            if (metrics is not null)
            {
                Count(metrics, "value_tracking.pointer_visited.inline_probes", (ulong)index + 1);
                Count(metrics, "value_tracking.pointer_visited.inline_hits");
            }
            return false;
        }

        if (metrics is not null) Count(metrics, "value_tracking.pointer_visited.inline_probes", (ulong)count);
        if (count < 4)
        {
            entries[count++] = value;
            if (metrics is not null) Count(metrics, "value_tracking.pointer_visited.inline_inserts");
            return true;
        }

        // Unreachable IR may contain cycles; long walks retain unbounded tracking.
        overflow = new HashSet<LlvmValue>(8, ReferenceEqualityComparer.Instance);
        for (int index = 0; index < count; ++index) overflow.Add(entries[index]!);
        overflow.Add(value);
        if (metrics is not null)
        {
            Count(metrics, "value_tracking.pointer_visited.overflow_promotions");
            Count(metrics, "value_tracking.pointer_visited.overflow_seed_entries", (ulong)count);
            Count(metrics, "value_tracking.pointer_visited.overflow_inserts");
        }
        return true;
    }

    private static void Count(Dictionary<string, ulong> metrics, string key, ulong amount = 1) =>
        metrics[key] = metrics.GetValueOrDefault(key) + amount;
}
