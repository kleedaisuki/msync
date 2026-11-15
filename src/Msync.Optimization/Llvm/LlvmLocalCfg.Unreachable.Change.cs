// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/Local.cpp changeToUnreachable.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocalCfg
{
    public static uint ChangeToUnreachable(LlvmInstruction first, bool preserveLcssa = false,
        LlvmDomTreeUpdater? updater = null, LlvmMemorySsaUpdater? memoryUpdater = null,
        Dictionary<string, ulong>? metrics = null)
    {
        LlvmBasicBlock block = first.Parent!;
        memoryUpdater?.ChangeToUnreachable(first);
        HashSet<LlvmBasicBlock>? unique = updater is null ? null : [];
        foreach (LlvmBasicBlock successor in block.GetTerminator().Successors)
        {
            LlvmBasicBlockUtils.RemovePredecessor(successor, block, preserveLcssa, metrics: metrics);
            unique?.Add(successor);
        }
        block.InsertBefore(new LlvmUnreachableInstruction(block.Context) { Source = first.Source }, first);
        uint removed = 0;
        for (LlvmInstruction? instruction = first; instruction is not null;)
        {
            LlvmInstruction current = instruction;
            instruction = instruction.Next;
            if (!current.UseEmpty) current.ReplaceAllUsesWith(LlvmPoisonValue.Get(current.Type));
            current.EraseFromParent();
            ++removed;
        }
        if (updater is not null)
            updater.ApplyUpdates(unique!.Select(successor => new LlvmCfgUpdate(LlvmCfgUpdateKind.Delete, block, successor)).ToArray());
        Count(metrics, "local_cfg.unreachable.instructions_removed", removed);
        if (unique is not null) Count(metrics, "local_cfg.unreachable.dominator_updates", (ulong)unique.Count);
        return removed;
    }
}
