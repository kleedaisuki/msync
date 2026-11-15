// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp removeEdge, duplicate edges, backedge and predecessor wiring.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    public void RemoveEdge(LlvmBasicBlock from, LlvmBasicBlock to)
    {
        if (MemorySsa.GetMemoryAccess(to) is not { } phi) return;
        int before = phi.IncomingCount;
        phi.UnorderedDeleteIncomingBlock(from); Count("cfg.removed_edges", (ulong)(before - phi.IncomingCount)); TryRemoveTrivialPhi(phi);
    }

    public void RemoveDuplicatePhiEdgesBetween(LlvmBasicBlock from, LlvmBasicBlock to)
    {
        if (MemorySsa.GetMemoryAccess(to) is not { } phi) return;
        int before = phi.IncomingCount;
        bool found = false;
        phi.UnorderedDeleteIncomingIf((_, block) =>
        {
            if (!ReferenceEquals(from, block)) return false;
            if (found) return true;
            found = true; return false;
        });
        Count("cfg.duplicate_edges_removed", (ulong)(before - phi.IncomingCount)); TryRemoveTrivialPhi(phi);
    }

    public void UpdatePhisWhenInsertingUniqueBackedgeBlock(LlvmBasicBlock header, LlvmBasicBlock preheader, LlvmBasicBlock backedge)
    {
        if (MemorySsa.GetMemoryAccess(header) is not { } phi) return;
        var newPhi = MemorySsa.CreateMemoryPhi(backedge);
        for (int index = 0; index < phi.IncomingCount; ++index)
            if (!ReferenceEquals(phi.GetIncomingBlock(index), preheader))
                newPhi.AddIncoming(phi.GetIncomingValue(index), phi.GetIncomingBlock(index));
        var preheaderAccess = phi.GetIncomingValueForBlock(preheader);
        phi.SetIncomingValue(0, preheaderAccess); phi.SetIncomingBlock(0, preheader);
        for (int index = phi.IncomingCount - 1; index >= 1; --index) phi.UnorderedDeleteIncoming(index);
        phi.AddIncoming(newPhi, backedge); TryRemoveTrivialPhi(newPhi); Count("cfg.unique_backedges");
    }

    public void WireOldPredecessorsToNewImmediatePredecessor(LlvmBasicBlock oldBlock, LlvmBasicBlock newBlock,
        IReadOnlyList<LlvmBasicBlock> predecessors, bool identicalEdgesWereMerged = true)
    {
        Debug.Assert(MemorySsa.GetBlockAccesses(newBlock) is null);
        if (MemorySsa.GetMemoryAccess(oldBlock) is not { } phi) return;
        if (Predecessors(oldBlock).Length == 1)
        { MemorySsa.MoveTo(phi, newBlock, LlvmMemorySsaInsertionPlace.Beginning); return; }
        Debug.Assert(predecessors.Count != 0);
        var newPhi = MemorySsa.CreateMemoryPhi(newBlock);
        var predecessorSet = predecessors.ToHashSet();
        Debug.Assert(identicalEdgesWereMerged || predecessorSet.Count == predecessors.Count);
        phi.UnorderedDeleteIncomingIf((access, block) =>
        {
            if (!predecessorSet.Contains(block)) return false;
            newPhi.AddIncoming(access, block);
            if (!identicalEdgesWereMerged) predecessorSet.Remove(block);
            return true;
        });
        phi.AddIncoming(newPhi, newBlock); TryRemoveTrivialPhi(newPhi); Count("cfg.wired_predecessors");
    }
}
