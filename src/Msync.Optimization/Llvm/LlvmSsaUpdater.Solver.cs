// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/SSAUpdaterImpl.h GetValue/BuildBlockList/local dominators/placement.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSsaUpdater
{
    private sealed partial class Solver
    {
        private sealed class BlockInfo(LlvmBasicBlock? block, LlvmValue? value)
        {
            internal readonly LlvmBasicBlock? Block = block;
            internal LlvmValue? AvailableValue = value;
            internal BlockInfo? Definition;
            internal int Number;
            internal BlockInfo? IDom;
            internal BlockInfo[] Predecessors = [];
            internal LlvmPhiInstruction? PhiTag;
        }
        private readonly LlvmSsaUpdater updater;
        private readonly Dictionary<LlvmBasicBlock, BlockInfo> blocks = [];
        private readonly Dictionary<string, ulong>? metrics;
        private readonly List<BlockInfo> blockList = [];
        internal Solver(LlvmSsaUpdater updater)
        { this.updater = updater; metrics = updater.trace is null ? null : []; }
        private void Count(string name, ulong value = 1)
        {
            if (metrics is null && updater.metrics is null) return;
            name = "ssa_updater." + name;
            if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + value;
            if (updater.metrics is not null) updater.metrics[name] = updater.metrics.GetValueOrDefault(name) + value;
        }
        internal LlvmValue GetValue(LlvmBasicBlock block)
        {
            BlockInfo entry;
            if (updater.trace is null) entry = BuildBlockList(block);
            else entry = updater.trace.Measure("optimizer.llvm-ssa-updater.backward-and-postorder", (Solver: this, Block: block),
                static state => state.Solver.BuildBlockList(state.Block));
            LlvmValue result;
            if (blockList.Count == 0)
            {
                result = LlvmPoisonValue.Get(updater.prototypeType); updater.available[block] = result;
                Count("blocks.unreachable_query");
            }
            else
            {
                if (updater.trace is null) { FindDominators(entry); FindPhiPlacement(); FindAvailableValues(); }
                else
                {
                    updater.trace.Run("optimizer.llvm-ssa-updater.local-dominators", (Solver: this, Entry: entry), static state => state.Solver.FindDominators(state.Entry));
                    updater.trace.Run("optimizer.llvm-ssa-updater.phi-placement", this, static state => state.FindPhiPlacement());
                    updater.trace.Run("optimizer.llvm-ssa-updater.available-values", this, static state => state.FindAvailableValues());
                }
                result = blocks[block].Definition!.AvailableValue!;
            }
            if (metrics is not null) foreach (var metric in metrics) updater.trace!.Metric(metric.Key, (long)metric.Value);
            return result;
        }
        private BlockInfo NewInfo(LlvmBasicBlock block, LlvmValue? value)
        {
            BlockInfo info = new(block, value);
            if (value is not null) info.Definition = info;
            blocks.Add(block, info); Count("blocks.created"); return info;
        }
        private static BlockInfo Pop(List<BlockInfo> work)
        { BlockInfo value = work[^1]; work.RemoveAt(work.Count - 1); return value; }
        private BlockInfo BuildBlockList(LlvmBasicBlock block)
        {
            var roots = new List<BlockInfo>();
            var work = new List<BlockInfo> { NewInfo(block, null) };
            while (work.Count != 0)
            {
                BlockInfo info = Pop(work);
                LlvmBasicBlock[] predecessors = Predecessors(info.Block!).ToArray();
                info.Predecessors = new BlockInfo[predecessors.Length];
                for (int index = 0; index < predecessors.Length; ++index)
                {
                    Count("backward.predecessor_edges");
                    LlvmBasicBlock predecessor = predecessors[index];
                    if (blocks.TryGetValue(predecessor, out BlockInfo? existing)) { info.Predecessors[index] = existing; continue; }
                    BlockInfo predecessorInfo = NewInfo(predecessor, updater.available.GetValueOrDefault(predecessor));
                    info.Predecessors[index] = predecessorInfo;
                    if (predecessorInfo.AvailableValue is not null) { roots.Add(predecessorInfo); Count("backward.definition_roots"); }
                    else work.Add(predecessorInfo);
                }
            }
            BlockInfo pseudoEntry = new(null, null);
            int number = 1;
            while (roots.Count != 0)
            {
                BlockInfo info = Pop(roots); info.IDom = pseudoEntry; info.Number = -1; work.Add(info);
            }
            while (work.Count != 0)
            {
                BlockInfo info = work[^1];
                if (info.Number == -2)
                {
                    info.Number = number++;
                    if (info.AvailableValue is null) blockList.Add(info);
                    work.RemoveAt(work.Count - 1); continue;
                }
                info.Number = -2;
                foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(info.Block!))
                {
                    Count("postorder.successor_edges");
                    if (!blocks.TryGetValue(successor, out BlockInfo? successorInfo) || successorInfo.Number != 0) continue;
                    successorInfo.Number = -1; work.Add(successorInfo);
                }
            }
            pseudoEntry.Number = number;
            Count("postorder.blocks", (ulong)blockList.Count);
            return pseudoEntry;
        }
        private BlockInfo IntersectDominators(BlockInfo first, BlockInfo second)
        {
            while (!ReferenceEquals(first, second))
            {
                while (first.Number < second.Number)
                {
                    Count("dominators.intersect_steps");
                    if (first.IDom is not { } parent) return second;
                    first = parent;
                }
                while (second.Number < first.Number)
                {
                    Count("dominators.intersect_steps");
                    if (second.IDom is not { } parent) return first;
                    second = parent;
                }
            }
            return first;
        }
        private void FindDominators(BlockInfo pseudoEntry)
        {
            bool changed;
            do
            {
                changed = false; Count("dominators.iterations");
                for (int index = blockList.Count - 1; index >= 0; --index)
                {
                    BlockInfo info = blockList[index]; BlockInfo? newIDom = null;
                    foreach (BlockInfo predecessor in info.Predecessors)
                    {
                        Count("dominators.predecessor_edges");
                        if (predecessor.Number == 0)
                        {
                            predecessor.AvailableValue = LlvmPoisonValue.Get(updater.prototypeType);
                            updater.available[predecessor.Block!] = predecessor.AvailableValue;
                            predecessor.Definition = predecessor; predecessor.Number = pseudoEntry.Number++;
                            Count("dominators.unreachable_poison_definitions");
                        }
                        newIDom = newIDom is null ? predecessor : IntersectDominators(newIDom, predecessor);
                    }
                    if (newIDom is null || ReferenceEquals(newIDom, info.IDom)) continue;
                    info.IDom = newIDom; changed = true; Count("dominators.updates");
                }
            } while (changed);
        }
        private bool IsDefinitionInFrontier(BlockInfo predecessor, BlockInfo dominator)
        {
            for (; !ReferenceEquals(predecessor, dominator); predecessor = predecessor.IDom!)
            {
                Count("placement.frontier_steps");
                if (ReferenceEquals(predecessor.Definition, predecessor)) return true;
            }
            return false;
        }
        private void FindPhiPlacement()
        {
            bool changed;
            do
            {
                changed = false; Count("placement.iterations");
                for (int index = blockList.Count - 1; index >= 0; --index)
                {
                    BlockInfo info = blockList[index];
                    if (ReferenceEquals(info.Definition, info)) continue;
                    BlockInfo? newDefinition = info.IDom!.Definition;
                    foreach (BlockInfo predecessor in info.Predecessors)
                        if (IsDefinitionInFrontier(predecessor, info.IDom)) { newDefinition = info; break; }
                    if (ReferenceEquals(newDefinition, info.Definition)) continue;
                    info.Definition = newDefinition; changed = true; Count("placement.updates");
                }
            } while (changed);
        }
    }
}
