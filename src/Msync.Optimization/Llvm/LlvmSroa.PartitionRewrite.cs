// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp SROA::rewritePartition.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class State
    {
        internal (LlvmAllocaInstruction? Alloca, ulong Bits) RewritePartition(LlvmAllocaInstruction allocation,
            AllocaSlices slices, Partition partition)
        {
            if (Trace is not { } trace) return RewritePartitionCore(allocation, slices, partition);
            return trace.MeasureDetailed("optimizer.llvm-sroa.rewrite-partition",
                (State: this, Alloca: allocation, Slices: slices, Partition: partition),
                static work => work.State.RewritePartitionCore(work.Alloca, work.Slices, work.Partition),
                attributes: new Dictionary<string, string>
                {
                    ["function.name"] = allocation.Parent!.Parent!.Name,
                    ["alloca.name"] = allocation.Name,
                    ["partition.begin"] = partition.BeginOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["partition.end"] = partition.EndOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        private static LlvmStorageView OriginalStorageView(LlvmAllocaInstruction allocation, ulong offset)
        {
            var original = allocation.StorageView ?? new LlvmStorageView(allocation.AllocatedType,
                allocation.AllocatedLogicalType, 0);
            return original with { ByteOffset = unchecked(original.ByteOffset + offset) };
        }

        private (LlvmAllocaInstruction? Alloca, ulong Bits) RewritePartitionCore(LlvmAllocaInstruction allocation,
            AllocaSlices slices, Partition partition)
        {
            var (type, integerWidening, vector) = SelectPartitionType(partition, allocation);
            LlvmAllocaInstruction replacement;
            if (type == allocation.AllocatedType && partition.BeginOffset == 0)
            { replacement = allocation; Count("partition.allocas_reused"); }
            else
            {
                LlvmAlign alignment = LlvmAlign.CommonAlignment(allocation.Alignment, partition.BeginOffset);
                replacement = new(type, LlvmConstantInt.Get(LlvmType.GetInt32Ty(C), 1), allocation.AddressSpace, alignment)
                {
                    Source = allocation.Source,
                    StorageView = OriginalStorageView(allocation, partition.BeginOffset)
                };
                replacement.SetName(allocation.Name + ".sroa." + partition.StartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                allocation.Parent!.InsertBefore(replacement, allocation);
                LlvmLocalCfg.TryEnforceAlignment(replacement, DL.GetPrefTypeAlign(type), DL);
                Count("partition.allocas_created");
                Count("partition.storage_views.created");
            }
            int postPromotionSize = PostPromotionWorklist.Count;
            ulong uses = 0;
            OrderedSet<LlvmPhiInstruction> phiUsers = new();
            OrderedSet<LlvmSelectInstruction> selectUsers = new();
            AllocaSliceRewriter rewriter = new(DL, slices, this, allocation, replacement, type,
                partition.BeginOffset, partition.EndOffset, integerWidening, vector, phiUsers, selectUsers);
            bool promotable = true;
            if (rewriter.RewriteTreeStructuredMerge(partition) is { } deleted)
            {
                uses += (ulong)deleted.Count + 1;
                foreach (LlvmValue value in deleted) AddDead((LlvmInstruction)value);
                Count("partition.tree_merges_rewritten");
            }
            else
            {
                foreach (Slice tail in partition.SplitSliceTails) { promotable &= rewriter.Visit(tail); ++uses; }
                foreach (Slice slice in partition) { promotable &= rewriter.Visit(slice); ++uses; }
            }
            Count("partition.uses", uses);
            if (Metrics is not null)
                Metrics["partition.uses.max"] = Math.Max(Metrics.GetValueOrDefault("partition.uses.max"), uses);
            List<LlvmPhiInstruction> speculative = [];
            List<LlvmPhiInstruction> stored = [];
            foreach (var phi in phiUsers)
            {
                if (IsSafePHIToSpeculate(phi)) { speculative.Add(phi); continue; }
                if (GetPHIStoreToRewrite(phi, PreserveCFG) is not null) { stored.Add(phi); continue; }
                promotable = false;
                selectUsers.Clear();
                Count("partition.nonpromotable_phi");
                break;
            }
            List<KeyValuePair<LlvmSelectInstruction, RewriteableMemOps>> rewrittenSelects = new(selectUsers.Count);
            foreach (var select in selectUsers)
            {
                var operations = IsSafeSelectToSpeculateOrPredicate(select, PreserveCFG);
                if (operations is null) { promotable = false; Count("partition.nonpromotable_select"); break; }
                rewrittenSelects.Add(KeyValuePair.Create(select, operations));
            }
            if (promotable)
            {
                foreach (LlvmUse use in slices.DeadUseIfPromotable)
                {
                    LlvmInstruction? old = use.Value as LlvmInstruction;
                    LlvmValue.DropDroppableUse(use);
                    if (old is not null && LlvmDeadCodeElimination.IsInstructionTriviallyDead(old)) AddDead(old);
                    Count("partition.droppable_uses_removed");
                }
                if (speculative.Count == 0 && stored.Count == 0 && selectUsers.Count == 0)
                { PromotableAllocas.Add(replacement); Count("partition.allocas_promotable"); }
                else
                {
                    SpeculatablePHIs.AddRange(speculative);
                    PHIsWithStoreToRewrite.AddRange(stored);
                    foreach (var rewrite in rewrittenSelects) SelectsToRewrite.Insert(rewrite);
                    Worklist.Add(replacement);
                    Count("partition.allocas_requeued_for_speculation");
                }
            }
            else
            {
                while (PostPromotionWorklist.Count > postPromotionSize) PostPromotionWorklist.PopBack();
                if (ReferenceEquals(replacement, allocation))
                { Count("partition.unchanged_unpromotable"); return (null, 0); }
                Worklist.Add(replacement);
                Count("partition.new_allocas_requeued");
            }
            return (replacement, DL.GetTypeSizeInBits(type).GetFixedValue());
        }
    }
}
