// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SCCPSolver.cpp removeNonFeasibleEdges; Local.cpp changeToUnreachable;
// BasicBlock.cpp removePredecessor; no preserved cached analyses are returned.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmSccp
{
    private sealed partial class Rewrite
    {
        private void RemovePredecessor(LlvmBasicBlock block, LlvmBasicBlock predecessor)
        {
            if (block.FirstInstruction is not LlvmPhiInstruction first) return;
            int originalPreds = first.IncomingCount;
            for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi;)
            {
                instruction = instruction.Next;
                int index = phi.GetBasicBlockIndex(predecessor);
                Debug.Assert(index >= 0);
                phi.RemoveIncomingValue(index);
                Count("rewrite.phi_inputs_removed");
                if (originalPreds == 1) continue;
                if (phi.HasConstantValue() is not { } value) continue;
                phi.ReplaceAllUsesWith(value); phi.EraseFromParent(); Count("rewrite.phis_removed");
            }
        }
        private void ChangeToUnreachable(LlvmBasicBlock block)
        {
            foreach (var successor in LlvmDominatorTree.Successors(block)) RemovePredecessor(successor, block);
            var first = block.FirstInstruction!;
            while (first is LlvmPhiInstruction) first = first.Next!;
            var unreachable = new LlvmUnreachableInstruction(function.Context) { Source = first.Source };
            block.InsertBefore(unreachable, first);
            for (var instruction = first; instruction is not null;)
            {
                var current = instruction; instruction = current.Next;
                if (!current.UseEmpty) current.ReplaceAllUsesWith(LlvmPoisonValue.Get(current.Type));
                current.EraseFromParent(); Count("rewrite.dead_block_instructions_removed");
            }
        }
        private bool RemoveNonFeasibleEdges(LlvmBasicBlock block)
        {
            var terminator = block.GetTerminator();
            var feasible = new HashSet<LlvmBasicBlock>(); bool hasNonFeasible = false;
            foreach (var successor in LlvmDominatorTree.Successors(block))
            {
                if (Solver.IsEdgeFeasible(block, successor)) feasible.Add(successor);
                else hasNonFeasible = true;
            }
            if (!hasNonFeasible) return false;
            if (trace is null) RemoveEdges(block, terminator, feasible);
            else trace.Run("optimizer.llvm-sccp.prune-edges", (Self: this, Block: block, Terminator: terminator, Feasible: feasible),
                static state => state.Self.RemoveEdges(state.Block, state.Terminator, state.Feasible), InstructionAttributes(terminator));
            return true;
        }
        private void RemoveEdges(LlvmBasicBlock block, LlvmInstruction terminator, HashSet<LlvmBasicBlock> feasible)
        {
            if (feasible.Count == 0)
            {
                foreach (var successor in LlvmDominatorTree.Successors(block))
                { RemovePredecessor(successor, block); Count("rewrite.cfg_edges_removed"); }
                var unreachable = new LlvmUnreachableInstruction(function.Context) { Source = terminator.Source };
                terminator.EraseFromParent(); block.Append(unreachable);
                Count("rewrite.branch_to_unreachable"); return;
            }
            if (feasible.Count == 1)
            {
                var only = feasible.First(); bool seen = false;
                foreach (var successor in LlvmDominatorTree.Successors(block))
                {
                    if (ReferenceEquals(successor, only) && !seen) { seen = true; continue; }
                    RemovePredecessor(successor, block); Count("rewrite.cfg_edges_removed");
                }
                var branch = new LlvmUncondBrInstruction(only) { Source = terminator.Source };
                block.Append(branch); terminator.EraseFromParent(); Count("rewrite.branch_to_unconditional"); return;
            }
            var selection = (LlvmSwitchInstruction)terminator;
            if (!feasible.Contains(selection.DefaultDestination))
            {
                var original = selection.DefaultDestination;
                if (unreachableDefault is null)
                {
                    unreachableDefault = new(function.Context, "default.unreachable", function, original);
                    unreachableDefault.Append(new LlvmUnreachableInstruction(function.Context));
                }
                RemovePredecessor(original, block); selection.DefaultDestination = unreachableDefault;
                Count("rewrite.switch_default_to_unreachable");
            }
            for (int index = 0; index < selection.CaseCount;)
            {
                var successor = selection.GetCaseDestination(index);
                if (feasible.Contains(successor)) { ++index; continue; }
                RemovePredecessor(successor, block); selection.RemoveCase(index); Count("rewrite.switch_cases_removed");
            }
        }
        private void DeleteBlock(LlvmBasicBlock block)
        {
            for (var instruction = block.FirstInstruction; instruction is not null;)
            {
                var current = instruction; instruction = current.Next;
                if (!current.UseEmpty) current.ReplaceAllUsesWith(LlvmPoisonValue.Get(current.Type));
                current.EraseFromParent();
            }
            Debug.Assert(block.UseEmpty);
            block.EraseFromParent(); Count("rewrite.blocks_removed");
        }
    }
}
