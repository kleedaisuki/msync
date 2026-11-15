// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// EqualBBWrapper/DenseMapInfo, mergeIdenticalBBs, duplicate switch arms/preds.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private readonly struct EqualSwitchBlockKey(LlvmBasicBlock successor, LlvmValue[] incoming) : IEquatable<EqualSwitchBlockKey>
        {
            private readonly LlvmBasicBlock successor = successor;
            private readonly LlvmValue[] incoming = incoming;
            public bool Equals(EqualSwitchBlockKey other) => successor == other.successor && incoming.SequenceEqual(other.incoming);
            public override bool Equals(object? value) => value is EqualSwitchBlockKey other && Equals(other);
            public override int GetHashCode()
            {
                HashCode hash = new(); hash.Add(successor);
                foreach (LlvmValue value in incoming) hash.Add(value);
                return hash.ToHashCode();
            }
        }

        private static bool CanMergeEqualSwitchBlock(LlvmBasicBlock block)
        {
            if (block == block.Parent!.BasicBlocks[0] || block.GetTerminator() is not LlvmUncondBrInstruction) return false;
            if (block.Users.Any(user => user.SubclassId == (byte)LlvmValueKind.BlockAddressVal) || FirstNonPhi(block).IsEHPad) return false;
            if (block.FirstInstruction != block.LastInstruction) return false;
            return LlvmDominatorTree.Predecessors(block).Any();
        }

        private bool MergeIdenticalBBs(IReadOnlyList<LlvmBasicBlock> candidates)
        {
            Count("switch.duplicates.queries");
            if (candidates.Count < 2) return false;
            List<LlvmPhiInstruction> phis = [];
            HashSet<LlvmPhiInstruction> seenPhis = [];
            foreach (LlvmBasicBlock block in candidates)
                foreach (LlvmPhiInstruction phi in SwitchPhis(((LlvmUncondBrInstruction)block.GetTerminator()).Destination))
                    if (seenPhis.Add(phi)) phis.Add(phi);
            Dictionary<LlvmPhiInstruction, Dictionary<LlvmBasicBlock, LlvmValue>> phiInputs = [];
            foreach (LlvmPhiInstruction phi in phis)
            {
                Dictionary<LlvmBasicBlock, LlvmValue> inputs = [];
                for (int i = 0; i < phi.IncomingCount; ++i)
                { inputs.TryAdd(phi.GetIncomingBlock(i), phi.GetIncomingValue(i)); Count("switch.duplicates.phi_inputs_cached"); }
                phiInputs.Add(phi, inputs);
            }
            Dictionary<EqualSwitchBlockKey, LlvmBasicBlock> keep = [];
            List<LlvmCfgUpdate> updates = [];
            bool changed = false;
            foreach (LlvmBasicBlock block in candidates)
            {
                LlvmBasicBlock successor = ((LlvmUncondBrInstruction)block.GetTerminator()).Destination;
                EqualSwitchBlockKey key = new(successor, SwitchPhis(successor).Select(phi => phiInputs[phi][block]).ToArray());
                if (keep.TryAdd(key, block)) continue;
                LlvmBasicBlock live = keep[key];
                if (live == block) continue;
                LlvmBasicBlock[] deadPredecessors = LlvmDominatorTree.Predecessors(block).Distinct().ToArray();
                HashSet<LlvmBasicBlock> livePredecessors = LlvmDominatorTree.Predecessors(live).ToHashSet();
                foreach (LlvmBasicBlock predecessor in deadPredecessors)
                {
                    if (!livePredecessors.Contains(predecessor)) updates.Add(new(LlvmCfgUpdateKind.Insert, predecessor, live));
                    updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
                }
                foreach (LlvmBasicBlock predecessor in deadPredecessors)
                {
                    LlvmInstruction terminator = predecessor.GetTerminator();
                    for (int i = 0; i < terminator.SuccessorCount; ++i)
                        if (terminator.GetSuccessor(i) == block) terminator.SetSuccessor(i, live);
                }
                Count("switch.duplicates.blocks_redirected"); changed = true;
            }
            if (updates.Count != 0) DominatorUpdater?.ApplyUpdates(updates);
            return changed;
        }

        private bool SimplifyDuplicateSwitchArms(LlvmSwitchInstruction selection) =>
            MergeIdenticalBBs(selection.Successors.Where(CanMergeEqualSwitchBlock).Distinct().ToArray());

        private bool SimplifyDuplicatePredecessors(LlvmBasicBlock block)
        {
            if (LlvmDominatorTree.Predecessors(block).Take(2).Count() < 2) return false;
            if (Options.NeedCanonicalLoop && IsLoopHeader(block)) return false;
            return MergeIdenticalBBs(LlvmDominatorTree.Predecessors(block).Where(CanMergeEqualSwitchBlock).Distinct().ToArray());
        }
    }
}
