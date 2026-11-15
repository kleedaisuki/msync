// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LICM.cpp promoteLoopAccessesToScalars, modern MemorySSA path.
using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private enum StoreSafety { Safe, Unsafe, Unknown }

    private sealed partial class Work
    {
        private bool PromoteLoopAccesses(IReadOnlyList<LlvmValue> pointers, List<LlvmBasicBlock> exits,
            List<LlvmInstruction> insertions, List<LlvmMemoryAccess?> memoryInsertions,
            Dictionary<LlvmBasicBlock, LlvmBasicBlock[]> predecessorCache, bool readsOutside)
        {
            if (Trace is null) return PromoteLoopAccessesCore(pointers, exits, insertions, memoryInsertions, predecessorCache, readsOutside);
            return Trace.MeasureDetailed("optimizer.llvm-licm.promote-memory", (Work: this, pointers, exits,
                insertions, memoryInsertions, predecessorCache, readsOutside), static state => state.Work.PromoteLoopAccessesCore(
                    state.pointers, state.exits, state.insertions, state.memoryInsertions, state.predecessorCache, state.readsOutside),
                attributes: Attributes(Loop.Header));
        }

        private bool PromoteLoopAccessesCore(IReadOnlyList<LlvmValue> pointers, List<LlvmBasicBlock> exits,
            List<LlvmInstruction> insertions, List<LlvmMemoryAccess?> memoryInsertions,
            Dictionary<LlvmBasicBlock, LlvmBasicBlock[]> predecessorCache, bool readsOutside)
        {
            Count("promotion.attempts");
            LlvmValue somePointer = pointers[0];
            LlvmBasicBlock preheader = Loop.GetLoopPreheader()!;
            LlvmDataLayout layout = preheader.Parent!.Parent!.DataLayout;
            bool dereferenceable = false, storeGuaranteed = false, loadGuaranteed = false, foundLoad = false;
            StoreSafety safety = readsOutside ? StoreSafety.Unsafe : StoreSafety.Unknown;
            List<LlvmInstruction> uses = [];
            var alignment = new LlvmAlign(1);
            bool sawAtomic = false, sawNotAtomic = false;
            if (safety == StoreSafety.Unknown && Safety.AnyBlockMayThrow())
            {
                LlvmValue underlying = LlvmValueTracking.GetUnderlyingObject(somePointer, metrics: Metrics);
                if (!LlvmLicmObjectFacts.IsNotVisibleOnUnwindInLoop(underlying, Loop, Dominators, Metrics)) safety = StoreSafety.Unsafe;
            }
            LlvmType? accessType = null;
            foreach (LlvmValue pointer in pointers)
            {
                foreach (LlvmUse use in pointer.Uses)
                {
                    if (use.User is not LlvmInstruction instruction || !Loop.Contains(instruction.Parent!)) continue;
                    LlvmType type;
                    if (instruction is LlvmLoadInstruction load)
                    {
                        if (!load.IsUnordered) return Retain("promotion_ordered_load");
                        sawAtomic |= load.IsAtomic; sawNotAtomic |= !load.IsAtomic; foundLoad = true;
                        if (!loadGuaranteed) loadGuaranteed = Safety.IsGuaranteedToExecute(load, Dominators);
                        if ((!dereferenceable || load.Alignment > alignment) && SafeToExecuteUnconditionally(load, preheader.GetTerminator()))
                        { dereferenceable = true; alignment = LlvmAlign.Max(alignment, load.Alignment); }
                        type = load.Type;
                    }
                    else if (instruction is LlvmStoreInstruction store)
                    {
                        if (use.OperandNo != 1) continue;
                        if (!store.IsUnordered) return Retain("promotion_ordered_store");
                        sawAtomic |= store.IsAtomic; sawNotAtomic |= !store.IsAtomic;
                        bool guaranteed = Safety.IsGuaranteedToExecute(store, Dominators);
                        storeGuaranteed |= guaranteed;
                        if (guaranteed)
                        {
                            dereferenceable = true;
                            if (safety == StoreSafety.Unknown) safety = StoreSafety.Safe;
                            alignment = LlvmAlign.Max(alignment, store.Alignment);
                        }
                        if (safety == StoreSafety.Unknown && exits.All(exit => Dominators.Dominates(store.Parent, exit))) safety = StoreSafety.Safe;
                        if (!dereferenceable)
                            dereferenceable = LlvmValueTracking.IsDereferenceableAndAlignedPointer(store.PointerOperand,
                                store.ValueOperand.Type, store.Alignment, layout, preheader.GetTerminator(), Dominators, metrics: Metrics);
                        type = store.ValueOperand.Type;
                    }
                    else continue;
                    if (accessType is null) accessType = type;
                    else if (accessType != type) return Retain("promotion_access_types");
                    // No AA tag attachments are represented; the source merge is empty.
                    uses.Add(instruction);
                    Count("promotion.collected_accesses");
                }
            }
            if (sawAtomic && sawNotAtomic) return Retain("promotion_mixed_atomicity");
            if (sawAtomic && alignment.Value < layout.GetTypeStoreSize(accessType!).GetFixedValue()) return Retain("promotion_atomic_alignment");
            if (!dereferenceable) return Retain("promotion_not_dereferenceable_in_preheader");
            if (safety == StoreSafety.Unknown)
            {
                LlvmValue underlying = LlvmValueTracking.GetUnderlyingObject(somePointer, metrics: Metrics);
                if (LlvmLicmObjectFacts.IsWritableObject(underlying, out bool explicitOnly, Metrics) &&
                    (!explicitOnly || LlvmValueTracking.IsDereferenceableAndAlignedPointer(somePointer, accessType!,
                        new LlvmAlign(1), layout, ignoreFree: true, metrics: Metrics)) &&
                    LlvmLicmObjectFacts.IsThreadLocalObject(underlying, Loop, Dominators, Metrics)) safety = StoreSafety.Safe;
            }
            if (safety != StoreSafety.Safe && !foundLoad) return Retain("promotion_no_safe_stores_or_loads");
            Count(safety == StoreSafety.Safe ? "promotion.load_store" : "promotion.load_only");
            int logicalType = uses[0] is LlvmLoadInstruction ? uses[0].LogicalType : uses[0].GetOperandUse(0).LogicalType;
            int pointerType = PointerView(somePointer, uses);
            SourceSpan? source = uses.Select(instruction => instruction.Source).FirstOrDefault(span => span is not null);
            List<LlvmPhiInstruction> newPhis = [];
            var ssa = new LlvmSsaUpdater(newPhis, Trace, Metrics);
            var promoter = new LoopPromoter(this, somePointer, uses, ssa, exits, insertions, memoryInsertions,
                predecessorCache, alignment, sawAtomic, safety == StoreSafety.Safe, logicalType, pointerType, source);
            LlvmLoadInstruction? initialLoad = null;
            if (foundLoad || !storeGuaranteed)
            {
                initialLoad = new(accessType!, somePointer, alignment, ordering: sawAtomic ? LlvmAtomicOrdering.Unordered : LlvmAtomicOrdering.NotAtomic)
                { Source = source, LogicalType = logicalType };
                initialLoad.GetOperandUse(0).LogicalType = pointerType;
                initialLoad.SetName(AppendName(somePointer.NameBytes.Span, ".promoted"u8));
                preheader.InsertBefore(initialLoad, preheader.GetTerminator());
                var access = (LlvmMemoryUse)Updater.CreateMemoryAccessInBB(initialLoad, null, preheader, LlvmMemorySsaInsertionPlace.End)!;
                Updater.InsertUse(access, true);
                ssa.AddAvailableValue(preheader, initialLoad);
                Count("promotion.preheader_loads");
            }
            else ssa.AddAvailableValue(preheader, LlvmPoisonValue.Get(accessType!));
            promoter.Run(uses);
            if (initialLoad is not null && initialLoad.UseEmpty) EraseInstruction(initialLoad);
            return true;
        }

        private static int PointerView(LlvmValue pointer, IReadOnlyList<LlvmInstruction> uses)
        {
            foreach (LlvmInstruction instruction in uses)
            {
                int operand = instruction is LlvmLoadInstruction ? 0 : 1;
                if (instruction.GetOperand(operand) == pointer) return instruction.GetOperandUse(operand).LogicalType;
            }
            return pointer.LogicalType;
        }
    }
}
