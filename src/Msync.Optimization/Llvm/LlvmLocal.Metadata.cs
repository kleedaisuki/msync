// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Local.cpp combineMetadata,
// MD_invariant_load case and combineMetadataForCSE. Other semantic MD kinds are
// not represented by the current Core instruction model.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocal
{
    public static void CombineMetadataForCse(LlvmInstruction kept, LlvmInstruction removed, bool doesMove,
        Dictionary<string, ulong>? metrics = null)
    {
        if (kept is not LlvmLoadInstruction load) return;
        bool hadInvariant = load.HasInvariantLoadMetadata;
        if (doesMove) load.HasInvariantLoadMetadata &= removed is LlvmLoadInstruction { HasInvariantLoadMetadata: true };
        if (metrics is null) return;
        string key = hadInvariant && !load.HasInvariantLoadMetadata
            ? "metadata.invariant_load.dropped_on_move" : "metadata.invariant_load.preserved";
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }
}
