// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp
// isFoldableInLoop / isNotUsedOrFoldableInLoop / sink /
// canSplitPredecessors / splitPredecessorsOfLoopExit.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private sealed partial class Work
    {
        private bool FoldableInLoop(LlvmInstruction instruction, LlvmLoop loop)
        {
            if (instruction is not LlvmGetElementPtrInstruction gep ||
                !TargetInfo.GetInstructionCost(gep, LlvmTargetCostKind.SizeAndLatency).IsFree) return false;
            foreach (LlvmUser value in gep.Users)
            {
                var user = (LlvmInstruction)value;
                if (loop.Contains(user.Parent!) && (gep.Parent != user.Parent || user is not (LlvmStoreInstruction or LlvmLoadInstruction))) return false;
            }
            return true;
        }

        private bool NotUsedOrFoldableInLoop(LlvmInstruction instruction, LlvmLoop loop, ref bool foldable, bool loopNestMode)
        {
            bool isFoldable = FoldableInLoop(instruction, loop);
            foreach (LlvmUser value in instruction.Users)
            {
                var user = (LlvmInstruction)value;
                if (user is LlvmPhiInstruction phi)
                {
                    if (phi.Parent!.GetTerminator().Opcode == LlvmOpcode.CatchSwitch) return false;
                    if (instruction is LlvmCallInstruction && Safety.GetBlockColors() is { Count: > 0 } colors &&
                        colors[phi.Parent].Count != 1) return false;
                    if (loopNestMode)
                        while (user is LlvmPhiInstruction chain && user.HasOneUser && chain.OperandCount == 1)
                        {
                            if (!loop.Contains(user.Parent!)) break;
                            user = (LlvmInstruction)user.Users.Last();
                        }
                }
                if (!loop.Contains(user.Parent!)) continue;
                if (!isFoldable) return false;
                foldable = true;
            }
            return true;
        }

        private bool Sink(LlvmInstruction instruction, LlvmLoop loop)
        {
            if (Trace is null) return SinkCore(instruction, loop);
            return Trace.MeasureDetailed("optimizer.llvm-licm.sink", (Work: this, instruction, loop),
                static state => state.Work.SinkCore(state.instruction, state.loop), attributes: InstructionAttributes(instruction));
        }

        private bool SinkCore(LlvmInstruction instruction, LlvmLoop loop)
        {
            bool changed = false;
            HashSet<LlvmInstruction> visited = [];
            LlvmUse? use = instruction.FirstUse;
            while (use is not null)
            {
                var user = (LlvmInstruction)use.User;
                LlvmUse current = use;
                use = use.Next;
                if (visited.Contains(user) || loop.Contains(user.Parent!)) continue;
                if (!Dominators.IsReachableFromEntry(user.Parent!))
                { current.Set(LlvmPoisonValue.Get(instruction.Type)); changed = true; continue; }
                var phi = (LlvmPhiInstruction)user;
                LlvmBasicBlock incoming = phi.GetIncomingBlock(current.OperandNo);
                if (!Dominators.IsReachableFromEntry(incoming))
                { current.Set(LlvmPoisonValue.Get(instruction.Type)); changed = true; continue; }
                visited.Add(phi);
                if (TriviallyReplaceablePhi(phi, instruction)) continue;
                if (!CanSplitPredecessors(phi)) return changed;
                SplitExitPredecessors(phi, loop);
                use = instruction.FirstUse;
            }
            if (visited.Count == 0) return changed;
            Count("instructions.sunk");
            if (instruction is LlvmLoadInstruction) Count("loads.moved");
            else if (instruction is LlvmCallInstruction) Count("calls.moved");
            Dictionary<LlvmBasicBlock, LlvmInstruction> copies = [];
            List<LlvmUser> users = [];
            HashSet<LlvmUser> unique = [];
            foreach (LlvmUser user in instruction.Users) if (unique.Add(user)) users.Add(user);
            foreach (LlvmUser value in users)
            {
                var user = (LlvmInstruction)value;
                if (loop.Contains(user.Parent!)) continue;
                var phi = (LlvmPhiInstruction)user;
                Debug.Assert(TriviallyReplaceablePhi(phi, instruction));
                LlvmInstruction copy = SinkThroughPhi(phi, instruction, copies);
                phi.ReplaceAllUsesWith(copy);
                EraseInstruction(phi);
                changed = true;
            }
            return changed;
        }

        private bool CanSplitPredecessors(LlvmPhiInstruction phi)
        {
            LlvmBasicBlock block = phi.Parent!;
            LlvmInstruction first = FirstNonPhi(block);
            if (first.IsEHPad && first.Opcode != LlvmOpcode.LandingPad) return false;
            if (Safety.GetBlockColors().Count != 0 && first.IsEHPad) return false;
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block))
                if (predecessor.GetTerminator().Opcode == LlvmOpcode.IndirectBr) return false;
            return true;
        }

        private void SplitExitPredecessors(LlvmPhiInstruction phi, LlvmLoop loop)
        {
            LlvmBasicBlock exit = phi.Parent!;
            List<LlvmBasicBlock> predecessors = [];
            HashSet<LlvmBasicBlock> unique = [];
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(exit))
                if (unique.Add(predecessor)) predecessors.Add(predecessor);
            foreach (LlvmBasicBlock predecessor in predecessors)
            {
                Debug.Assert(loop.Contains(predecessor));
                if (phi.GetBasicBlockIndex(predecessor) < 0) continue;
                LlvmBasicBlock split = LlvmLoopSimplify.SplitBlockPredecessors(exit, [predecessor], ".split.loop.exit",
                    Dominators, LoopInfo, Updater, preserveLcssa: true, trace: Trace);
                if (Safety.GetBlockColors().Count != 0) Safety.CopyColors(split, predecessor);
                Count("sink.exit_edges_split");
            }
        }
    }
}
