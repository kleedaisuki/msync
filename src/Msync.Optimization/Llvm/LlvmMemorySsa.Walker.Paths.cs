// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: MemorySSA.cpp ClobberWalker,
// MemorySSA.h upward_defs_iterator / def_chain_iterator.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmMemorySsaClobberWalker(LlvmMemorySsa memorySsa)
{
    private struct DefPath(LlvmMemoryLocation location, LlvmMemoryAccess first,
        LlvmMemoryAccess last, bool crossIteration, int? previous)
    {
        internal LlvmMemoryLocation Location = location;
        internal LlvmMemoryAccess First = first;
        internal LlvmMemoryAccess Last = last;
        internal readonly bool MayBeCrossIteration = crossIteration;
        internal readonly int? Previous = previous;
    }
    private readonly record struct UpwardDef(LlvmMemoryAccess Access, LlvmMemoryLocation Location, bool MayBeCrossIteration);
    private readonly record struct WalkResult(LlvmMemoryAccess Access, bool IsKnownClobber);
    private readonly record struct TerminatedPath(LlvmMemoryAccess Clobber, int LastNode);
    private readonly record struct OptimizationResult(TerminatedPath PrimaryClobber, List<TerminatedPath> OtherClobbers);
    private readonly List<DefPath> paths = new(32);
    private readonly HashSet<UpwardDef> visitedPhis = [];
    private LlvmBatchAAResults aa = null!;
    private LlvmUpwardsMemoryQuery query = null!;
    private uint upwardWalkLimit;
    private Dictionary<string, ulong>? metrics;

    private LlvmMemoryAccess GetWalkTarget(LlvmMemoryPhi from)
    {
        for (var node = memorySsa.DominatorTree.GetNode(from.Block)!.IDom; node is not null; node = node.IDom)
            if (memorySsa.GetBlockDefs(node.Block!) is { } definitions)
                return definitions.Last!;
        return memorySsa.LiveOnEntryDef;
    }

    private WalkResult WalkToPhiOrClobber(ref DefPath description, LlvmMemoryAccess? stopAt = null,
        LlvmMemoryAccess? skipStopAt = null)
    {
        bool limitAlreadyReached = upwardWalkLimit == 0;
        if (limitAlreadyReached) { upwardWalkLimit = 1; Count("memoryssa.walker.budget_zero_phi_advance"); }
        for (LlvmMemoryAccess? current = description.Last; current is not null; current =
            current is LlvmMemoryUseOrDef useOrDef ? useOrDef.DefiningAccess : null)
        {
            Count("memoryssa.walker.def_chain_steps");
            description.Last = current;
            if (ReferenceEquals(current, stopAt) || ReferenceEquals(current, skipStopAt)) return new(current, false);
            if (current is not LlvmMemoryDef definition) continue;
            if (memorySsa.IsLiveOnEntryDef(definition)) return new(definition, true);
            if (--upwardWalkLimit == 0)
            { Count("memoryssa.walker.budget_exhausted"); return new(current, true); }
            bool previousCrossIteration = aa.MayBeCrossIteration;
            aa.MayBeCrossIteration = description.MayBeCrossIteration;
            bool clobbers;
            try { clobbers = InstructionClobbersQuery(definition, description.Location); }
            finally { aa.MayBeCrossIteration = previousCrossIteration; }
            if (clobbers) { Count("memoryssa.walker.aa_clobber"); return new(definition, true); }
        }
        if (limitAlreadyReached) upwardWalkLimit = 0;
        return new(description.Last, false);
    }

    private void AddSearches(LlvmMemoryPhi phi, List<int> paused, int priorNode, bool crossIteration)
    {
        LlvmMemoryLocation location = paths[priorNode].Location;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            LlvmMemoryLocation translated = location;
            bool mayCross = crossIteration;
            if (location.Ptr is not null)
            {
                var translator = new LlvmPhiTransAddr(location.Ptr, memorySsa.Function.Parent!.DataLayout, metrics: metrics);
                LlvmValue? pointer = translator.TranslateValue(phi.Block, phi.GetIncomingBlock(index), memorySsa.DominatorTree, true);
                if (pointer is not null && !ReferenceEquals(pointer, translated.Ptr))
                { translated = translated.WithNewPtr(pointer); Count("memoryssa.walker.phi_pointer_translated"); }
                if (!IsGuaranteedLoopInvariant(translated.Ptr!))
                { translated = translated.WithNewSize(LlvmLocationSize.BeforeOrAfterPointer()); Count("memoryssa.walker.phi_size_widened"); }
            }
            else mayCross = true;
            paused.Add(paths.Count);
            paths.Add(new(translated, phi.GetIncomingValue(index), phi.GetIncomingValue(index), mayCross, priorNode));
            Count("memoryssa.walker.phi_paths");
        }
    }

    private bool IsGuaranteedLoopInvariant(LlvmValue pointer)
    {
        bool BaseInvariant(LlvmValue value) => LlvmValueTracking.StripPointerCasts(value, metrics) is not LlvmInstruction or LlvmAllocaInstruction;
        pointer = LlvmValueTracking.StripPointerCasts(pointer, metrics);
        if (pointer is LlvmInstruction instruction && ReferenceEquals(instruction.Parent, memorySsa.Function.BasicBlocks[0])) return true;
        if (pointer is LlvmGetElementPtrInstruction gep) return BaseInvariant(gep.PointerOperand) && gep.HasAllConstantIndices;
        return BaseInvariant(pointer);
    }

    private bool InstructionClobbersQuery(LlvmMemoryDef definition, LlvmMemoryLocation location)
    {
        Count("memoryssa.walker.clobber_candidates");
        LlvmInstruction instruction = definition.MemoryInstruction!;
        if (query.Instruction is LlvmCallBase call)
        {
            Count("memoryssa.walker.aa_queries");
            return (aa.GetModRefInfo(instruction, call) & LlvmModRefInfo.Mod) != 0;
        }
        if (instruction is LlvmLoadInstruction defLoad && query.Instruction is LlvmLoadInstruction useLoad)
        {
            Count("memoryssa.walker.load_order_queries");
            if (useLoad.IsVolatile && defLoad.IsVolatile) return true;
            return useLoad.Ordering == LlvmAtomicOrdering.SequentiallyConsistent ||
                defLoad.Ordering is LlvmAtomicOrdering.Acquire or LlvmAtomicOrdering.AcquireRelease or LlvmAtomicOrdering.SequentiallyConsistent;
        }
        Count("memoryssa.walker.aa_queries");
        return (aa.GetModRefInfo(instruction, location) & LlvmModRefInfo.Mod) != 0;
    }

    private void Count(string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
