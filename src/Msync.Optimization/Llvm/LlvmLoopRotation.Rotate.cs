// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/LoopRotationUtils.cpp rotateLoop; represented scalar CFG paths.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopRotation
{
    private sealed partial class Work
    {
        private bool RotateLoop(bool simplifiedLatch)
        {
            if (loop.BlockCount == 1) { Count("skip.single_block"); return false; }
            LlvmBasicBlock originalHeader = loop.Header;
            LlvmBasicBlock? originalLatch = loop.GetLoopLatch();
            if (originalHeader.GetTerminator() is not LlvmCondBrInstruction branch)
            { Count("skip.header_not_conditional"); return false; }
            if (!loop.IsLoopExiting(originalHeader)) { Count("skip.header_not_exiting"); return false; }
            if (originalLatch is null) { Count("skip.no_single_latch"); return false; }
            if (loop.IsLoopExiting(originalLatch) && !simplifiedLatch && !isUtilMode && !ProfitableExitingLatch())
            { Count("skip.exiting_latch_profitability"); return false; }
            bool withinCost = trace is null ? HeaderWithinCost(originalHeader) :
                trace.MeasureDetailed("optimizer.llvm-loop-rotate.header-cost", (Self: this, Header: originalHeader),
                    static state => state.Self.HeaderWithinCost(state.Header));
            if (!withinCost) return false;
            LlvmBasicBlock? originalPreheader = loop.GetLoopPreheader();
            if (originalPreheader is null || !loop.HasDedicatedExits())
            { Count("skip.not_loop_simplify_form"); return false; }
            LlvmBasicBlock exit = branch.GetSuccessor(0), newHeader = branch.GetSuccessor(1);
            if (loop.Contains(exit)) (exit, newHeader) = (newHeader, exit);
            Debug.Assert(loop.Contains(newHeader) && !loop.Contains(exit));
            Debug.Assert(LlvmBasicBlockUtils.GetSinglePredecessor(newHeader) is not null);
            LlvmBasicBlockUtils.FoldSingleEntryPHINodes(newHeader, trace: trace, metrics: Metrics);
            Dictionary<LlvmValue, LlvmValue> values = [], memoryValues = [];
            if (trace is null) CloneHeader(originalHeader, originalPreheader, values, memoryValues);
            else trace.Run("optimizer.llvm-loop-rotate.clone-header",
                (Self: this, Header: originalHeader, Preheader: originalPreheader, Values: values, MemoryValues: memoryValues),
                static state => state.Self.CloneHeader(state.Header, state.Preheader, state.Values, state.MemoryValues));
            // CloneHeader has installed the copied header terminator before the
            // original preheader branch. Repair successor PHIs before erasing it.
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(originalHeader))
                for (LlvmInstruction? current = successor.FirstInstruction; current is LlvmPhiInstruction phi; current = current.Next)
                {
                    int index = phi.GetBasicBlockIndex(originalHeader);
                    LlvmValue value = phi.GetIncomingValue(index);
                    int logicalType = phi.GetOperandUse(index).LogicalType;
                    phi.AddIncoming(value, originalPreheader);
                    phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = logicalType;
                    Count("phis.successor_incoming_duplicated");
                }
            originalPreheader.GetTerminator().EraseFromParent();
            if (memoryUpdater is not null)
            {
                InsertMap(memoryValues, originalHeader, originalPreheader);
                memoryUpdater.UpdateForClonedBlockIntoPred(originalHeader, originalPreheader, memoryValues);
                Count("memory_ssa.cloned_block_updates");
            }
            List<LlvmPhiInstruction> inserted = [];
            if (trace is null) RewriteClonedUses(originalHeader, originalPreheader, values, inserted);
            else trace.Run("optimizer.llvm-loop-rotate.ssa-repair",
                (Self: this, Header: originalHeader, Preheader: originalPreheader, Values: values, Inserted: inserted),
                static state => state.Self.RewriteClonedUses(state.Header, state.Preheader, state.Values, state.Inserted));
            Count("phis.ssa_inserted", (ulong)inserted.Count);
            loop.MoveToHeader(newHeader);
            LlvmCfgUpdate[] updates =
            [
                new(LlvmCfgUpdateKind.Insert, originalPreheader, exit),
                new(LlvmCfgUpdateKind.Insert, originalPreheader, newHeader),
                new(LlvmCfgUpdateKind.Delete, originalPreheader, originalHeader)
            ];
            if (memoryUpdater is not null) memoryUpdater.ApplyUpdates(updates, tree, updateDtFirst: true);
            else tree.ApplyUpdates(updates, metrics: Metrics);
            LlvmCondBrInstruction preheaderBranch = (LlvmCondBrInstruction)originalPreheader.GetTerminator();
            LlvmValue condition = preheaderBranch.Condition;
            bool conditionalPreheader = condition is not LlvmConstantInt constant ||
                !ReferenceEquals(preheaderBranch.GetSuccessor(constant.IsZero ? 1 : 0), newHeader);
            // Source updateBranchWeights returns immediately when branch-weight
            // metadata is absent, as in the represented instruction carrier.
            if (conditionalPreheader)
            {
                if (trace is null) RestoreLoopSimplifyForm(originalPreheader, newHeader, exit);
                else trace.Run("optimizer.llvm-loop-rotate.restore-loop-simplify",
                    (Self: this, Preheader: originalPreheader, Header: newHeader, Exit: exit),
                    static state => state.Self.RestoreLoopSimplifyForm(state.Preheader, state.Header, state.Exit));
            }
            else FoldEnteringPreheader(preheaderBranch, newHeader, exit);
            Debug.Assert(loop.GetLoopPreheader() is not null && loop.GetLoopLatch() is not null);
            using var updater = new LlvmDomTreeUpdater(tree, LlvmDomTreeUpdateStrategy.Eager, trace: trace, metrics: Metrics);
            LlvmBasicBlockUtils.MergeBlockIntoPredecessor(originalHeader, updater: updater, loops: loops,
                memoryUpdater: memoryUpdater, trace: trace, metrics: Metrics);
            return true;
        }
        private bool HeaderWithinCost(LlvmBasicBlock header)
        {
            HashSet<LlvmValue> ephemeral = [];
            LlvmCodeMetrics.CollectEphemeralValues(loop, assumptions?.Assumptions().Select(static handle => handle.Value as LlvmInstruction), ephemeral, Metrics);
            LlvmCodeMetrics metrics = new(Metrics);
            metrics.AnalyzeBasicBlock(header, target, ephemeral, prepareForLto, loop);
            if (metrics.NotDuplicatable) { Count("skip.not_duplicatable"); return false; }
            if (metrics.Convergence != LlvmConvergenceKind.None) { Count("skip.convergent"); return false; }
            if (!metrics.NumInsts.IsValid) { Count("skip.invalid_cost"); return false; }
            Count("cost.header", (ulong)metrics.NumInsts.Value);
            if (Metrics is not null)
                Count("cost.header.quarter_units", (ulong)(metrics.NumInsts * new LlvmInstructionCost(4)).Value);
            if (metrics.NumInsts > new LlvmInstructionCost(maxHeaderSize)) { Count("skip.header_cost"); return false; }
            if (prepareForLto && metrics.NumInlineCandidates > 0) { Count("skip.lto_inline_candidate"); return false; }
            return true;
        }
        private bool ProfitableExitingLatch()
        {
            LlvmCondBrInstruction branch = (LlvmCondBrInstruction)loop.Header.GetTerminator();
            LlvmBasicBlock exit = branch.GetSuccessor(0);
            if (loop.Contains(exit)) exit = branch.GetSuccessor(1);
            for (LlvmInstruction? current = loop.Header.FirstInstruction; current is LlvmPhiInstruction phi; current = current.Next)
            {
                bool usedElsewhere = false;
                foreach (LlvmUser user in phi.Users)
                {
                    Count("profitability.phi_users");
                    if (ReferenceEquals(((LlvmInstruction)user).Parent, exit)) continue;
                    usedElsewhere = true;
                    break;
                }
                if (!usedElsewhere) return true;
            }
            // ScalarEvolution is not represented: this is exactly the source
            // optional-null SE branch, including when CheckExitCount is enabled.
            if (checkExitCount) Count("profitability.exit_count_no_scalar_evolution");
            return false;
        }
        private void RestoreLoopSimplifyForm(LlvmBasicBlock preheader, LlvmBasicBlock newHeader, LlvmBasicBlock exit)
        {
            var options = new LlvmCriticalEdgeSplittingOptions(tree, loops, memoryUpdater).SetPreserveLCSSA();
            LlvmBasicBlock newPreheader = LlvmBasicBlockUtils.SplitCriticalEdge(preheader, newHeader, options, trace: trace, metrics: Metrics)!;
            newPreheader.SetName(newHeader.Name + ".lr.ph");
            Count("cfg.preheader_edges_split");
            LlvmBasicBlock[] predecessors = LlvmDominatorTree.Predecessors(exit).ToArray();
            bool splitLatch = false;
            foreach (LlvmBasicBlock predecessor in predecessors)
            {
                LlvmLoop? predecessorLoop = loops.GetLoopFor(predecessor);
                if (predecessorLoop is null || predecessorLoop.Contains(exit) || predecessor.GetTerminator().Opcode == LlvmOpcode.IndirectBr) continue;
                splitLatch |= ReferenceEquals(loop.GetLoopLatch(), predecessor);
                LlvmBasicBlock split = LlvmBasicBlockUtils.SplitCriticalEdge(predecessor, exit,
                    new LlvmCriticalEdgeSplittingOptions(tree, loops, memoryUpdater).SetPreserveLCSSA(), trace: trace, metrics: Metrics)!;
                split.MoveBefore(exit);
                Count("cfg.exit_edges_split");
            }
            Debug.Assert(splitLatch);
        }
        private void FoldEnteringPreheader(LlvmCondBrInstruction branch, LlvmBasicBlock newHeader, LlvmBasicBlock exit)
        {
            LlvmBasicBlock preheader = branch.Parent!;
            LlvmBasicBlockUtils.RemovePredecessor(exit, preheader, keepOneInputPhis: true, trace: trace, metrics: Metrics);
            preheader.InsertBefore(new LlvmUncondBrInstruction(newHeader) { Source = branch.Source }, branch);
            branch.EraseFromParent();
            tree.DeleteEdge(preheader, exit, Metrics);
            memoryUpdater?.RemoveEdge(preheader, exit);
            Count("cfg.entering_preheader_folded");
        }
    }
}
