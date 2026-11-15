// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/DomConditionCache.h/.cpp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmDomConditionCache(Dictionary<string, ulong>? metrics = null)
{
    private readonly Dictionary<LlvmValue, List<LlvmCondBrInstruction>> affectedValues = new(ReferenceEqualityComparer.Instance);

    public void RegisterBranch(LlvmCondBrInstruction branch)
    {
        Count("branches.registered");
        LlvmValueTracking.FindValuesAffectedByCondition(branch.Condition, false, value =>
        {
            Count("affected.visited");
            if (!affectedValues.TryGetValue(value, out var branches)) affectedValues.Add(value, branches = []);
            if (branches.Contains(branch)) { Count("branches.duplicate"); return; }
            branches.Add(branch);
            Count("affected.registered");
        });
    }

    public void RemoveValue(LlvmValue value)
    {
        Count(affectedValues.Remove(value) ? "values.removed" : "values.remove_miss");
    }

    public IReadOnlyList<LlvmCondBrInstruction> ConditionsFor(LlvmValue value)
    {
        Count("queries");
        if (!affectedValues.TryGetValue(value, out var conditions)) { Count("queries.miss"); return []; }
        Count("queries.hit");
        return conditions;
    }

    private void Count(string name)
    {
        if (metrics is null) return;
        name = "dom_condition_cache." + name;
        metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}
