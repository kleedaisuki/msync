// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private readonly Dictionary<LlvmValue, StoreRunObject> storeRunObjects = new(ReferenceEqualityComparer.Instance);
        private sealed class StoreRunObject(uint width)
        {
            internal readonly uint Width = width;
            internal readonly SortedList<long, long> Ranges = new();
        }
        private LlvmMemoryDef? storeRunPrevious, indexedKilling;
        private LlvmValue? storeRunBase;
        private LlvmMemoryAccess? storeRunBoundary, indexedStart;
        private uint storeRunWidth;
        private bool storeRunIdentified;
        private ulong storeRunLength, indexedSkipped;

        private void PrepareStoreCandidates(LlvmMemoryDef killing, LlvmMemoryLocation location)
        {
            indexedKilling = null;
            CountCandidate("dse.candidates.requests");
            if (killing.MemoryInstruction is not LlvmStoreInstruction { IsUnordered: true } store
                || !location.Size.IsPrecise || location.Size.IsScalable)
            { ResetStoreRun(); CountCandidate("dse.candidates.opaque_store"); return; }
            var pointer = StripCasts(location.Ptr!);
            var root = ConstantOffset(pointer, out var displacement);
            ulong size = location.Size.GetValue().GetFixedValue();
            uint width = displacement.BitWidth;
            if (width > 64 || size == 0 || size > long.MaxValue)
            { ResetStoreRun(); CountCandidate("dse.candidates.unrepresented_range"); return; }
            long begin = displacement.GetSExtValue();
            long maximum = width == 64 ? long.MaxValue : (1L << (int)(width - 1)) - 1;
            if (begin > maximum - (long)size)
            { ResetStoreRun(); CountCandidate("dse.candidates.wrapping_range"); return; }
            long end = begin + (long)size;
            bool identified = LlvmBasicAliasAnalysis.IsIdentifiedObject(root);
            bool compatibleRoot = ReferenceEquals(storeRunBase, root) && storeRunWidth == width
                || storeRunIdentified && identified;
            if (storeRunObjects.TryGetValue(root, out var partition) && partition.Width != width) compatibleRoot = false;
            bool extends = storeRunPrevious is { } previous && ReferenceEquals(previous.Block, killing.Block)
                && ReferenceEquals(killing.DefiningAccess, previous) && compatibleRoot && StoreRunHasNoObserver(previous, killing);
            if (!extends)
            {
                ResetStoreRun();
                storeRunIdentified = identified;
                storeRunBoundary = killing.DefiningAccess;
                CountCandidate("dse.candidates.runs");
            }
            if (!storeRunObjects.TryGetValue(root, out partition))
            {
                storeRunObjects.Add(root, partition = new(width));
                if (identified) CountCandidate("dse.candidates.identified_root_partitions");
            }
            var ranges = partition.Ranges;
            int index = LowerBound(ranges, begin);
            bool disjoint = (index == ranges.Count || ranges.Keys[index] >= end)
                && (index == 0 || ranges.Values[index - 1] <= begin);
            if (disjoint && storeRunLength != 0)
            {
                indexedKilling = killing;
                indexedStart = storeRunBoundary;
                indexedSkipped = storeRunLength;
            }
            else if (!disjoint) CountCandidate("dse.candidates.overlapping_range");
            if (index != 0 && ranges.Values[index - 1] >= begin) --index;
            while (index < ranges.Count && ranges.Keys[index] <= end)
            {
                begin = Math.Min(begin, ranges.Keys[index]);
                end = Math.Max(end, ranges.Values[index]);
                ranges.RemoveAt(index);
            }
            ranges.Add(begin, end);
            storeRunBase = root;
            storeRunWidth = width;
            storeRunPrevious = killing;
            ++storeRunLength;
        }

        private bool StoreRunHasNoObserver(LlvmMemoryDef previous, LlvmMemoryDef killing)
        {
            foreach (var use in previous.Uses)
            {
                if (use.User is LlvmMemoryDef && use.OperandNo == 1) continue;
                if (ReferenceEquals(use.User, killing) && use.OperandNo == 0) continue;
                if (use.User is LlvmMemoryUse { MemoryInstruction: { } read } && IsUnrelatedStoreRunRead(read)) continue;
                CountCandidate("dse.candidates.memory_use_boundary");
                return false;
            }
            for (var instruction = previous.MemoryInstruction!.Next;
                !ReferenceEquals(instruction, killing.MemoryInstruction); instruction = instruction!.Next)
            {
                if (!instruction!.MayReadFromMemory() && !instruction.MayWriteToMemory() && !instruction.MayThrow()) continue;
                if (IsUnrelatedStoreRunRead(instruction)) continue;
                CountCandidate("dse.candidates.effect_boundary");
                return false;
            }
            return true;
        }

        private bool IsUnrelatedStoreRunRead(LlvmInstruction instruction)
        {
            if (!storeRunIdentified || instruction is not LlvmLoadInstruction { IsUnordered: true } load) return false;
            var root = Underlying(load.PointerOperand);
            if (!LlvmBasicAliasAnalysis.IsIdentifiedObject(root) || storeRunObjects.ContainsKey(root)) return false;
            CountCandidate("dse.candidates.disjoint_read_checks");
            return true;
        }

        private LlvmMemoryAccess IndexedCandidateStart(LlvmMemoryDef killing, LlvmMemoryAccess start)
        {
            if (!ReferenceEquals(indexedKilling, killing) || !ReferenceEquals(start, killing.DefiningAccess)) return start;
            CountCandidate("dse.candidates.disjoint_runs_skipped");
            CountCandidate("dse.candidates.disjoint_definitions_skipped", indexedSkipped);
            return indexedStart!;
        }

        private void ResetStoreRun()
        {
            storeRunObjects.Clear();
            storeRunPrevious = indexedKilling = null;
            storeRunBase = null;
            storeRunBoundary = indexedStart = null;
            storeRunLength = indexedSkipped = 0;
        }

        private void CountCandidate(string key, ulong value = 1)
        {
            if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + value;
        }
    }
}
