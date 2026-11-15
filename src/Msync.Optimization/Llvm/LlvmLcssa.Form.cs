// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Transforms/Utils/LCSSA.cpp
// formLCSSAForInstructionsImpl; no ScalarEvolution, debug-record or intrinsic
// lifetime subsystem is represented by this typed IR slice.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLcssa
{
    private sealed partial class Work
    {
        internal bool FormInstructions(List<LlvmInstruction> worklist, List<LlvmPhiInstruction>? phisToRemove,
            List<LlvmPhiInstruction>? insertedPhis)
        {
            List<LlvmUse> usesToRewrite = [];
            List<LlvmPhiInstruction> localToRemove = [];
            HashSet<LlvmPhiInstruction> removeMembership = [];
            Dictionary<LlvmBasicBlock, LlvmBasicBlock[]> predecessorCache = [];
            bool changed = false;
            while (worklist.Count != 0)
            {
                usesToRewrite.Clear();
                LlvmInstruction instruction = Pop(worklist);
                Count("values.worklist_pops");
                Debug.Assert(!instruction.Type.IsTokenLikeTy());
                LlvmLoop loop = loops.GetLoopFor(instruction.Parent!)!;
                Debug.Assert(loop is not null);
                List<LlvmBasicBlock> exits = GetExitBlocks(loop);
                if (exits.Count == 0) continue;
                bool valueChanged = trace is null ? FormValue(instruction, loop, exits, usesToRewrite, predecessorCache,
                    worklist, localToRemove, removeMembership, insertedPhis) :
                    trace.MeasureDetailed("optimizer.llvm-lcssa.live-out",
                        (self: this, instruction, loop, exits, usesToRewrite, predecessorCache, worklist, localToRemove, removeMembership, insertedPhis),
                        static state => state.self.FormValue(state.instruction, state.loop, state.exits, state.usesToRewrite,
                            state.predecessorCache, state.worklist, state.localToRemove, state.removeMembership, state.insertedPhis),
                        attributes: SourceAttributes(instruction));
                changed |= valueChanged;
            }
            if (phisToRemove is not null) phisToRemove.AddRange(localToRemove);
            else foreach (LlvmPhiInstruction phi in localToRemove)
                if (phi.UseEmpty) { phi.EraseFromParent(); Count("phis.unused_removed"); }
            return changed;
        }
        private bool FormValue(LlvmInstruction instruction, LlvmLoop loop, List<LlvmBasicBlock> exits,
            List<LlvmUse> usesToRewrite, Dictionary<LlvmBasicBlock, LlvmBasicBlock[]> predecessorCache,
            List<LlvmInstruction> worklist, List<LlvmPhiInstruction> localToRemove,
            HashSet<LlvmPhiInstruction> removeMembership, List<LlvmPhiInstruction>? insertedPhis)
        {
            LlvmBasicBlock definitionBlock = instruction.Parent!;
            for (LlvmUse? use = instruction.FirstUse; use is not null;)
            {
                LlvmUse? next = use.Next;
                LlvmInstruction user = (LlvmInstruction)use.User;
                LlvmBasicBlock useBlock = user.Parent!;
                Count("uses.scanned");
                if (!tree.IsReachableFromEntry(useBlock))
                {
                    SetUse(use, LlvmPoisonValue.Get(instruction.Type), instruction.LogicalType);
                    Count("uses.unreachable_poisoned");
                    use = next;
                    continue;
                }
                if (user is LlvmPhiInstruction phi) useBlock = phi.GetIncomingBlock(use.OperandNo);
                if (!ReferenceEquals(definitionBlock, useBlock) && !loop.Contains(useBlock))
                { usesToRewrite.Add(use); Count("uses.live_out"); }
                use = next;
            }
            if (usesToRewrite.Count == 0) { Count("values.no_live_out"); return false; }
            Count("values.transformed");
            LlvmBasicBlock dominanceBlock = instruction.Opcode == LlvmOpcode.Invoke ? instruction.GetSuccessor(0) : definitionBlock;
            LlvmDominatorTreeNode? dominator = tree.GetNode(dominanceBlock);
            List<LlvmPhiInstruction> added = [], postProcess = [], localInserted = [];
            LlvmSsaUpdater updater = new(localInserted, trace: trace, metrics: Metrics);
            updater.Initialize(instruction.Type, instruction.Name, instruction.LogicalType);
            foreach (LlvmBasicBlock exit in exits)
            {
                Count("phis.exit_candidates");
                if (!tree.Dominates(dominator, tree.GetNode(exit))) { Count("phis.exit_not_dominated"); continue; }
                if (updater.HasValueForBlock(exit)) { Count("phis.duplicate_exit"); continue; }
                if (!predecessorCache.TryGetValue(exit, out LlvmBasicBlock[]? predecessors))
                {
                    predecessors = LlvmDominatorTree.Predecessors(exit).ToArray();
                    predecessorCache.Add(exit, predecessors); Count("predecessors.computed");
                }
                else Count("predecessors.cache_hit");
                LlvmPhiInstruction phi = new(instruction.Type, predecessors.Length)
                { Source = instruction.Source, LogicalType = instruction.LogicalType };
                phi.SetName(instruction.Name + ".lcssa");
                exit.InsertBefore(phi, exit.FirstInstruction);
                insertedPhis?.Add(phi);
                foreach (LlvmBasicBlock predecessor in predecessors)
                {
                    phi.AddIncoming(instruction, predecessor);
                    LlvmUse incoming = phi.GetOperandUse(phi.IncomingCount - 1);
                    incoming.LogicalType = instruction.LogicalType;
                    if (!loop.Contains(predecessor)) usesToRewrite.Add(incoming);
                }
                added.Add(phi); Count("phis.exit_inserted");
                updater.AddAvailableValue(exit, phi);
                if (loops.GetLoopFor(exit) is { } otherLoop && !loop.Contains(otherLoop)) postProcess.Add(phi);
            }
            foreach (LlvmUse use in usesToRewrite)
            {
                LlvmInstruction user = (LlvmInstruction)use.User;
                LlvmBasicBlock useBlock = user is LlvmPhiInstruction phi ? phi.GetIncomingBlock(use.OperandNo) : user.Parent!;
                if (useBlock.FirstInstruction is LlvmPhiInstruction first && exits.Contains(useBlock))
                {
                    SetUse(use, first, instruction.LogicalType); Count("uses.exit_direct"); continue;
                }
                if (added.Count == 1)
                {
                    SetUse(use, added[0], instruction.LogicalType); Count("uses.single_phi_direct"); continue;
                }
                if (use.LogicalType < 0) use.LogicalType = instruction.LogicalType;
                updater.RewriteUse(use); Count("uses.ssa_updater");
            }
            foreach (LlvmPhiInstruction phi in localInserted)
            {
                if (loops.GetLoopFor(phi.Parent!) is { } otherLoop && !loop.Contains(otherLoop)) postProcess.Add(phi);
                insertedPhis?.Add(phi); Count("phis.ssa_updater_inserted");
            }
            foreach (LlvmPhiInstruction phi in postProcess)
                if (!phi.UseEmpty) { worklist.Add(phi); Count("phis.disjoint_loop_requeued"); }
            foreach (LlvmPhiInstruction phi in added)
                if (phi.UseEmpty && removeMembership.Add(phi)) localToRemove.Add(phi);
            return true;
        }
        private static void SetUse(LlvmUse use, LlvmValue value, int logicalType)
        {
            if (use.LogicalType < 0) use.LogicalType = logicalType;
            use.Set(value);
        }
        private Dictionary<string, string> SourceAttributes(LlvmInstruction instruction)
        {
            Dictionary<string, string> attributes = new()
            {
                ["function.name"] = loops.Function.Name, ["block.name"] = instruction.Parent!.Name,
                ["instruction.name"] = instruction.Name, ["instruction.opcode"] = instruction.Opcode.ToString()
            };
            if (instruction.Source is { } source)
            {
                attributes["source.name"] = source.Source;
                attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
