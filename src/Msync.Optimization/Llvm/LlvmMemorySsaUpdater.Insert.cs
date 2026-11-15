// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp insertUse / insertDef / fixupDefs.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    public void InsertUse(LlvmMemoryUse use, bool renameUses = false)
        => RunUpdate("insert-use", use, (use, renameUses), static (updater, state) => updater.InsertUseCore(state.use, state.renameUses));

    private void InsertUseCore(LlvmMemoryUse use, bool renameUses)
    {
        Count("insert.uses"); visitedBlocks.Clear(); insertedPhis.Clear();
        use.SetDefiningAccess(GetPreviousDef(use));
        if (!renameUses && insertedPhis.Count != 0)
            System.Diagnostics.Debug.Assert(MemorySsa.GetBlockDefs(use.Block) is not { Count: > 1 });
        if (!renameUses || insertedPhis.Count == 0) return;
        var visited = new HashSet<LlvmBasicBlock>();
        if (MemorySsa.GetBlockDefs(use.Block)?.First is { } first)
            MemorySsa.RenamePass(use.Block, first is LlvmMemoryDef definition ? definition.DefiningAccess : first, visited);
        foreach (var weak in insertedPhis)
            if (WeakPhi(weak) is { } phi) MemorySsa.RenamePass(phi.Block, null, visited);
    }

    private static void SetMemoryPhiValueForBlock(LlvmMemoryPhi phi, LlvmBasicBlock block, LlvmMemoryAccess definition)
    {
        int index = phi.GetBasicBlockIndex(block);
        System.Diagnostics.Debug.Assert(index >= 0);
        while (index < phi.IncomingCount && ReferenceEquals(phi.GetIncomingBlock(index), block)) phi.SetIncomingValue(index++, definition);
    }

    public void InsertDef(LlvmMemoryDef definition, bool renameUses = false)
        => RunUpdate("insert-def", definition, (definition, renameUses), static (updater, state) => updater.InsertDefCore(state.definition, state.renameUses));

    private void InsertDefCore(LlvmMemoryDef definition, bool renameUses)
    {
        Count("insert.defs");
        if (!MemorySsa.DominatorTree.IsReachableFromEntry(definition.Block))
        { definition.SetDefiningAccess(MemorySsa.LiveOnEntryDef); Count("insert.unreachable"); return; }
        visitedBlocks.Clear(); insertedPhis.Clear();
        var previous = GetPreviousDef(definition);
        bool sameBlock = ReferenceEquals(previous.Block, definition.Block)
            && !(previous is LlvmMemoryPhi phi && insertedPhis.Contains(phi));
        if (sameBlock)
            foreach (var use in previous.Uses.ToArray())
                if (use.User is not LlvmMemoryUse && !ReferenceEquals(use.User, definition)) use.Set(definition);
        definition.SetDefiningAccess(previous);
        var fixups = insertedPhis.Cast<LlvmMemoryAccess>().ToList();
        var existingPhis = new HashSet<LlvmMemoryPhi>();
        int newPhiIndex = insertedPhis.Count;
        if (!sameBlock)
        {
            var definingBlocks = new HashSet<LlvmBasicBlock> { definition.Block };
            foreach (var weak in insertedPhis)
                if (WeakPhi(weak) is { } live) definingBlocks.Add(live.Block);
            var idf = new LlvmForwardIdfCalculator(MemorySsa.DominatorTree, MemorySsa.Metrics) { DefiningBlocks = definingBlocks };
            var frontier = new List<LlvmBasicBlock>(); idf.Calculate(frontier);
            var newPhis = new List<LlvmMemoryPhi>();
            foreach (var block in frontier)
            {
                var current = MemorySsa.GetMemoryAccess(block);
                if (current is null) { current = MemorySsa.CreateMemoryPhi(block); newPhis.Add(current); Count("insert.idf_phis"); }
                else existingPhis.Add(current);
                nonOptPhis.Add(current);
            }
            foreach (var current in newPhis)
                foreach (var predecessor in Predecessors(current.Block))
                    current.AddIncoming(GetPreviousDefFromEnd(predecessor, []), predecessor);
            newPhiIndex = insertedPhis.Count;
            foreach (var current in newPhis) { insertedPhis.Add(current); fixups.Add(current); }
            fixups.Add(definition);
        }
        int newPhiEnd = insertedPhis.Count;
        FixupDefs(fixups);
        System.Diagnostics.Debug.Assert(newPhiEnd == insertedPhis.Count);
        if (newPhiEnd != newPhiIndex) TryRemoveTrivialPhis(insertedPhis.GetRange(newPhiIndex, newPhiEnd - newPhiIndex));
        if (!renameUses) return;
        var visited = new HashSet<LlvmBasicBlock>();
        var first = MemorySsa.GetBlockDefs(definition.Block)!.First!;
        MemorySsa.RenamePass(definition.Block, first is LlvmMemoryDef firstDef ? firstDef.DefiningAccess : first, visited);
        foreach (var weak in insertedPhis)
            if (WeakPhi(weak) is { } live) MemorySsa.RenamePass(live.Block, null, visited);
        foreach (var weak in existingPhis)
            if (WeakPhi(weak) is { } live) MemorySsa.RenamePass(live.Block, null, visited);
    }

    private void FixupDefs(IReadOnlyList<LlvmMemoryAccess> variables)
    {
        var seen = new HashSet<LlvmBasicBlock>();
        var worklist = new Stack<LlvmBasicBlock>();
        foreach (var variable in variables)
        {
            if (LlvmMemoryAccess.Weak(variable) is not { } definition) continue;
            Count("fixup.definitions");
            if (definition is LlvmMemoryPhi phi) nonOptPhis.Remove(phi);
            if (definition.NextDef is LlvmMemoryDef next) { next.SetDefiningAccess(definition); continue; }
            foreach (var successor in Successors(definition.Block))
            {
                if (MemorySsa.GetMemoryAccess(successor) is { } successorPhi) SetMemoryPhiValueForBlock(successorPhi, definition.Block, definition);
                else worklist.Push(successor);
            }
            while (worklist.TryPop(out var block))
            {
                Count("fixup.blocks");
                if (MemorySsa.GetBlockDefs(block)?.First is { } first)
                {
                    System.Diagnostics.Debug.Assert(first is not LlvmMemoryPhi && MemorySsa.Dominates(definition, first));
                    ((LlvmMemoryDef)first).SetDefiningAccess(definition); continue;
                }
                foreach (var successor in Successors(block))
                {
                    if (MemorySsa.GetMemoryAccess(successor) is { } successorPhi) SetMemoryPhiValueForBlock(successorPhi, block, definition);
                    else if (seen.Add(successor)) worklist.Push(successor);
                }
            }
        }
    }
}
