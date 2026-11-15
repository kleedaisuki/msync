// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// CodeMetrics.cpp appendSpeculatableOperands, completeEphemeralValues,
// and function/loop collectEphemeralValues.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmCodeMetrics
{
    public static void CollectEphemeralValues(LlvmLoop loop, IEnumerable<LlvmInstruction?>? assumptions,
        ISet<LlvmValue> ephemeralValues, Dictionary<string, ulong>? metrics = null)
    {
        HashSet<LlvmValue> visited = [];
        List<LlvmValue> worklist = [];
        foreach (LlvmInstruction? instruction in assumptions ?? [])
        {
            if (instruction is null || !loop.Contains(instruction.Parent!)) continue;
            Count(metrics, "ephemeral.seeds");
            if (ephemeralValues.Add(instruction)) AppendOperands(instruction, visited, worklist, metrics);
        }
        CompleteEphemeralValues(visited, worklist, ephemeralValues, metrics);
    }

    public static void CollectEphemeralValues(LlvmFunction function, IEnumerable<LlvmInstruction?>? assumptions,
        ISet<LlvmValue> ephemeralValues, Dictionary<string, ulong>? metrics = null)
    {
        HashSet<LlvmValue> visited = [];
        List<LlvmValue> worklist = [];
        foreach (LlvmInstruction? instruction in assumptions ?? [])
        {
            if (instruction is null) continue;
            Debug.Assert(ReferenceEquals(instruction.Parent?.Parent, function));
            Count(metrics, "ephemeral.seeds");
            if (ephemeralValues.Add(instruction)) AppendOperands(instruction, visited, worklist, metrics);
        }
        CompleteEphemeralValues(visited, worklist, ephemeralValues, metrics);
    }

    private static void AppendOperands(LlvmValue value, HashSet<LlvmValue> visited,
        List<LlvmValue> worklist, Dictionary<string, ulong>? metrics)
    {
        if (value is not LlvmUser user) return;
        foreach (LlvmValue? operand in user.Operands)
        {
            Count(metrics, "ephemeral.operands");
            if (operand is not null && visited.Add(operand) && operand is LlvmInstruction instruction &&
                !instruction.MayHaveSideEffects() && !instruction.IsTerminator)
                worklist.Add(instruction);
        }
    }

    private static void CompleteEphemeralValues(HashSet<LlvmValue> visited, List<LlvmValue> worklist,
        ISet<LlvmValue> ephemeralValues, Dictionary<string, ulong>? metrics)
    {
        for (int index = 0; index < worklist.Count; ++index)
        {
            LlvmValue value = worklist[index];
            Debug.Assert(visited.Contains(value));
            Count(metrics, "ephemeral.candidates");
            if (!value.Users.All(ephemeralValues.Contains))
            {
                Count(metrics, "ephemeral.live_users");
                continue;
            }
            ephemeralValues.Add(value);
            Count(metrics, "ephemeral.values");
            AppendOperands(value, visited, worklist, metrics);
        }
    }
}
