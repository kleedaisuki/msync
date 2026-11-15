// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp presplitLoadsAndStores.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private sealed class SplitOffsets(Slice slice)
    {
        internal readonly Slice Slice = slice;
        internal readonly List<ulong> Splits = [];
    }

    internal sealed partial class State
    {
        internal bool PresplitLoadsAndStores(LlvmAllocaInstruction allocation, AllocaSlices slices)
        {
            if (Trace is not { } trace) return PresplitLoadsAndStoresCore(allocation, slices);
            return trace.MeasureDetailed("optimizer.llvm-sroa.presplit", (State: this, Alloca: allocation, Slices: slices),
                static work => work.State.PresplitLoadsAndStoresCore(work.Alloca, work.Slices),
                attributes: new Dictionary<string, string>
                {
                    ["function.name"] = allocation.Parent!.Parent!.Name,
                    ["alloca.name"] = allocation.Name
                });
        }

        private bool PresplitLoadsAndStoresCore(LlvmAllocaInstruction allocation, AllocaSlices slices)
        {
            List<LlvmLoadInstruction> loads = [];
            List<LlvmStoreInstruction> stores = [];
            Dictionary<LlvmInstruction, SplitOffsets> offsets = [];
            HashSet<LlvmLoadInstruction> unsplittable = [];
            foreach (Partition partition in slices.Partitions())
            {
                Count("presplit.partitions");
                Dictionary<LlvmInstruction, SliceGroup> map = [];
                foreach (Slice slice in partition)
                {
                    var instruction = (LlvmInstruction)slice.Use!.User;
                    if (!map.TryGetValue(instruction, out var uses))
                    {
                        map.Add(instruction, new(slice));
                        Count("presplit.slice_groups");
                        continue;
                    }
                    if (ReferenceEquals(slice, uses.First)) continue;
                    var overflow = uses.Overflow;
                    if (overflow is null)
                    {
                        overflow = [];
                        map[instruction] = new(uses.First, overflow);
                        Count("presplit.slice_group_overflows");
                    }
                    overflow.Add(slice);
                }
                foreach (Slice slice in partition)
                {
                    var instruction = (LlvmInstruction)slice.Use!.User;
                    Count("presplit.slices");
                    Slice? overlap = FindOverlappingCopySlice(slice, map);
                    if (!slice.IsSplittable || slice.EndOffset <= partition.EndOffset && overlap is null)
                    {
                        if (instruction is LlvmLoadInstruction load) unsplittable.Add(load);
                        else if (instruction is LlvmStoreInstruction { ValueOperand: LlvmLoadInstruction stored }) unsplittable.Add(stored);
                        Count("presplit.unsplittable_or_contained");
                        continue;
                    }
                    Debug.Assert(partition.EndOffset > slice.BeginOffset);
                    if (instruction is LlvmLoadInstruction candidateLoad)
                    {
                        Debug.Assert(!candidateLoad.IsVolatile);
                        if (candidateLoad.Users.Any(user => user is not LlvmStoreInstruction { IsSimple: true }))
                        { unsplittable.Add(candidateLoad); Count("presplit.load_not_simply_stored"); continue; }
                        loads.Add(candidateLoad);
                    }
                    else if (instruction is LlvmStoreInstruction candidateStore)
                    {
                        if (!ReferenceEquals(slice.Use, candidateStore.GetOperandUse(1))) continue;
                        if (candidateStore.ValueOperand is not LlvmLoadInstruction { IsSimple: true }) continue;
                        Debug.Assert(!candidateStore.IsVolatile);
                        stores.Add(candidateStore);
                    }
                    else continue;
                    Debug.Assert(!offsets.ContainsKey(instruction));
                    var entry = new SplitOffsets(slice);
                    offsets.Add(instruction, entry);
                    if (overlap is not null) AppendCopySplits(entry.Splits, slice, overlap);
                    else entry.Splits.Add(partition.EndOffset - slice.BeginOffset);
                    Count("presplit.candidates");
                    Count("presplit.split_points", (ulong)entry.Splits.Count);
                }
            }
            stores.RemoveAll(store => RemoveMismatchedStore(store, offsets, unsplittable));
            stores.RemoveAll(store => unsplittable.Contains((LlvmLoadInstruction)store.ValueOperand));
            loads.RemoveAll(unsplittable.Contains);
            if (loads.Count == 0 && stores.Count == 0) return false;
            LlvmTransformBuilder builder = new(allocation, Metrics);
            builder.DisableGepCanonicalization();
            List<Slice> newSlices = [];
            HashSet<LlvmAllocaInstruction> resplit = [];
            Dictionary<LlvmLoadInstruction, List<LlvmLoadInstruction>> splitLoads = [];
            SplitLoads(allocation, loads, stores.Count != 0, offsets, builder, newSlices, resplit, splitLoads);
            SplitStores(allocation, stores, offsets, builder, newSlices, resplit, splitLoads);
            slices.Slices.RemoveAll(slice => slice.IsDead);
            slices.InsertSlices(newSlices);
            PromotableAllocas.ExceptWith(resplit);
            Count("presplit.allocas_changed");
            Count("presplit.new_slices", (ulong)newSlices.Count);
            return true;
        }

        private static void AppendCopySplits(List<ulong> splits, Slice slice, Slice overlap)
        {
            ulong overlapSize = Math.Min(slice.EndOffset, overlap.EndOffset) - Math.Max(slice.BeginOffset, overlap.BeginOffset);
            ulong size = slice.EndOffset - slice.BeginOffset;
            ulong outside = size - overlapSize;
            if (overlapSize < outside)
            { splits.Add(overlapSize); splits.Add(size - overlapSize); return; }
            if (overlapSize == outside) { splits.Add(overlapSize); return; }
            for (ulong split = outside; split <= size / 2; split += outside)
            {
                splits.Add(split);
                if (split != size - split) splits.Add(size - split);
            }
            splits.Sort();
        }

        private bool RemoveMismatchedStore(LlvmStoreInstruction store, IReadOnlyDictionary<LlvmInstruction, SplitOffsets> offsets,
            HashSet<LlvmLoadInstruction> unsplittable)
        {
            var load = (LlvmLoadInstruction)store.ValueOperand;
            if (unsplittable.Contains(load)) return true;
            if (!offsets.TryGetValue(load, out var loadOffsets)) return false;
            if (loadOffsets.Splits.SequenceEqual(offsets[store].Splits)) return false;
            unsplittable.Add(load);
            Count("presplit.mismatched_relative_splits");
            return true;
        }

        private void SplitLoads(LlvmAllocaInstruction allocation, IReadOnlyList<LlvmLoadInstruction> loads, bool haveStores,
            IReadOnlyDictionary<LlvmInstruction, SplitOffsets> offsets, LlvmTransformBuilder builder,
            List<Slice> newSlices, HashSet<LlvmAllocaInstruction> resplit,
            Dictionary<LlvmLoadInstruction, List<LlvmLoadInstruction>> splitLoadsMap)
        {
            foreach (LlvmLoadInstruction load in loads)
            {
                var entry = offsets[load];
                uint size = unchecked((uint)(entry.Slice.EndOffset - entry.Slice.BeginOffset));
                Debug.Assert(load.Type.GetIntegerBitWidth() % 8 == 0 && load.Type.GetIntegerBitWidth() / 8 >= size);
                Debug.Assert(entry.Slice.BeginOffset + size > entry.Slice.BeginOffset);
                var pointer = (LlvmInstruction)load.PointerOperand;
                builder.SetInsertPoint(load);
                SliceSplitter splitter = new(builder, DL, entry.Splits, size);
                List<LlvmLoadInstruction> splitLoads = [];
                foreach (SplitPart part in splitter.Parts())
                {
                    var split = splitter.CreateLoad(pointer, part, load);
                    splitLoads.Add(split);
                    newSlices.Add(SliceSplitter.CreateSlice(split, entry.Slice.BeginOffset, part));
                    Count("presplit.loads_created");
                }
                bool deferred = false;
                foreach (LlvmUser user in load.Users)
                {
                    var store = (LlvmStoreInstruction)user;
                    if (haveStores && offsets.ContainsKey(store))
                    { deferred = true; Count("presplit.stores_deferred"); continue; }
                    builder.SetInsertPoint(store);
                    foreach (SplitPart part in splitter.Parts())
                    { splitter.CreateStore(splitLoads[part.Index], store.PointerOperand, part, store); Count("presplit.stores_created"); }
                    RequeueSplitPointer(store.PointerOperand, resplit);
                    AddDead(store);
                }
                if (deferred) splitLoadsMap.Add(load, splitLoads);
                AddDead(load);
                entry.Slice.Kill();
                Count("presplit.loads_split");
            }
        }

        private void SplitStores(LlvmAllocaInstruction allocation, IReadOnlyList<LlvmStoreInstruction> stores,
            IReadOnlyDictionary<LlvmInstruction, SplitOffsets> offsets, LlvmTransformBuilder builder,
            List<Slice> newSlices, HashSet<LlvmAllocaInstruction> resplit,
            IReadOnlyDictionary<LlvmLoadInstruction, List<LlvmLoadInstruction>> splitLoadsMap)
        {
            foreach (LlvmStoreInstruction store in stores)
            {
                var load = (LlvmLoadInstruction)store.ValueOperand;
                uint size = load.Type.GetIntegerBitWidth() / 8;
                var entry = offsets[store];
                Debug.Assert(size > 0 && load.Type.GetIntegerBitWidth() % 8 == 0);
                Debug.Assert(size == entry.Slice.EndOffset - entry.Slice.BeginOffset);
                Debug.Assert(entry.Slice.BeginOffset + size > entry.Slice.BeginOffset);
                LlvmValue loadPointer = load.PointerOperand;
                var storePointer = (LlvmInstruction)store.PointerOperand;
                splitLoadsMap.TryGetValue(load, out var splitLoads);
                Debug.Assert(splitLoads is null || splitLoads.Count == entry.Splits.Count + 1);
                SliceSplitter splitter = new(builder, DL, entry.Splits, size);
                foreach (SplitPart part in splitter.Parts())
                {
                    LlvmLoadInstruction split;
                    if (splitLoads is not null) split = splitLoads[part.Index];
                    else { builder.SetInsertPoint(load); split = splitter.CreateLoad(loadPointer, part, load); Count("presplit.loads_created"); }
                    builder.SetInsertPoint(store);
                    var splitStore = splitter.CreateStore(split, storePointer, part, store);
                    newSlices.Add(SliceSplitter.CreateSlice(splitStore, entry.Slice.BeginOffset, part));
                    Count("presplit.stores_created");
                }
                if (splitLoads is null)
                {
                    Debug.Assert(!ReferenceEquals(LlvmValueTracking.StripInBoundsOffsets(loadPointer), allocation));
                    RequeueSplitPointer(loadPointer, resplit);
                }
                if (load.HasOneUse) AddDead(load);
                AddDead(store);
                entry.Slice.Kill();
                Count("presplit.stores_split");
            }
        }

        private void RequeueSplitPointer(LlvmValue pointer, HashSet<LlvmAllocaInstruction> resplit)
        {
            if (pointer is LlvmAllocaInstruction direct)
            { resplit.Add(direct); Worklist.Add(direct); Count("presplit.direct_allocas_requeued"); }
            else if (LlvmValueTracking.StripInBoundsOffsets(pointer) is LlvmAllocaInstruction underlying)
            { Worklist.Add(underlying); Count("presplit.derived_allocas_requeued"); }
        }
    }
}
