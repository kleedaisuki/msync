// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// EphemeralValueTracker. Callers visit instructions in reverse order.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    private sealed class EphemeralValueTracker(Dictionary<string, ulong>? metrics = null)
    {
        private readonly HashSet<LlvmInstruction> ephemeral = [];
        internal bool Track(LlvmInstruction instruction)
        {
            Count("simplifycfg.ephemeral.visited");
            if (instruction is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true })
            {
                ephemeral.Add(instruction);
                Count("simplifycfg.ephemeral.assume");
                return true;
            }
            if (instruction.MayHaveSideEffects() || instruction.IsTerminator) return false;
            foreach (LlvmUser user in instruction.Users)
            {
                Count("simplifycfg.ephemeral.users");
                if (!ephemeral.Contains((LlvmInstruction)user)) return false;
            }
            ephemeral.Add(instruction);
            Count("simplifycfg.ephemeral.tracked");
            return true;
        }
        internal bool Contains(LlvmInstruction instruction) => ephemeral.Contains(instruction);
        private void Count(string key)
        { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
    }
}
