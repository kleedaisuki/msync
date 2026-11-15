// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MemorySSAUpdater.h
// and lib/Analysis/MemorySSAUpdater.cpp. Managed access handles replace LLVM VH.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater(LlvmMemorySsa memorySsa)
{
    public LlvmMemorySsa MemorySsa { get; } = memorySsa;
    private readonly List<LlvmMemoryPhi> insertedPhis = [];
    private readonly HashSet<LlvmBasicBlock> visitedBlocks = [];
    private readonly HashSet<LlvmMemoryPhi> nonOptPhis = [];
    private void Count(string key, ulong amount = 1)
    {
        if (MemorySsa.Metrics is not { } metrics) return;
        key = "memoryssa.update." + key;
        metrics[key] = metrics.GetValueOrDefault(key) + amount;
    }
    private static LlvmMemoryAccess? Track(LlvmMemoryAccess? access) => LlvmMemoryAccess.Track(access);
    private static LlvmMemoryPhi? WeakPhi(LlvmMemoryPhi phi) => LlvmMemoryAccess.Weak(phi) as LlvmMemoryPhi;
    private static IEnumerable<LlvmBasicBlock> Successors(LlvmBasicBlock block) => LlvmDominatorTree.Successors(block);
    private static LlvmBasicBlock[] Predecessors(LlvmBasicBlock block) => LlvmDominatorTree.Predecessors(block).ToArray();
    private void RunUpdate<T>(string phase, LlvmMemoryAccess? access, T state, Action<LlvmMemorySsaUpdater, T> action)
    {
        if (MemorySsa.Trace is not { } trace) { action(this, state); return; }
        var attributes = new Dictionary<string, string> { ["function.name"] = MemorySsa.Function.Name };
        if (access is not null)
        { attributes["block.name"] = access.Block.Name; attributes["access.kind"] = access.GetType().Name; }
        if (access is LlvmMemoryUseOrDef { MemoryInstruction.Source: { } source })
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        trace.MeasureDetailed("optimizer.memoryssa.update." + phase, (Self: this, State: state, Action: action),
            static work => { work.Action(work.Self, work.State); return work.Self; }, attributes: attributes,
            metrics: static updater => updater.MemorySsa.Metrics!);
    }
}
