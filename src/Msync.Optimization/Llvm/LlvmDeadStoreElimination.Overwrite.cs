// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp isOverwrite/isPartialOverwrite.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private readonly Dictionary<LlvmBasicBlock, Dictionary<LlvmInstruction, SortedList<long, long>>> intervals = [];
        private readonly List<LlvmBasicBlock> intervalBlockOrder = [];
        private readonly Dictionary<LlvmBasicBlock, List<LlvmInstruction>> intervalInstructionOrder = [];
        private Overwrite IsOverwrite(LlvmInstruction killing, LlvmInstruction dead, LlvmMemoryLocation killingLoc,
            LlvmMemoryLocation deadLoc, out long killingOffset, out long deadOffset)
        {
            killingOffset = deadOffset = 0;
            Count("overwrite.queries");
            if (!IsLoopIndependent(dead, killing, deadLoc)) return Overwrite.Unknown;
            var deadPointer = LlvmValueTracking.StripPointerCasts(deadLoc.Ptr!, Metrics);
            var killingPointer = LlvmValueTracking.StripPointerCasts(killingLoc.Ptr!, Metrics);
            var deadObject = Underlying(deadPointer);
            var killingObject = Underlying(killingPointer);
            if (ReferenceEquals(deadObject, killingObject) && killingLoc.Size.IsPrecise && LlvmBasicAliasAnalysis.IsIdentifiedObject(killingObject))
            {
                var size = LlvmObjectSize.GetBaseObjectSize(killingObject, layout, metrics: Metrics);
                if (size is { IsScalable: false } exact && exact == killingLoc.Size.GetValue()) return Overwrite.Complete;
            }
            // No checked libcalls, dynamic-length or masked-store intrinsic IDs exist.
            if (!killingLoc.Size.IsPrecise || !deadLoc.Size.IsPrecise) return Overwrite.Unknown;
            if (killingLoc.Size.IsScalable || deadLoc.Size.IsScalable) return Overwrite.Unknown;
            ulong killingSize = killingLoc.Size.GetValue().GetFixedValue();
            ulong deadSize = deadLoc.Size.GetValue().GetFixedValue();
            Overwrite CheckAlias()
            {
                var result = alias.Alias(killingLoc, deadLoc);
                if (result == LlvmAliasKind.MustAlias && killingSize >= deadSize) return Overwrite.Complete;
                if (result == LlvmAliasKind.PartialAlias && result.HasOffset && result.Offset >= 0
                    && unchecked((ulong)result.Offset + deadSize) <= killingSize) return Overwrite.Complete;
                return !ReferenceEquals(deadObject, killingObject) && result == LlvmAliasKind.NoAlias ? Overwrite.None : Overwrite.Unknown;
            }
            if (!ReferenceEquals(deadObject, killingObject)) return CheckAlias();
            uint width = layout.GetIndexTypeSizeInBits(deadPointer.Type);
            if (width != layout.GetIndexTypeSizeInBits(killingPointer.Type)) return CheckAlias();
            var deadBaseOffset = LlvmAPInt.GetZero(width);
            var killingBaseOffset = LlvmAPInt.GetZero(width);
            var deadBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(deadPointer, layout, ref deadBaseOffset, metrics: Metrics);
            var killingBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(killingPointer, layout, ref killingBaseOffset, metrics: Metrics);
            if (!ReferenceEquals(deadBase, killingBase)) return CheckAlias();
            if (killingSize == 0) return Overwrite.None;
            var delta = deadBaseOffset - killingBaseOffset;
            if (UnsignedLessThan(delta, killingSize))
            {
                if (unchecked(delta.GetZExtValue() + deadSize) <= killingSize) return Overwrite.Complete;
                killingOffset = unchecked(-(long)delta.GetZExtValue());
                return Overwrite.MaybePartial;
            }
            if (UnsignedLessThan(-delta, deadSize))
            {
                killingOffset = unchecked((long)(-delta).GetZExtValue());
                return Overwrite.MaybePartial;
            }
            return Overwrite.None;
        }
        private static bool UnsignedLessThan(LlvmAPInt value, ulong limit) => value.GetActiveBits() <= 64 && value.GetZExtValue() < limit;
        private SortedList<long, long> GetIntervals(LlvmInstruction instruction)
        {
            if (!intervals.TryGetValue(instruction.Parent!, out var byInstruction))
            {
                intervals.Add(instruction.Parent!, byInstruction = []);
                intervalBlockOrder.Add(instruction.Parent!);
                intervalInstructionOrder.Add(instruction.Parent!, []);
            }
            if (!byInstruction.TryGetValue(instruction, out var ranges))
            {
                byInstruction.Add(instruction, ranges = new());
                intervalInstructionOrder[instruction.Parent!].Add(instruction);
            }
            return ranges;
        }
        private void RemoveIntervals(LlvmInstruction instruction)
        {
            if (intervals.TryGetValue(instruction.Parent!, out var byInstruction) && byInstruction.Remove(instruction))
                intervalInstructionOrder[instruction.Parent!].Remove(instruction);
        }
        private static int LowerBound(SortedList<long, long> ranges, long value)
        {
            int begin = 0, count = ranges.Count;
            while (count > 0)
            {
                int half = count / 2, middle = begin + half;
                if (ranges.Keys[middle] < value) { begin = middle + 1; count -= half + 1; }
                else count = half;
            }
            return begin;
        }
        private Overwrite PartialOverwrite(LlvmMemoryLocation killing, LlvmMemoryLocation dead, long killingOff, long deadOff, LlvmInstruction deadInstruction)
        {
            ulong killingSize = killing.Size.GetValue().GetFixedValue(), deadSize = dead.Size.GetValue().GetFixedValue();
            long deadEnd = unchecked(deadOff + (long)deadSize), killingEnd = unchecked(killingOff + (long)killingSize);
            if (options.PartialOverwriteTracking && killingOff < deadEnd && killingEnd >= deadOff)
            {
                var ranges = GetIntervals(deadInstruction);
                long start = killingOff, end = killingEnd;
                int index = LowerBound(ranges, start);
                if (index != ranges.Count && ranges.Values[index] <= end)
                {
                    start = Math.Min(start, ranges.Values[index]);
                    end = Math.Max(end, ranges.Keys[index]);
                    ranges.RemoveAt(index);
                    while (index != ranges.Count && ranges.Values[index] <= end)
                    {
                        Debug.Assert(ranges.Values[index] > start);
                        end = Math.Max(end, ranges.Keys[index]);
                        ranges.RemoveAt(index);
                    }
                }
                ranges[end] = start;
                Count("partial.intervals.recorded");
                if (ranges.Values[0] <= deadOff && ranges.Keys[0] >= deadEnd)
                { Count("partial.complete"); return Overwrite.Complete; }
            }
            if (options.PartialStoreMerging && killingOff >= deadOff && deadEnd > killingOff
                && unchecked((ulong)(killingOff - deadOff) + killingSize) <= deadSize) return Overwrite.PartialEarlierWithFullLater;
            if (!options.PartialOverwriteTracking && killingOff > deadOff && killingOff < deadEnd && killingEnd >= deadEnd) return Overwrite.End;
            if (!options.PartialOverwriteTracking && killingOff <= deadOff && killingEnd > deadOff)
            { Debug.Assert(killingEnd < deadEnd); return Overwrite.Begin; }
            return Overwrite.Unknown;
        }
        private bool RemovePartialOverlaps()
        {
            // Both source shortening predicates reject ordinary StoreInst. No
            // memset/memtransfer carrier exists, so every represented interval
            // entry takes those source false branches after overlap tracking.
            foreach (var block in intervalBlockOrder)
                foreach (var instruction in intervalInstructionOrder[block]) Count("partial.shortening.store_not_shortenable");
            return false;
        }
    }
}
