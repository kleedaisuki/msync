// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LICM.cpp foreachMemoryAccess / isPotentiallyPromotable /
// collectPromotionCandidates (represented empty AA-metadata state).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private readonly record struct PromotionCandidate(IReadOnlyList<LlvmValue> Pointers, bool HasReadsOutsideSet);

    private sealed partial class Work
    {
        private IEnumerable<LlvmInstruction> LoopMemoryInstructions()
        {
            foreach (LlvmBasicBlock block in Loop.Blocks)
            {
                if (Memory.GetBlockAccesses(block) is not { } accesses) continue;
                foreach (LlvmMemoryAccess access in accesses)
                    if (access is LlvmMemoryUseOrDef memory) yield return memory.MemoryInstruction!;
            }
        }

        private bool PotentiallyPromotable(LlvmInstruction instruction)
        {
            return instruction switch
            {
                LlvmStoreInstruction store => (byte)store.Ordering <= (byte)LlvmAtomicOrdering.Monotonic &&
                    store.PointerOperand is not LlvmConstantData && Loop.IsLoopInvariant(store.PointerOperand),
                LlvmLoadInstruction load => (byte)load.Ordering <= (byte)LlvmAtomicOrdering.Monotonic &&
                    load.PointerOperand is not LlvmConstantData && Loop.IsLoopInvariant(load.PointerOperand),
                _ => false
            };
        }

        private List<PromotionCandidate> CollectPromotionCandidates()
        {
            if (Trace is null) return CollectPromotionCandidatesCore();
            return Trace.MeasureDetailed("optimizer.llvm-licm.promotion-candidates", this,
                static state => state.CollectPromotionCandidatesCore(), attributes: Attributes(Loop.Header));
        }

        private List<PromotionCandidate> CollectPromotionCandidatesCore()
        {
            var batch = new LlvmBatchAAResults(Alias);
            var tracker = new LlvmAliasSetTracker(batch, Metrics);
            HashSet<LlvmInstruction> attempting = [];
            foreach (LlvmInstruction instruction in LoopMemoryInstructions())
            {
                Count("promotion.candidate_accesses");
                if (!PotentiallyPromotable(instruction)) continue;
                attempting.Add(instruction);
                // The current IR has no AA scope/noalias metadata. Source's
                // conditional-store tag stripping therefore has no tags to strip.
                tracker.Add(instruction);
            }
            List<(LlvmAliasSet Set, bool Reads)> sets = [];
            foreach (LlvmAliasSet set in tracker)
                if (!set.IsForwardingAliasSet && set.IsMod && set.IsMustAlias) sets.Add((set, false));
            if (sets.Count == 0) return [];
            foreach (LlvmInstruction instruction in LoopMemoryInstructions())
            {
                if (attempting.Contains(instruction)) continue;
                for (int index = 0; index < sets.Count;)
                {
                    var (set, reads) = sets[index];
                    LlvmModRefInfo effect = set.AliasesUnknownInst(instruction, batch);
                    Count("promotion.outside_access_queries");
                    if (effect.IsModSet() || effect.IsRefSet() && !set.IsRef)
                    { sets.RemoveAt(index); Count("promotion.sets_invalidated"); continue; }
                    if (effect.IsRefSet()) reads = true;
                    sets[index] = (set, reads);
                    ++index;
                }
            }
            List<PromotionCandidate> result = [];
            foreach (var (set, reads) in sets) result.Add(new(set.GetPointers(), reads));
            Count("promotion.candidate_sets", (ulong)result.Count);
            return result;
        }
    }
}
