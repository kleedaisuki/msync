// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADCE.cpp initialize,
// isAlwaysLive, markLiveInstructions/markLive/markPhiLive/control dependence.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmAdce
{
    private sealed partial class Work
    {
        private void Initialize()
        {
            blockInfo = new BlockInfo[function.GetMaxBlockNumber()];
            int count = function.BasicBlocks.Sum(block => block.Instructions.Count);
            liveInstructions.EnsureCapacity(count);
            foreach (var block in function.BasicBlocks)
                foreach (var instruction in block.Instructions)
                {
                    Count("initialize.instructions");
                    if (AlwaysLive(instruction)) MarkLive(instruction);
                }
            if (!options.RemoveControlFlow) return;
            if (!options.RemoveLoops)
                foreach (var edge in FindFunctionBackedges())
                { MarkLive(edge.From.GetTerminator()); Count("initialize.backedges_preserved"); }
            foreach (var child in postDominators.RootNode!.Children)
            {
                if (child.Block!.GetTerminator() is LlvmReturnInstruction) continue;
                var stack = new Stack<LlvmDominatorTreeNode>(); stack.Push(child);
                while (stack.TryPop(out var node))
                {
                    MarkLive(node.Block!.GetTerminator()); Count("initialize.nonreturn_region_blocks");
                    var children = node.Children.ToArray();
                    for (int i = children.Length - 1; i >= 0; --i) stack.Push(children[i]);
                }
            }
            var entry = function.BasicBlocks[0]; Info(entry).Live = true;
            if (entry.GetTerminator() is LlvmUncondBrInstruction) MarkLive(entry.GetTerminator());
            foreach (var block in function.BasicBlocks)
                if (!liveInstructions.Contains(block.GetTerminator()))
                { deadTerminators.Add(block); deadTerminatorMembership.Add(block); }
            Count("initialize.dead_terminators", (ulong)deadTerminators.Count);
        }
        private bool AlwaysLive(LlvmInstruction instruction)
        {
            if (instruction.IsEHPad || instruction.MayHaveSideEffects())
            {
                // InstrProfData.inc expands getInstrProfValueProfFuncName to this exact runtime name.
                if (instruction is LlvmCallInstruction { CalledFunction.Name: "__llvm_profile_instrument_target" } call &&
                    call.GetArgument(0) is LlvmConstant)
                { Count("roots.constant_value_profile_ignored"); return false; }
                Count("roots.side_effect_or_eh"); return true;
            }
            if (!instruction.IsTerminator) return false;
            if (options.RemoveControlFlow && instruction is LlvmUncondBrInstruction or LlvmCondBrInstruction or LlvmSwitchInstruction) return false;
            Count("roots.terminators"); return true;
        }
        private void MarkLive(LlvmInstruction instruction)
        {
            if (!liveInstructions.Add(instruction)) { Count("liveness.already_live"); return; }
            Count("liveness.instructions_marked");
            if (trace is null) MarkLiveCore(instruction);
            else trace.Run("optimizer.llvm-adce.live-instruction", (Self: this, Instruction: instruction),
                static state => state.Self.MarkLiveCore(state.Instruction), Attributes(instruction));
        }
        private void MarkLiveCore(LlvmInstruction instruction)
        {
            worklist.Add(instruction);
            var block = instruction.Parent!;
            if (ReferenceEquals(instruction, block.LastInstruction))
            {
                if (deadTerminatorMembership.Remove(block)) deadTerminators.Remove(block);
                if (instruction is not LlvmUncondBrInstruction)
                    foreach (var successor in instruction.Successors) MarkLive(successor);
            }
            MarkLive(block);
        }
        private void MarkLive(LlvmBasicBlock block)
        {
            ref var info = ref Info(block);
            if (info.Live) return;
            info.Live = true; Count("liveness.blocks_marked");
            if (!info.CfLive) { info.CfLive = true; newLiveBlocks.Add(block); }
            if (block.GetTerminator() is LlvmUncondBrInstruction branch) MarkLive(branch);
        }
        private void MarkLiveInstructions()
        {
            do
            {
                Count("liveness.rounds");
                while (worklist.Count != 0)
                {
                    var instruction = worklist[^1]; worklist.RemoveAt(worklist.Count - 1);
                    Count("liveness.worklist_pops");
                    foreach (var operand in instruction.OperandUses)
                    { Count("liveness.operands"); if (operand.Value is LlvmInstruction defining) MarkLive(defining); }
                    if (instruction is LlvmPhiInstruction phi) MarkPhiLive(phi);
                }
                if (trace is null) MarkControlDependences();
                else trace.Run("optimizer.llvm-adce.control-dependence", this, static state => state.MarkControlDependences());
            } while (worklist.Count != 0);
        }
        private void MarkPhiLive(LlvmPhiInstruction phi)
        {
            ref var info = ref Info(phi.Parent!);
            if (info.HasLivePhiNodes) return;
            info.HasLivePhiNodes = true;
            foreach (var predecessor in LlvmDominatorTree.Predecessors(phi.Parent!))
            {
                Count("liveness.phi_predecessors");
                ref var predInfo = ref Info(predecessor);
                if (predInfo.CfLive) continue;
                predInfo.CfLive = true; newLiveBlocks.Add(predecessor);
            }
        }
        private void MarkControlDependences()
        {
            if (deadTerminators.Count == 0) return;
            var frontier = ReverseIdf(postDominators, newLiveBlocks, deadTerminatorMembership, Metrics);
            newLiveBlocks.Clear();
            foreach (var block in frontier)
            { MarkLive(block.GetTerminator()); Count("liveness.control_sources_marked"); }
        }
    }
}
