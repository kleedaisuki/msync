// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADCE.cpp updateDeadRegions/
// makeUnconditional; BasicBlock.cpp removePredecessor. Eager DomTreeUpdater
// calls are expanded to the actual supplied forward/postdom trees' batch APIs.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmAdce
{
    private sealed partial class Work
    {
        private bool UpdateDeadRegions()
        {
            bool havePostOrder = false, changed = false;
            var deletedEdges = new List<LlvmCfgUpdate>();
            foreach (var block in deadTerminators)
            {
                if (block.GetTerminator() is LlvmUncondBrInstruction unconditional)
                { liveInstructions.Add(unconditional); Count("cfg.dead_unconditional_retained"); continue; }
                if (!havePostOrder)
                {
                    if (trace is null) ComputeReversePostOrder();
                    else trace.Run("optimizer.llvm-adce.reverse-cfg-postorder", this, static state => state.ComputeReversePostOrder());
                    havePostOrder = true;
                }
                LlvmBasicBlock? preferred = null; uint preferredOrder = 0;
                foreach (var successor in LlvmDominatorTree.Successors(block))
                {
                    uint order = Info(successor).PostOrder;
                    if (preferredOrder < order) { preferred = successor; preferredOrder = order; }
                    Count("cfg.preferred_successor_candidates");
                }
                Debug.Assert(preferred is not null && preferredOrder > 0, "Failed to find safe successor for dead branch");
                var removed = new HashSet<LlvmBasicBlock>(); bool first = true;
                foreach (var successor in LlvmDominatorTree.Successors(block))
                {
                    if (!first || !ReferenceEquals(successor, preferred))
                    { RemovePredecessor(successor, block); removed.Add(successor); Count("cfg.predecessor_edges_removed"); }
                    else first = false;
                }
                MakeUnconditional(block, preferred!);
                foreach (var successor in removed)
                    if (!ReferenceEquals(successor, preferred)) deletedEdges.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
                // Preserve the source statistic's two increment sites separately
                // from actual branch rewrite counts.
                Count("source.num_branches_removed"); Count("cfg.dead_regions_rewritten"); changed = true;
            }
            if (deletedEdges.Count != 0)
            {
                dominators?.ApplyUpdates(deletedEdges, metrics: Metrics);
                postDominators.ApplyUpdates(deletedEdges, metrics: Metrics);
                Count("cfg.dominance_edge_deletions", (ulong)deletedEdges.Count);
            }
            return changed;
        }
        private void MakeUnconditional(LlvmBasicBlock block, LlvmBasicBlock target)
        {
            var previous = block.GetTerminator();
            if (previous is LlvmUncondBrInstruction unconditional)
            { unconditional.Destination = target; liveInstructions.Add(previous); return; }
            if (trace is not null)
            {
                var attributes = Attributes(previous); attributes["cfg.preferred_successor"] = target.Name;
                trace.Run("optimizer.llvm-adce.rewrite-branch", (Self: this, Previous: previous, Target: target),
                    static state => state.Self.ReplaceTerminator(state.Previous, state.Target), attributes);
            }
            else ReplaceTerminator(previous, target);
        }
        private void ReplaceTerminator(LlvmInstruction previous, LlvmBasicBlock target)
        {
            var branch = new LlvmUncondBrInstruction(target) { Source = previous.Source };
            previous.Parent!.InsertBefore(branch, previous); liveInstructions.Add(branch);
            previous.EraseFromParent(); Count("source.num_branches_removed"); Count("cfg.terminators_replaced");
        }
        private void RemovePredecessor(LlvmBasicBlock block, LlvmBasicBlock predecessor)
        {
            Debug.Assert(block.HasNUsesOrMore(16) || LlvmDominatorTree.Predecessors(block).Contains(predecessor));
            if (block.FirstInstruction is not LlvmPhiInstruction first) return;
            int count = first.IncomingCount;
            for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi;)
            {
                instruction = instruction.Next;
                phi.RemoveIncomingValue(phi.GetBasicBlockIndex(predecessor)); Count("cfg.phi_inputs_removed");
                if (count == 1) continue;
                if (phi.HasConstantValue() is not { } value) continue;
                phi.ReplaceAllUsesWith(value); phi.EraseFromParent(); Count("cfg.constant_phis_removed");
            }
        }
    }
}
