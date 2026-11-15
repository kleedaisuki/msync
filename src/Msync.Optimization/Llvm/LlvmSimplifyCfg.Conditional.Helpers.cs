// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// findReaching/isSafeToThreadThroughLoopHeader/getKnownValueOnEdge.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool ConditionalFindReaching(LlvmBasicBlock block, LlvmBasicBlock definition,
            HashSet<LlvmBasicBlock> reachesUses)
        {
            Count("conditional.thread.live_blocks");
            if (block == definition || !reachesUses.Add(block)) return true;
            if ((uint)reachesUses.Count > Tuning.MaxJumpThreadingLiveBlocks)
            { Count("conditional.thread.live_budget"); return false; }
            foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                if (!ConditionalFindReaching(predecessor, definition, reachesUses)) return false;
            return true;
        }

        private bool ConditionalSafeToThreadLoopHeader(LlvmBasicBlock header, IReadOnlyList<LlvmBasicBlock> predecessors,
            LlvmBasicBlock destination)
        {
            if (LlvmDominatorTree.Predecessors(header).Take(3).Count() != 2) return false;
            HashSet<LlvmBasicBlock> visited = new(ReferenceEqualityComparer.Instance);
            List<LlvmBasicBlock> work = new(predecessors);
            while (work.Count != 0)
            {
                var block = work[^1]; work.RemoveAt(work.Count - 1);
                if (block == header || !visited.Add(block)) continue;
                Count("conditional.thread.loop_header_search");
                if (block == destination || (uint)visited.Count > Tuning.MaxLoopHeaderThreadingSearchBlocks) return false;
                bool hasPredecessor = false;
                foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                { hasPredecessor = true; work.Add(predecessor); }
                if (!hasPredecessor) return false;
            }
            return true;
        }

        private static LlvmConstantInt? ConditionalKnownValueOnEdge(LlvmValue value, LlvmBasicBlock from, LlvmBasicBlock to)
        {
            if (value is LlvmInstruction instruction && instruction.Parent == to) return null;
            if (from.GetTerminator() is not LlvmCondBrInstruction branch || branch.Condition != value || branch.GetSuccessor(0) == branch.GetSuccessor(1)) return null;
            return LlvmConstantInt.GetBool(value.Context, branch.GetSuccessor(0) == to);
        }

        private bool ConditionalSimpleEnoughToThread(LlvmBasicBlock block, HashSet<LlvmBasicBlock> nonLocalUses)
        {
            int size = 0;
            var ephemerals = new EphemeralValueTracker(Metrics);
            for (var instruction = block.LastInstruction; instruction is not null; instruction = instruction.Previous)
            {
                Count("conditional.thread.block_instructions");
                if (instruction is LlvmCallInstruction call && (call.CannotDuplicate || call.IsConvergent))
                { Count("conditional.thread.noduplicate_or_convergent"); return false; }
                if (!ephemerals.Track(instruction) && instruction is not LlvmPhiInstruction && size++ > Tuning.MaxSmallBlockSize)
                { Count("conditional.thread.block_size_budget"); return false; }
                foreach (var user in instruction.Users)
                {
                    var use = (LlvmInstruction)user;
                    if (use.Parent != block) nonLocalUses.Add(use.Parent!);
                    else if (use is LlvmPhiInstruction) { Count("conditional.thread.local_phi_cycle"); return false; }
                }
            }
            return true;
        }

        private bool ConditionalReachesUncontrolledConvergentCall(LlvmBasicBlock from, LlvmBasicBlock stop)
        {
            const uint maxInstructions = 512;
            HashSet<LlvmBasicBlock> reachesStop = new(ReferenceEqualityComparer.Instance), convergentBlocks = new(ReferenceEqualityComparer.Instance);
            List<LlvmBasicBlock> work = [.. LlvmDominatorTree.Predecessors(stop)];
            uint scanned = 0;
            while (work.Count != 0)
            {
                var block = work[^1]; work.RemoveAt(work.Count - 1);
                if (block == stop || !reachesStop.Add(block)) continue;
                foreach (var instruction in block.Instructions)
                {
                    Count("conditional.thread.convergent_scan");
                    if (++scanned > maxInstructions) return true;
                    if (instruction is LlvmCallBase { IsConvergent: true } call && call.GetConvergenceControlToken() is null)
                    { convergentBlocks.Add(block); break; }
                }
                work.AddRange(LlvmDominatorTree.Predecessors(block));
            }
            if (!reachesStop.Contains(from)) return false;
            HashSet<LlvmBasicBlock> visited = new(ReferenceEqualityComparer.Instance);
            work.Add(from);
            while (work.Count != 0)
            {
                var block = work[^1]; work.RemoveAt(work.Count - 1);
                if (block == stop || !reachesStop.Contains(block) || !visited.Add(block)) continue;
                if (convergentBlocks.Contains(block)) return true;
                work.AddRange(block.GetTerminator().Successors);
            }
            return false;
        }
    }
}
