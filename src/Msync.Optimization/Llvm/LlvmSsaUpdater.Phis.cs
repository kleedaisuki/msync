// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/SSAUpdaterImpl.h FindSingularVal/FindAvailableVals/PHI matching.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSsaUpdater
{
    private sealed partial class Solver
    {
        private bool FindSingularValue(BlockInfo info)
        {
            if (info.Predecessors.Length == 0) return false;
            LlvmValue? singular = info.Predecessors[0].Definition!.AvailableValue;
            if (singular is null) return false;
            for (int index = 1; index < info.Predecessors.Length; ++index)
                if (!ReferenceEquals(singular, info.Predecessors[index].Definition!.AvailableValue)) return false;
            updater.available[info.Block!] = singular; info.AvailableValue = singular;
            info.Definition = info.Predecessors[0].Definition;
            Count("values.singular"); return true;
        }
        private void FindAvailableValues()
        {
            foreach (BlockInfo info in blockList)
            {
                if (!ReferenceEquals(info.Definition, info) || FindSingularValue(info)) continue;
                FindExistingPhi(info.Block!);
                if (info.AvailableValue is not null) continue;
                LlvmPhiInstruction phi = updater.CreateEmptyPhi(info.Block!, info.Predecessors.Length);
                info.AvailableValue = phi; updater.available[info.Block!] = phi;
                Count("phi.created");
            }
            for (int index = blockList.Count - 1; index >= 0; --index)
            {
                BlockInfo info = blockList[index];
                if (!ReferenceEquals(info.Definition, info))
                {
                    updater.available[info.Block!] = info.Definition!.AvailableValue;
                    Count("values.cached_from_definition"); continue;
                }
                if (info.AvailableValue is not LlvmPhiInstruction { IncomingCount: 0 } phi) continue;
                foreach (BlockInfo predecessor in info.Predecessors)
                {
                    BlockInfo definition = ReferenceEquals(predecessor.Definition, predecessor) ? predecessor : predecessor.Definition!;
                    phi.AddIncoming(definition.AvailableValue!, predecessor.Block!); Count("phi.incoming_added");
                }
                updater.insertedPhis?.Add(phi);
                Count("phi.filled");
            }
        }
        private void FindExistingPhi(LlvmBasicBlock block)
        {
            var tagged = new List<BlockInfo>();
            int count = 0;
            for (LlvmInstruction? instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                if (++count > updater.PhiSearchLimit) { Count("phi.search_limit_reached"); break; }
                Count("phi.candidates");
                if (!CheckIfPhiMatches(phi, tagged)) { Count("phi.candidate_mismatch"); continue; }
                foreach (BlockInfo info in tagged)
                {
                    Debug.Assert(info.PhiTag is not null);
                    LlvmBasicBlock parent = info.PhiTag.Parent!;
                    updater.available[parent] = info.PhiTag; blocks[parent].AvailableValue = info.PhiTag;
                    Count("phi.reused");
                }
                break;
            }
        }
        private bool CheckIfPhiMatches(LlvmPhiInstruction phi, List<BlockInfo> tagged)
        {
            bool matched = false;
            try
            {
                var pending = new List<LlvmPhiInstruction> { phi };
                BlockInfo phiBlock = blocks[phi.Parent!]; phiBlock.PhiTag = phi; tagged.Add(phiBlock);
                while (pending.Count != 0)
                {
                    LlvmPhiInstruction current = pending[^1]; pending.RemoveAt(pending.Count - 1);
                    for (int index = 0; index < current.IncomingCount; ++index)
                    {
                        Count("phi.match_incoming");
                        LlvmValue incoming = current.GetIncomingValue(index);
                        BlockInfo predecessor = blocks[current.GetIncomingBlock(index)];
                        if (!ReferenceEquals(predecessor.Definition, predecessor)) predecessor = predecessor.Definition!;
                        if (predecessor.AvailableValue is not null)
                        {
                            if (ReferenceEquals(incoming, predecessor.AvailableValue)) continue;
                            return false;
                        }
                        if (incoming is not LlvmPhiInstruction incomingPhi || !ReferenceEquals(incomingPhi.Parent, predecessor.Block)) return false;
                        if (predecessor.PhiTag is not null)
                        {
                            if (ReferenceEquals(incomingPhi, predecessor.PhiTag)) continue;
                            return false;
                        }
                        predecessor.PhiTag = incomingPhi; tagged.Add(predecessor); pending.Add(incomingPhi);
                    }
                }
                matched = true; return true;
            }
            finally
            {
                if (!matched)
                {
                    foreach (BlockInfo info in tagged) info.PhiTag = null;
                    tagged.Clear();
                }
            }
        }
    }
}
