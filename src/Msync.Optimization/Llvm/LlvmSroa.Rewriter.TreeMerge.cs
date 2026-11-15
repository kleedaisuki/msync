// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SROA.cpp rewriteTreeStructuredMerge.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class AllocaSliceRewriter
    {
        private readonly record struct TreeStore(LlvmStoreInstruction Store, ulong Begin, ulong End, LlvmValue Value);
        private readonly record struct TreeLoad(LlvmLoadInstruction Load, ulong Begin, ulong End);
        private readonly record struct TreeAccess(LlvmInstruction Instruction, ulong Begin, ulong End, bool IsStore);

        internal IReadOnlyList<LlvmValue>? RewriteTreeStructuredMerge(Partition partition)
        {
            Count("rewriter.tree.attempts");
            if (partition.SplitSliceTails.Count != 0) return RejectTree("split_tails");
            List<TreeStore> stores = [];
            List<TreeLoad> loads = [];
            LlvmLoadInstruction? fullLoad = null;
            LlvmStoreInstruction? initialStore = null;
            LlvmType allocatedElement = allocaType is LlvmFixedVectorType vector ? vector.ElementType : LlvmType.GetInt8Ty(newAlloca.Context);
            ulong allocatedBits = layout.GetTypeSizeInBits(allocatedElement).GetFixedValue();
            foreach (var slice in partition)
            {
                var instruction = (LlvmInstruction)slice.Use!.User;
                bool full = slice.BeginOffset == allocaBegin && slice.EndOffset == allocaEnd;
                if (instruction is LlvmLoadInstruction load)
                {
                    if (!load.IsSimple || !ValidTreeType(load.Type)) return RejectTree("load_shape");
                    if (full)
                    {
                        if (fullLoad is not null) return RejectTree("multiple_full_loads");
                        fullLoad = load;
                    }
                    else loads.Add(new(load, slice.BeginOffset, slice.EndOffset));
                }
                else if (instruction is LlvmStoreInstruction store)
                {
                    if (!store.IsSimple || !ValidTreeType(store.ValueOperand.Type)) return RejectTree("store_shape");
                    var storedVector = (LlvmFixedVectorType)store.ValueOperand.Type;
                    ulong elementBits = layout.GetTypeSizeInBits(storedVector.ElementType).GetFixedValue();
                    if (storedVector.NumElements * elementBits % allocatedBits != 0) return RejectTree("element_size");
                    if (full)
                    {
                        if (initialStore is not null) return RejectTree("multiple_initializers");
                        initialStore = store;
                    }
                    else stores.Add(new(store, slice.BeginOffset, slice.EndOffset, store.ValueOperand));
                }
                else return RejectTree("non_memory_user");
            }
            if (stores.Count < 2) return RejectTree("store_count");
            bool readModifyWrite = initialStore is not null && vectorType is not null && loads.Count != 0;
            bool storesOnly = initialStore is null && fullLoad is not null && loads.Count == 0;
            if (!readModifyWrite && !storesOnly) return RejectTree("pattern");
            LlvmBasicBlock block = stores[0].Store.Parent!;
            if (stores.Any(store => store.Store.Parent != block)) return RejectTree("store_blocks");
            List<LlvmValue> deleted = [];
            if (storesOnly)
            {
                stores.Sort(static (left, right) => left.Begin.CompareTo(right.Begin));
                ulong expected = allocaBegin;
                foreach (var store in stores)
                {
                    if (store.Begin != expected) return RejectTree("store_tiling");
                    expected = store.End;
                }
                if (expected != allocaEnd) return RejectTree("store_coverage");
                if (fullLoad!.Parent == block && stores.Any(store => !store.Store.ComesBefore(fullLoad))) return RejectTree("store_order");
                var insertion = new LlvmTransformBuilder(fullLoad!.Parent == block ? fullLoad : block.GetTerminator(), pass.Metrics);
                var values = stores.Select(store => store.Value).ToList();
                foreach (var store in stores) deleted.Add(store.Store);
                var merged = TreeMerge(values, insertion, allocatedElement);
                insertion.CreateAlignedStore(merged, newAlloca, SliceAlignment());
                ReplaceFullTreeLoad(fullLoad, merged, deleted);
                Count("rewriter.tree.stores_only");
                return deleted;
            }
            if (initialStore!.Parent != block || loads.Any(load => load.Load.Parent != block)) return RejectTree("access_blocks");
            List<TreeAccess> accesses = [];
            foreach (var load in loads) accesses.Add(new(load.Load, load.Begin, load.End, false));
            foreach (var store in stores) accesses.Add(new(store.Store, store.Begin, store.End, true));
            accesses.Sort(static (left, right) => left.Instruction == right.Instruction ? 0 : left.Instruction.ComesBefore(right.Instruction) ? -1 : 1);
            if (!initialStore.ComesBefore(accesses[0].Instruction)) return RejectTree("initializer_order");
            if (fullLoad?.Parent == block && !accesses[^1].Instruction.ComesBefore(fullLoad)) return RejectTree("load_order");
            var ranges = accesses.Select(access => (access.Begin, access.End)).Distinct().OrderBy(range => range.Begin).ThenBy(range => range.End).ToList();
            ulong next = allocaBegin;
            foreach (var range in ranges)
            {
                if (range.Begin != next) return RejectTree("access_tiling");
                next = range.End;
            }
            if (next != allocaEnd) return RejectTree("access_coverage");
            builder.SetInsertPoint(initialStore.Parent!, initialStore.Next);
            LlvmValue initialized = initialStore.ValueOperand;
            if (initialized.Type != allocaType) initialized = builder.CreateBitCast(initialized, allocaType, "init.cast");
            Dictionary<(ulong Begin, ulong End), LlvmValue> contents = [];
            foreach (var range in ranges)
            {
                uint first = VectorIndex(range.Begin), last = VectorIndex(range.End);
                contents.Add(range, builder.CreateShuffleVector(initialized, LlvmPoisonValue.Get(initialized.Type),
                    SequentialMask(first, last - first), "init.extract"));
            }
            deleted.Add(initialStore);
            foreach (var access in accesses)
            {
                var range = (access.Begin, access.End);
                if (access.IsStore) contents[range] = ((LlvmStoreInstruction)access.Instruction).ValueOperand;
                else
                {
                    LlvmValue value = contents[range];
                    if (value.Type != access.Instruction.Type)
                    {
                        builder.SetInsertPoint(access.Instruction);
                        value = builder.CreateBitCast(value, access.Instruction.Type);
                    }
                    access.Instruction.ReplaceAllUsesWith(value);
                }
                deleted.Add(access.Instruction);
            }
            var finalBuilder = new LlvmTransformBuilder(fullLoad?.Parent == block ? fullLoad! : block.GetTerminator(), pass.Metrics);
            var result = TreeMerge(ranges.Select(range => contents[range]).ToList(), finalBuilder, allocatedElement);
            finalBuilder.CreateAlignedStore(result, newAlloca, SliceAlignment());
            if (fullLoad is not null) ReplaceFullTreeLoad(fullLoad, result, deleted);
            Count("rewriter.tree.read_modify_write");
            return deleted;
        }

        private IReadOnlyList<LlvmValue>? RejectTree(string reason)
        {
            if (pass.Metrics is not null) Count("rewriter.tree.rejected." + reason);
            return null;
        }

        private bool ValidTreeType(LlvmType type) => type is LlvmFixedVectorType vector &&
            layout.GetTypeSizeInBits(vector.ElementType).GetFixedValue() % 8 == 0 && !vector.ElementType.IsPointerTy();

        private LlvmValue TreeMerge(List<LlvmValue> values, LlvmTransformBuilder insertion, LlvmType element)
        {
            while (values.Count > 1)
            {
                List<LlvmValue> next = [];
                for (int index = 0; index + 1 < values.Count; index += 2)
                {
                    next.Add(MergeTwoVectors(values[index], values[index + 1], layout, element, insertion));
                    Count("rewriter.tree.vector_pairs");
                }
                if (values.Count % 2 != 0) next.Add(values[^1]);
                values = next;
            }
            return values[0];
        }

        private void ReplaceFullTreeLoad(LlvmLoadInstruction load, LlvmValue merged, List<LlvmValue> deleted)
        {
            var insertion = new LlvmTransformBuilder(load, pass.Metrics);
            LlvmValue value = insertion.CreateAlignedLoad(merged.Type, newAlloca, SliceAlignment(), load.Name + ".sroa.new.load", load.IsVolatile);
            if (value.Type != load.Type) value = insertion.CreateBitCast(value, load.Type);
            load.ReplaceAllUsesWith(value); deleted.Add(load);
        }

        private static int[] SequentialMask(uint begin, uint count)
        {
            var mask = new int[count];
            for (uint index = 0; index < count; ++index) mask[index] = (int)(begin + index);
            return mask;
        }
    }
}
