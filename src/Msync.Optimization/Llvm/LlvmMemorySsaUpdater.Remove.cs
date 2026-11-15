// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp removeMemoryAccess / removeBlocks / changeToUnreachable.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    private static LlvmMemoryAccess? OnlySingleValue(LlvmMemoryPhi phi)
    {
        LlvmMemoryAccess? value = null;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            var incoming = phi.GetIncomingValue(index);
            if (value is null) value = incoming;
            else if (!ReferenceEquals(value, incoming)) return null;
        }
        return value;
    }

    public void RemoveMemoryAccess(LlvmInstruction instruction, bool optimizePhis = false)
    {
        if (MemorySsa.GetMemoryAccess(instruction) is { } access) RemoveMemoryAccess(access, optimizePhis);
    }

    public void RemoveMemoryAccess(LlvmMemoryAccess access, bool optimizePhis = false)
        => RunUpdate("remove", access, (access, optimizePhis), static (updater, state) => updater.RemoveMemoryAccessCore(state.access, state.optimizePhis));

    private void RemoveMemoryAccessCore(LlvmMemoryAccess access, bool optimizePhis)
    {
        Debug.Assert(!MemorySsa.IsLiveOnEntryDef(access));
        Count("remove.accesses");
        LlvmMemoryAccess? replacement;
        if (access is LlvmMemoryPhi phi)
        { replacement = OnlySingleValue(phi); Debug.Assert(replacement is not null || phi.UseEmpty); }
        else replacement = ((LlvmMemoryUseOrDef)access).DefiningAccess;
        var phisToCheck = new HashSet<LlvmMemoryPhi>();
        var orderedPhis = new List<LlvmMemoryPhi>();
        if (access is not LlvmMemoryUse && !access.UseEmpty)
        {
            Debug.Assert(replacement is not null && !ReferenceEquals(replacement, access));
            access.ReplacedBy = replacement;
            while (!access.UseEmpty)
            {
                var use = access.Uses.First();
                if (use.User is LlvmMemoryUseOrDef useOrDef) useOrDef.ResetOptimized();
                if (optimizePhis && use.User is LlvmMemoryPhi userPhi && phisToCheck.Add(userPhi)) orderedPhis.Add(userPhi);
                use.Set(replacement); Count("remove.relinked_uses");
            }
        }
        MemorySsa.RemoveFromLookups(access); MemorySsa.RemoveFromLists(access);
        foreach (var weak in Enumerable.Reverse(orderedPhis))
            if (WeakPhi(weak) is { } live) TryRemoveTrivialPhi(live);
    }

    public void RemoveBlocks(IReadOnlySet<LlvmBasicBlock> deadBlocks)
    {
        foreach (var block in deadBlocks)
        {
            foreach (var successor in Successors(block))
                if (!deadBlocks.Contains(successor) && MemorySsa.GetMemoryAccess(successor) is { } phi)
                { phi.UnorderedDeleteIncomingBlock(block); TryRemoveTrivialPhi(phi); }
            if (MemorySsa.GetBlockAccesses(block) is { } accesses)
                foreach (var access in accesses) access.DropAllReferences();
        }
        foreach (var block in deadBlocks)
        {
            if (MemorySsa.GetBlockAccesses(block) is not { } accesses) continue;
            for (var access = accesses.First; access is not null;)
            {
                var next = access.Next;
                MemorySsa.RemoveFromLookups(access); MemorySsa.RemoveFromLists(access); access = next;
                Count("remove.dead_block_accesses");
            }
        }
    }

    public void ChangeToUnreachable(LlvmInstruction instruction)
    {
        var block = instruction.Parent!;
        for (var current = instruction; current is not null; current = current.Next) RemoveMemoryAccess(current);
        var updated = new List<LlvmMemoryPhi>();
        foreach (var successor in Successors(block))
        {
            RemoveDuplicatePhiEdgesBetween(block, successor);
            if (MemorySsa.GetMemoryAccess(successor) is not { } phi) continue;
            phi.UnorderedDeleteIncomingBlock(block); updated.Add(phi);
        }
        TryRemoveTrivialPhis(updated);
    }
}
