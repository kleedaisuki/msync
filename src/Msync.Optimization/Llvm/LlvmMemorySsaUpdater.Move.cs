// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp moveTo*, moveAllAccesses, createMemoryAccess*.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    private void PrepareMove(LlvmMemoryUseOrDef access)
    {
        foreach (var user in access.Users)
            if (user is LlvmMemoryPhi phi) nonOptPhis.Add(phi);
        access.ReplaceAllUsesWith(access.DefiningAccess!);
    }
    private void FinishMove(LlvmMemoryUseOrDef access)
    {
        if (access is LlvmMemoryDef definition) InsertDef(definition, true);
        else InsertUse((LlvmMemoryUse)access, true);
        nonOptPhis.Clear(); Count("move.accesses");
    }
    private void MoveTo(LlvmMemoryUseOrDef access, LlvmBasicBlock block, LlvmMemoryAccess? where)
        => RunUpdate("move-before", access, (access, block, where), static (updater, state) => updater.MoveToCore(state.access, state.block, state.where));
    private void MoveToCore(LlvmMemoryUseOrDef access, LlvmBasicBlock block, LlvmMemoryAccess? where)
    { PrepareMove(access); MemorySsa.MoveTo(access, block, where); FinishMove(access); }
    private void MoveTo(LlvmMemoryUseOrDef access, LlvmBasicBlock block, LlvmMemorySsaInsertionPlace point)
        => RunUpdate("move-to-place", access, (access, block, point), static (updater, state) => updater.MoveToCore(state.access, state.block, state.point));
    private void MoveToCore(LlvmMemoryUseOrDef access, LlvmBasicBlock block, LlvmMemorySsaInsertionPlace point)
    { PrepareMove(access); MemorySsa.MoveTo(access, block, point); FinishMove(access); }

    public void MoveBefore(LlvmMemoryUseOrDef access, LlvmMemoryUseOrDef where) => MoveTo(access, where.Block, where);
    public void MoveAfter(LlvmMemoryUseOrDef access, LlvmMemoryUseOrDef where) => MoveTo(access, where.Block, where.Next);
    public void MoveToPlace(LlvmMemoryUseOrDef access, LlvmBasicBlock block, LlvmMemorySsaInsertionPlace point)
    {
        if (point != LlvmMemorySsaInsertionPlace.BeforeTerminator) { MoveTo(access, block, point); return; }
        if (MemorySsa.GetMemoryAccess(block.GetTerminator()) is { } where) MoveBefore(access, where);
        else MoveTo(access, block, LlvmMemorySsaInsertionPlace.End);
    }

    private void MoveAllAccesses(LlvmBasicBlock from, LlvmBasicBlock to, LlvmInstruction start)
    {
        if (MemorySsa.GetBlockAccesses(from) is null) return;
        Debug.Assert(ReferenceEquals(start.Parent, to));
        LlvmMemoryUseOrDef? first = null;
        for (var instruction = start; instruction is not null; instruction = instruction.Next)
            if ((first = MemorySsa.GetMemoryAccess(instruction)) is not null) break;
        for (var access = first; access is not null;)
        {
            var next = (LlvmMemoryUseOrDef?)access.Next;
            MemorySsa.MoveTo(access, to, LlvmMemorySsaInsertionPlace.End); access = next;
            Count("move.spliced_accesses");
        }
        if (MemorySsa.GetBlockDefs(from)?.First is LlvmMemoryPhi phi) TryRemoveTrivialPhi(phi);
    }

    public void MoveAllAfterSpliceBlocks(LlvmBasicBlock from, LlvmBasicBlock to, LlvmInstruction start)
    {
        Debug.Assert(MemorySsa.GetBlockAccesses(to) is null);
        MoveAllAccesses(from, to, start);
        foreach (var successor in Successors(to))
            if (MemorySsa.GetMemoryAccess(successor) is { } phi) phi.SetIncomingBlock(phi.GetBasicBlockIndex(from), to);
    }
    public void MoveAllAfterMergeBlocks(LlvmBasicBlock from, LlvmBasicBlock to, LlvmInstruction start)
    {
        Debug.Assert(Predecessors(from).All(predecessor => ReferenceEquals(predecessor, to)));
        MoveAllAccesses(from, to, start);
        foreach (var successor in Successors(from))
            if (MemorySsa.GetMemoryAccess(successor) is { } phi) phi.SetIncomingBlock(phi.GetBasicBlockIndex(from), to);
    }

    public LlvmMemoryAccess? CreateMemoryAccessInBB(LlvmInstruction instruction, LlvmMemoryAccess? definition,
        LlvmBasicBlock block, LlvmMemorySsaInsertionPlace point, bool creationMustSucceed = true)
    {
        var access = MemorySsa.CreateDefinedAccess(instruction, definition, creationMustSucceed: creationMustSucceed);
        if (access is not null) MemorySsa.InsertIntoListsForBlock(access, block, point);
        Count(access is null ? "create.no_memory_access" : "create.accesses");
        Count("create.in_block"); return access;
    }
    public LlvmMemoryUseOrDef CreateMemoryAccessBefore(LlvmInstruction instruction, LlvmMemoryAccess? definition, LlvmMemoryUseOrDef where)
    {
        Debug.Assert(ReferenceEquals(instruction.Parent, where.Block));
        var access = MemorySsa.CreateDefinedAccess(instruction, definition)!;
        MemorySsa.InsertIntoListsBefore(access, where.Block, where); Count("create.before"); return access;
    }
    public LlvmMemoryUseOrDef CreateMemoryAccessAfter(LlvmInstruction instruction, LlvmMemoryAccess? definition, LlvmMemoryAccess where)
    {
        Debug.Assert(ReferenceEquals(instruction.Parent, where.Block));
        var access = MemorySsa.CreateDefinedAccess(instruction, definition)!;
        MemorySsa.InsertIntoListsBefore(access, where.Block, where.Next); Count("create.after"); return access;
    }
}
