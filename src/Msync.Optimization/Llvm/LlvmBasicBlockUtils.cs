// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicBlock.cpp predecessor queries/
// removePredecessor; BasicBlockUtils.cpp emptyAndDetachBlock, detachDeadBlocks,
// DeleteDeadBlock(s), FoldSingleEntryPHINodes. See vendor/llvm-reference/llvm/LICENSE.TXT.
using System.Diagnostics;
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmBasicBlockUtils
{
    public static LlvmBasicBlock? GetSinglePredecessor(LlvmBasicBlock block)
    {
        using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
        if (!predecessors.MoveNext()) return null;
        var predecessor = predecessors.Current;
        return predecessors.MoveNext() ? null : predecessor;
    }
    public static LlvmBasicBlock? GetUniquePredecessor(LlvmBasicBlock block)
    {
        using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
        if (!predecessors.MoveNext()) return null;
        var predecessor = predecessors.Current;
        while (predecessors.MoveNext()) if (!ReferenceEquals(predecessors.Current, predecessor)) return null;
        return predecessor;
    }
    public static LlvmBasicBlock? GetUniqueSuccessor(LlvmBasicBlock block)
    {
        var terminator = block.GetTerminator();
        if (terminator.SuccessorCount == 0) return null;
        var successor = terminator.GetSuccessor(0);
        for (int i = 1; i < terminator.SuccessorCount; ++i)
            if (!ReferenceEquals(successor, terminator.GetSuccessor(i))) return null;
        return successor;
    }
    public static void RemovePredecessor(LlvmBasicBlock block, LlvmBasicBlock predecessor,
        bool keepOneInputPhis = false, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(block.HasNUsesOrMore(16) || LlvmDominatorTree.Predecessors(block).Contains(predecessor));
        if (block.FirstInstruction is not LlvmPhiInstruction first) return;
        int originalPreds = first.IncomingCount;
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi;)
        {
            instruction = instruction.Next;
            phi.RemoveIncomingValue(phi.GetBasicBlockIndex(predecessor), !keepOneInputPhis);
            Count(trace, metrics, "cfg.phi_predecessors_removed");
            if (keepOneInputPhis || originalPreds == 1) continue;
            if (phi.HasConstantValue() is not { } constant) continue;
            phi.ReplaceAllUsesWith(constant);
            phi.EraseFromParent();
            Count(trace, metrics, "cfg.constant_phis_removed");
        }
    }
    public static bool FoldSingleEntryPHINodes(LlvmBasicBlock block, CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null)
    {
        bool changed = block.FirstInstruction is LlvmPhiInstruction;
        while (block.FirstInstruction is LlvmPhiInstruction phi)
        {
            var incoming = phi.GetIncomingValue(0);
            phi.ReplaceAllUsesWith(ReferenceEquals(incoming, phi) ? LlvmPoisonValue.Get(phi.Type) : incoming);
            phi.EraseFromParent();
            Count(trace, metrics, "cfg.single_entry_phis_folded");
        }
        return changed;
    }
    public static void DeleteDeadBlock(LlvmBasicBlock block, LlvmDomTreeUpdater? updater = null,
        bool keepOneInputPhis = false, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null) =>
        DeleteDeadBlocks([block], updater, keepOneInputPhis, trace, metrics);

    public static void DeleteDeadBlocks(IReadOnlyList<LlvmBasicBlock> blocks, LlvmDomTreeUpdater? updater = null,
        bool keepOneInputPhis = false, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        AssertDeadPredecessors(blocks);
        List<LlvmCfgUpdate>? updates = updater is null ? null : [];
        DetachDeadBlocks(blocks, updates, keepOneInputPhis, trace, metrics);
        if (updater is not null) updater.ApplyUpdates(updates!);
        foreach (var block in blocks)
        {
            if (updater is not null) updater.DeleteBlock(block);
            else EraseBlock(block);
            Count(trace, metrics, "cfg.blocks_deleted");
        }
    }
    public static void DetachDeadBlocks(IReadOnlyList<LlvmBasicBlock> blocks, List<LlvmCfgUpdate>? updates = null,
        bool keepOneInputPhis = false, CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        foreach (var block in blocks) EmptyAndDetachBlock(block, updates, keepOneInputPhis, trace, metrics);
    }
    private static void EmptyAndDetachBlock(LlvmBasicBlock block, List<LlvmCfgUpdate>? updates,
        bool keepOneInputPhis, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        HashSet<LlvmBasicBlock>? uniqueSuccessors = updates is null ? null : [];
        foreach (var successor in LlvmDominatorTree.Successors(block))
        {
            RemovePredecessor(successor, block, keepOneInputPhis, trace, metrics);
            if (uniqueSuccessors?.Add(successor) == true) updates!.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
        }
        var source = block.LastInstruction?.Source;
        while (block.LastInstruction is { } instruction)
        {
            if (!instruction.UseEmpty) instruction.ReplaceAllUsesWith(LlvmPoisonValue.Get(instruction.Type));
            instruction.EraseFromParent();
            Count(trace, metrics, "cfg.dead_instructions_removed");
        }
        block.Append(new LlvmUnreachableInstruction(block.Context) { Source = source });
    }
    private static void EraseBlock(LlvmBasicBlock block)
    {
        block.EraseFromParent();
    }
    [Conditional("DEBUG")]
    private static void AssertDeadPredecessors(IReadOnlyList<LlvmBasicBlock> blocks)
    {
        var dead = blocks.ToHashSet();
        Debug.Assert(dead.Count == blocks.Count);
        foreach (var block in blocks)
            foreach (var predecessor in LlvmDominatorTree.Predecessors(block)) Debug.Assert(dead.Contains(predecessor));
    }
    private static void Count(CompilationTrace? trace, Dictionary<string, ulong>? metrics, string name, ulong amount = 1)
    {
        ulong total = amount;
        if (metrics is not null) metrics[name] = total += metrics.GetValueOrDefault(name);
        trace?.Metric(name, checked((long)total));
    }
}
