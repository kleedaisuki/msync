// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// foldCondBranchOnValueKnownInPredecessor{Impl,wrapper}.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool FoldCondBranchOnValueKnownInPredecessor(LlvmCondBrInstruction branch)
        {
            bool keepCanonicalLoop = Options.NeedCanonicalLoop && IsLoopHeader(branch.Parent!);
            bool changed = false;
            bool? result;
            do
            {
                result = ConditionalThreadKnownPredecessorImpl(branch, keepCanonicalLoop);
                changed |= !result.HasValue || result.Value;
            } while (!result.HasValue);
            return changed;
        }

        private bool? ConditionalThreadKnownPredecessorImpl(LlvmCondBrInstruction branch, bool keepCanonicalLoop)
        {
            Count("conditional.thread.queries");
            var block = branch.Parent!;
            var condition = branch.Condition;
            Dictionary<LlvmConstantInt, List<LlvmBasicBlock>> known = new(ReferenceEqualityComparer.Instance);
            if (condition is LlvmPhiInstruction phi && phi.Parent == block)
            {
                if (phi.IncomingCount == 1)
                {
                    LlvmBasicBlockUtils.FoldSingleEntryPHINodes(block, metrics: Metrics);
                    return true;
                }
                for (int i = 0; i < phi.IncomingCount; ++i)
                    if (phi.GetIncomingValue(i) is LlvmConstantInt constant) ConditionalAddKnown(known, constant, phi.GetIncomingBlock(i));
            }
            else
            {
                foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                    if (ConditionalKnownValueOnEdge(condition, predecessor, block) is { } constant) ConditionalAddKnown(known, constant, predecessor);
            }
            if (known.Count == 0) { Count("conditional.thread.no_known_edges"); return false; }
            HashSet<LlvmBasicBlock> nonLocal = new(ReferenceEqualityComparer.Instance), reachesNonLocal = new(ReferenceEqualityComparer.Instance);
            if (!ConditionalSimpleEnoughToThread(block, nonLocal)) return false;
            if (nonLocal.Contains(branch.GetSuccessor(0)) && nonLocal.Contains(branch.GetSuccessor(1)))
            { Count("conditional.thread.both_destinations_live"); return false; }
            foreach (var useBlock in nonLocal)
                if (!ConditionalFindReaching(useBlock, block, reachesNonLocal)) return false;
            foreach (var (constant, predecessors) in known)
            {
                var destination = branch.GetSuccessor(constant.IsZero ? 1 : 0);
                if (destination == block) { Count("conditional.thread.self_loop"); continue; }
                if (predecessors.Any(pred => pred.GetTerminator().Opcode == LlvmOpcode.IndirectBr))
                { Count("conditional.thread.indirect_predecessor"); continue; }
                if (reachesNonLocal.Contains(destination)) { Count("conditional.thread.destination_live"); continue; }
                if (keepCanonicalLoop && !ConditionalSafeToThreadLoopHeader(block, predecessors, destination))
                { Count("conditional.thread.canonical_loop"); continue; }
                if (TargetInfo.HasBranchDivergence(block.Parent) && ConditionalReachesUncontrolledConvergentCall(destination, block))
                { Count("conditional.thread.convergent_cycle"); continue; }
                var edge = LlvmBasicBlockUtils.SplitBlockPredecessors(block, predecessors, ".critedge", DominatorUpdater, metrics: Metrics);
                if (edge is null) continue;
                edge.SetName(destination.Name + ".critedge"); edge.MoveBefore(destination);
                AddPredecessorToBlock(destination, edge, block);
                var insertion = FirstNonPhi(edge);
                Dictionary<LlvmValue, LlvmValue> translated = new(ReferenceEqualityComparer.Instance) { [condition] = constant };
                for (var instruction = block.FirstInstruction; instruction != branch; instruction = instruction!.Next)
                {
                    if (instruction is LlvmPhiInstruction incoming)
                    { translated[incoming] = incoming.GetIncomingValue(incoming.GetBasicBlockIndex(edge)); continue; }
                    var clone = instruction!.Clone();
                    edge.InsertBefore(clone, insertion);
                    if (instruction.HasName) clone.SetName(instruction.Name + ".c");
                    LlvmValueMapper.RemapInstruction(clone, translated, LlvmRemapFlags.IgnoreMissingLocals | LlvmRemapFlags.NoModuleLevelChanges);
                    var simplified = LlvmInstructionSimplify.SimplifyInstruction(clone,
                        new LlvmInstructionSimplify.BinaryQuery(Layout, null, Metrics, Assumptions: Options.AC));
                    if (simplified is not null)
                    {
                        if (!instruction.UseEmpty) translated[instruction] = simplified;
                        if (!clone.MayHaveSideEffects()) { clone.EraseFromParent(); Count("conditional.thread.clones_folded"); }
                    }
                    else if (!instruction.UseEmpty) translated[instruction] = clone;
                    Count("conditional.thread.instructions_cloned");
                }
                LlvmBasicBlockUtils.RemovePredecessor(block, edge, metrics: Metrics);
                var edgeBranch = (LlvmUncondBrInstruction)edge.GetTerminator();
                edgeBranch.SetSuccessor(0, destination); edgeBranch.Source = branch.Source;
                DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, edge, block), new(LlvmCfgUpdateKind.Insert, edge, destination)]);
                LlvmBasicBlockUtils.MergeBlockIntoPredecessor(edge, DominatorUpdater, metrics: Metrics);
                Count("conditional.thread.edges_threaded");
                return null;
            }
            return false;
        }

        private static void ConditionalAddKnown(Dictionary<LlvmConstantInt, List<LlvmBasicBlock>> known,
            LlvmConstantInt value, LlvmBasicBlock block)
        {
            if (!known.TryGetValue(value, out var blocks)) known.Add(value, blocks = []);
            if (!blocks.Contains(block)) blocks.Add(block);
        }
    }
}
