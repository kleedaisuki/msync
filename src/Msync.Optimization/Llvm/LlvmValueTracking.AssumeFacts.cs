// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/Loads.cpp
// isDereferenceableAndAlignedPointerViaAssumption; ValueTracking.cpp
// willNotFreeBetween, hasNoFreeInRange and AC noundef knowledge consumer.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    private static bool IsDereferenceableAndAlignedViaAssumption(LlvmValue pointer, LlvmAlign alignment, LlvmAPInt size,
        LlvmDataLayout layout, LlvmInstruction? context, LlvmDominatorTree? tree, bool ignoreFree,
        Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null)
    {
        if (context is null || assumptions is null) return false;
        bool canBeFreed = CanBeFreed(pointer) && !ignoreFree;
        bool aligned = GetPointerAlignment(pointer, layout, metrics).Value >= alignment.Value;
        bool dereferenceable = false;
        foreach (var entry in assumptions.AssumptionsFor(pointer))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption || entry.Index == LlvmAssumptionCache.ExprResultIdx) continue;
            Count(metrics, "value_tracking.deref.assume_entries");
            var bundle = assumption.GetOperandBundleAt((int)entry.Index);
            if (!LlvmAssumeBundleQueries.TryGetRetainedAlignmentOrDereferenceable(bundle, out var value, out bool isAlignment, out ulong argument) ||
                !ReferenceEquals(pointer, value) || !IsValidAssumeForContext(assumption, context, tree, false, metrics)) continue;
            if (isAlignment)
            { aligned |= argument >= alignment.Value; Count(metrics, "value_tracking.deref.assume_alignment"); }
            else if (!dereferenceable && (!canBeFreed || WillNotFreeBetween(assumption, context, tree, metrics)) && argument >= size.GetZExtValue())
            { dereferenceable = true; Count(metrics, "value_tracking.deref.assume_bytes"); }
            if (aligned && dereferenceable) return true;
        }
        return false;
    }

    // getKnowledgeValidInContext uses the context overload's default false,
    // rather than a SimplifyQuery allow-ephemeral override.
    internal static bool HasNoUndefKnowledge(LlvmValue value, LlvmInstruction context, LlvmDominatorTree tree,
        Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null)
    {
        if (assumptions is null) return false;
        foreach (var entry in assumptions.AssumptionsFor(value))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption || entry.Index == LlvmAssumptionCache.ExprResultIdx) continue;
            var bundle = assumption.GetOperandBundleAt((int)entry.Index);
            if (bundle.Tag != "noundef" || !ReferenceEquals(bundle.GetInput(0), value)) continue;
            Count(metrics, "value_tracking.defined.assume_noundef_entries");
            if (IsValidAssumeForContext(assumption, context, tree, false, metrics)) return true;
        }
        return false;
    }

    public static bool CanBeFreed(LlvmValue value) => value is not (LlvmConstant or LlvmAllocaInstruction or LlvmHeapAllocInst);

    public static bool WillNotFreeBetween(LlvmInstruction assumption, LlvmInstruction context,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.no_free_between.queries");
        var contextBlock = context.Parent!; var assumptionBlock = assumption.Parent!;
        uint checkedInstructions = 0;
        if (ReferenceEquals(contextBlock, assumptionBlock))
        {
            if (!ReferenceEquals(assumption, context) && !assumption.ComesBefore(context)) return false;
            return HasNoFreeInRange(assumption, context, ref checkedInstructions, metrics);
        }
        if (tree is not null && !tree.Dominates(assumption, context)) return false;
        if (!HasNoFreeInRange(contextBlock.FirstInstruction, context, ref checkedInstructions, metrics)) return false;
        var work = new Stack<LlvmBasicBlock>();
        foreach (var predecessor in LlvmDominatorTree.Predecessors(contextBlock)) work.Push(predecessor);
        if (work.Count == 0) return false;
        var visited = new HashSet<LlvmBasicBlock>(ReferenceEqualityComparer.Instance);
        while (work.TryPop(out var block))
        {
            if (!visited.Add(block)) continue;
            Count(metrics, "value_tracking.no_free_between.blocks");
            if (ReferenceEquals(block, assumptionBlock))
            {
                if (!HasNoFreeInRange(assumption, null, ref checkedInstructions, metrics)) return false;
                continue;
            }
            using var predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
            if (!predecessors.MoveNext()) return false;
            if (!HasNoFreeInRange(ReferenceEquals(block, contextBlock) ? context : block.FirstInstruction,
                null, ref checkedInstructions, metrics)) return false;
            do { work.Push(predecessors.Current); } while (predecessors.MoveNext());
        }
        return true;
    }

    private static bool HasNoFreeInRange(LlvmInstruction? begin, LlvmInstruction? end, ref uint checkedInstructions,
        Dictionary<string, ulong>? metrics)
    {
        for (var instruction = begin; !ReferenceEquals(instruction, end); instruction = instruction!.Next)
        {
            if (checkedInstructions++ > 32) { Count(metrics, "value_tracking.no_free_between.scan_limit"); return false; }
            Count(metrics, "value_tracking.no_free_between.instructions");
            if (instruction is LlvmCallBase call)
            {
                if (call.CalledFunction?.IntrinsicDescriptor is not { NoFree: true })
                { Count(metrics, "value_tracking.no_free_between.call_may_free"); return false; }
            }
            else if (MaySynchronizeForAssumption(instruction!))
            { Count(metrics, "value_tracking.no_free_between.synchronization"); return false; }
        }
        return true;
    }

    private static bool MaySynchronizeForAssumption(LlvmInstruction instruction) => instruction switch
    {
        LlvmLoadInstruction load => load.Ordering > LlvmAtomicOrdering.Monotonic,
        LlvmStoreInstruction store => store.Ordering > LlvmAtomicOrdering.Monotonic,
        _ when instruction.Opcode is LlvmOpcode.Fence or LlvmOpcode.AtomicRMW or LlvmOpcode.AtomicCmpXchg =>
            throw new NotSupportedException("Synchronization scope/RMW/CmpXchg ordering state is not represented for this source query."),
        _ => false
    };
}
