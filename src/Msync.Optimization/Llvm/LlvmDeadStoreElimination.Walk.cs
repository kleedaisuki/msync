// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp getDomMemoryDef.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private LlvmMemoryAccess? GetDominatingDefinition(LlvmMemoryDef killing, LlvmMemoryAccess start,
            LlvmMemoryLocation killingLocation, LlvmValue underlying, ref uint scanLimit, ref uint walkLimit, ref uint partialLimit)
        {
            if (scanLimit == 0 || walkLimit == 0) { Count("walk.initial_limit"); return null; }
            var current = start;
            var killingInstruction = killing.MemoryInstruction!;
            bool optimize = options.OptimizeMemorySsa && ReferenceEquals(killing.DefiningAccess, start) && !killingInstruction.MayReadFromMemory();
            LlvmMemoryLocation? currentLocation;
            while (true)
            {
                Count("walk.definitions.visited");
                if (memory.IsLiveOnEntryDef(current))
                {
                    if (optimize && !ReferenceEquals(current, killing.DefiningAccess)) killing.SetOptimized(current);
                    return null;
                }
                uint cost = ReferenceEquals(killing.Block, current.Block) ? options.SameBlockCost : options.OtherBlockCost;
                if (walkLimit <= cost) { Count("walk.step_limit"); return null; }
                walkLimit -= cost;
                if (current is LlvmMemoryPhi) return current;
                var currentDef = (LlvmMemoryDef)current;
                var instruction = currentDef.MemoryInstruction!;
                var next = currentDef.DefiningAccess!;
                if (CanSkipDef(currentDef, !InvisibleOnUnwind(underlying))) { optimize = false; current = next; continue; }
                if (MayThrowBetween(killingInstruction, instruction, underlying)) { Count("walk.extra_throw"); return null; }
                if (IsBarrier(underlying, instruction)) { Count("walk.barrier"); return null; }
                if (instruction is not LlvmCallBase { CalledFunction.IntrinsicDescriptor: not null }
                    && IsReadClobber(killingLocation, instruction)) { Count("walk.read_clobber"); return null; }
                if (current.Uses.Any(use => use.User is LlvmMemoryUseOrDef access
                    && !memory.Dominates(start, access) && IsReadClobber(killingLocation, access.MemoryInstruction!)))
                { Count("walk.direct_use_read_clobber"); return null; }
                currentLocation = GetWriteLocation(instruction);
                if (currentLocation is null || !IsRemovable(instruction)) { optimize = false; current = next; continue; }
                if (!IsLoopIndependent(instruction, killingInstruction, currentLocation.Value))
                { optimize = false; current = next; Count("walk.cross_cycle"); continue; }
                var overwrite = IsOverwrite(killingInstruction, instruction, killingLocation, currentLocation.Value, out _, out _);
                if (optimize)
                {
                    if (!ReferenceEquals(currentDef, killing.DefiningAccess) && overwrite is Overwrite.Complete or Overwrite.MaybePartial)
                        killing.SetOptimized(currentDef);
                    if (overwrite != Overwrite.None) optimize = false;
                }
                if (overwrite is Overwrite.Unknown or Overwrite.None) { current = next; continue; }
                if (overwrite == Overwrite.MaybePartial)
                {
                    if (partialLimit <= 1) { --walkLimit; current = next; Count("walk.partial_limit"); continue; }
                    --partialLimit;
                }
                break;
            }
            var killingDefinitions = new HashSet<LlvmInstruction> { killingInstruction };
            var maybeDead = current;
            var maybeLocation = currentLocation!.Value;
            var maybeInstruction = ((LlvmMemoryDef)maybeDead).MemoryInstruction!;
            var work = new List<LlvmMemoryAccess>();
            var visited = new HashSet<LlvmMemoryAccess>();
            PushMemoryUses(maybeDead, work, visited);
            for (int index = 0; index < work.Count; ++index)
            {
                if (scanLimit < work.Count - index) { Count("walk.scan_limit"); return null; }
                --scanLimit;
                Count("walk.uses.visited");
                var use = work[index];
                if (use is LlvmMemoryPhi)
                {
                    if (killingDefinitions.Any(definition => dominators.ProperlyDominates(definition.Parent, use.Block))) continue;
                    PushMemoryUses(use, work, visited);
                    continue;
                }
                var instruction = ((LlvmMemoryUseOrDef)use).MemoryInstruction!;
                if (killingDefinitions.Any(definition => dominators.Dominates(definition, instruction))) continue;
                // No lifetime/free or noop-intrinsic IDs exist in this representation.
                if (instruction.MayThrow() && !InvisibleOnUnwind(underlying)) { Count("walk.use_may_throw"); return null; }
                if (IsReadClobber(maybeLocation, instruction)) { Count("walk.use_read_clobber"); return null; }
                if (ReferenceEquals(maybeDead, use) && !IsLoopInvariant(maybeLocation.Ptr!)) return null;
                if (ReferenceEquals(killing, use) || ReferenceEquals(maybeDead, use)) continue;
                if (use is not LlvmMemoryDef definitionUse) continue;
                if (CompleteOverwrite(maybeLocation, maybeInstruction, instruction))
                {
                    if (postOrder[instruction.Parent!] < postOrder[maybeDead.Block])
                    {
                        if (!InvisibleAfterReturn(underlying, killingLocation)) killingDefinitions.Add(instruction);
                    }
                    else { Count("walk.preceding_kill"); return null; }
                }
                else PushMemoryUses(definitionUse, work, visited);
            }
            if (!InvisibleAfterReturn(underlying, killingLocation) && !AllExitPathsKilled(maybeDead, killingDefinitions)) return null;
            Count("walk.candidate_found");
            return maybeDead;
        }
        private bool AllExitPathsKilled(LlvmMemoryAccess dead, HashSet<LlvmInstruction> definitions)
        {
            var blocks = definitions.Select(instruction => instruction.Parent!).ToHashSet();
            Debug.Assert(blocks.Count != 0);
            LlvmBasicBlock? common = blocks.First();
            foreach (var block in blocks.Skip(1))
            {
                if (common is null) break;
                common = postDominators.FindNearestCommonDominator(common, block, Metrics);
            }
            if (!postDominators.Dominates(common, dead.Block))
            {
                if (!anyUnreachableExit) return false;
                common = null;
            }
            if (common is not null && blocks.Contains(common)) return true;
            var work = new List<LlvmBasicBlock>();
            var seen = new HashSet<LlvmBasicBlock>();
            void Add(LlvmBasicBlock block) { if (seen.Add(block)) work.Add(block); }
            if (common is not null) Add(common);
            else foreach (var root in postDominators.Roots)
                    if (root.GetTerminator() is not LlvmUnreachableInstruction) Add(root);
            Count("cfg_path.tries");
            for (int index = 0; index < work.Count; ++index)
            {
                Count("cfg_path.blocks.visited");
                var block = work[index];
                if (blocks.Contains(block)) continue;
                if (ReferenceEquals(block, dead.Block)) return false;
                if (!dominators.IsReachableFromEntry(block)) continue;
                foreach (var predecessor in LlvmDominatorTree.Predecessors(block)) Add(predecessor);
                if (work.Count >= options.PathCheckLimit) { Count("cfg_path.limit"); return false; }
            }
            Count("cfg_path.killed");
            return true;
        }
    }
}
