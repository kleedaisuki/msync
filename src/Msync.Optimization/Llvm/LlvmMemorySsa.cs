// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: MemorySSA.h and
// MemorySSA.cpp function-local construction, lookups and access dominance.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsa : IDisposable
{
    private readonly Dictionary<LlvmValue, LlvmMemoryAccess> valueToAccess = [];
    private readonly Dictionary<LlvmBasicBlock, LlvmMemoryAccessList> perBlockAccesses = [];
    private readonly Dictionary<LlvmBasicBlock, LlvmMemoryAccessList> perBlockDefs = [];
    private readonly Dictionary<LlvmMemoryAccess, ulong> numbering = [];
    private readonly HashSet<LlvmBasicBlock> numberingValid = [];
    private uint nextId;
    internal readonly CompilationTrace? Trace;
    internal readonly Dictionary<string, ulong>? Metrics;
    public LlvmFunction Function { get; }
    public LlvmAAResults AA { get; }
    public LlvmDominatorTree DominatorTree { get; }
    public LlvmMemoryDef LiveOnEntryDef { get; }

    public LlvmMemorySsa(LlvmFunction function, LlvmAAResults aa, LlvmDominatorTree tree,
        CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(ReferenceEquals(function, tree.Function) && !tree.IsPostDominator && !function.IsDeclaration);
        Function = function; AA = aa; DominatorTree = tree; Trace = trace?.Detail;
        Metrics = metrics ?? (Trace is null ? null : []);
        LiveOnEntryDef = new LlvmMemoryDef(function.BasicBlocks[0], null, nextId++);
        if (Trace is null) BuildMemorySsa();
        else Trace.MeasureDetailed("optimizer.analysis.llvm-memoryssa", this, static graph =>
            { graph.BuildMemorySsa(); return graph; },
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name }, metrics: static graph => graph.Metrics!);
    }
    public LlvmMemoryUseOrDef? GetMemoryAccess(LlvmInstruction instruction) => valueToAccess.GetValueOrDefault(instruction) as LlvmMemoryUseOrDef;
    public LlvmMemoryPhi? GetMemoryAccess(LlvmBasicBlock block) => valueToAccess.GetValueOrDefault(block) as LlvmMemoryPhi;
    public LlvmMemoryAccessList? GetBlockAccesses(LlvmBasicBlock block) => perBlockAccesses.GetValueOrDefault(block);
    public LlvmMemoryAccessList? GetBlockDefs(LlvmBasicBlock block) => perBlockDefs.GetValueOrDefault(block);
    public bool IsLiveOnEntryDef(LlvmMemoryAccess access) => ReferenceEquals(access, LiveOnEntryDef);
    private LlvmMemoryAccessList Accesses(LlvmBasicBlock block)
    {
        if (!perBlockAccesses.TryGetValue(block, out var accesses)) perBlockAccesses.Add(block, accesses = new(false));
        return accesses;
    }
    private LlvmMemoryAccessList Definitions(LlvmBasicBlock block)
    {
        if (!perBlockDefs.TryGetValue(block, out var accesses)) perBlockDefs.Add(block, accesses = new(true));
        return accesses;
    }
    private void Count(string name, ulong value = 1)
    {
        if (Metrics is null) return;
        name = "memoryssa." + name;
        Metrics[name] = Metrics.GetValueOrDefault(name) + value;
    }
    public bool LocallyDominates(LlvmMemoryAccess dominator, LlvmMemoryAccess dominatee)
    {
        Debug.Assert(ReferenceEquals(dominator.Block, dominatee.Block));
        if (ReferenceEquals(dominator, dominatee)) return true;
        if (IsLiveOnEntryDef(dominatee)) return false;
        if (IsLiveOnEntryDef(dominator)) return true;
        if (!numberingValid.Contains(dominator.Block)) RenumberBlock(dominator.Block);
        return numbering[dominator] < numbering[dominatee];
    }
    public bool Dominates(LlvmMemoryAccess dominator, LlvmMemoryAccess dominatee)
    {
        if (ReferenceEquals(dominator, dominatee)) return true;
        if (IsLiveOnEntryDef(dominatee)) return false;
        return ReferenceEquals(dominator.Block, dominatee.Block) ? LocallyDominates(dominator, dominatee)
            : DominatorTree.Dominates(dominator.Block, dominatee.Block);
    }
    public bool Dominates(LlvmMemoryAccess dominator, LlvmUse use)
    {
        if (use.User is not LlvmMemoryPhi phi) return Dominates(dominator, (LlvmMemoryAccess)use.User);
        LlvmBasicBlock incoming = phi.GetIncomingBlock(use.OperandNo);
        return !ReferenceEquals(incoming, dominator.Block) ? DominatorTree.Dominates(dominator.Block, incoming)
            : LocallyDominates(dominator, (LlvmMemoryAccess)use.Value!);
    }
    private void RenumberBlock(LlvmBasicBlock block)
    {
        if (Trace is null) { RenumberBlockCore(block); return; }
        Trace.Run("optimizer.memoryssa.local-order", (Graph: this, Block: block),
            static state => state.Graph.RenumberBlockCore(state.Block),
            new Dictionary<string, string> { ["function.name"] = Function.Name, ["block.name"] = block.Name });
    }
    private void RenumberBlockCore(LlvmBasicBlock block)
    {
        ulong number = 0;
        foreach (LlvmMemoryAccess access in GetBlockAccesses(block)!) numbering[access] = ++number;
        numberingValid.Add(block); Count("local_order.renumbered", number);
    }
    public void Dispose()
    {
        foreach (LlvmMemoryAccessList list in perBlockAccesses.Values)
            foreach (LlvmMemoryAccess access in list) access.DropAllReferences();
        LiveOnEntryDef.DropAllReferences();
    }
}
