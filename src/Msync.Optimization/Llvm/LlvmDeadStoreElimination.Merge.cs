// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp memoryIsNotModifiedBetween/tryToMergePartialOverlappingStores.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private bool MemoryNotModifiedBetween(LlvmInstruction first, LlvmInstruction second)
        {
            var location = LlvmMemoryLocation.Get(second, layout);
            var work = new List<(LlvmBasicBlock Block, LlvmPhiTransAddr Address)> { (second.Parent!, new(location.Ptr!, layout, Metrics)) };
            var visited = new Dictionary<LlvmBasicBlock, LlvmValue>();
            bool firstBlock = true;
            while (work.Count != 0)
            {
                var (block, address) = work[^1];
                work.RemoveAt(work.Count - 1);
                var pointer = address.Address!;
                var begin = ReferenceEquals(block, first.Parent) ? first.Next : block.FirstInstruction;
                LlvmInstruction? end;
                if (firstBlock) { Debug.Assert(ReferenceEquals(block, second.Parent)); end = second; firstBlock = false; }
                else end = null;
                for (var instruction = begin; !ReferenceEquals(instruction, end); instruction = instruction!.Next)
                {
                    if (instruction!.MayWriteToMemory() && !ReferenceEquals(instruction, second)
                        && alias.GetModRefInfo(instruction, location.WithNewPtr(pointer)).IsModSet()) return false;
                    Count("partial.merge_between.instructions");
                }
                if (ReferenceEquals(block, first.Parent)) continue;
                Debug.Assert(!ReferenceEquals(block, function.BasicBlocks[0]));
                foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                {
                    var translated = address.Clone();
                    if (translated.NeedsPHITranslationFromBlock(block))
                    {
                        if (!translated.IsPotentiallyPHITranslatable() || translated.TranslateValue(block, predecessor, dominators, false) is null)
                            return false;
                    }
                    var translatedPointer = translated.Address!;
                    if (visited.TryGetValue(predecessor, out var previous))
                    {
                        if (!ReferenceEquals(previous, translatedPointer)) return false;
                        continue;
                    }
                    visited.Add(predecessor, translatedPointer);
                    work.Add((predecessor, translated));
                }
            }
            return true;
        }
        private LlvmConstantInt? TryMergeStores(LlvmStoreInstruction killing, LlvmStoreInstruction dead, long killingOffset, long deadOffset)
        {
            if (dead.ValueOperand is not LlvmConstantInt deadConstant || killing.ValueOperand is not LlvmConstantInt killingConstant
                || layout.GetTypeSizeInBits(deadConstant.Type) != layout.GetTypeStoreSizeInBits(deadConstant.Type)
                || layout.GetTypeSizeInBits(killingConstant.Type) != layout.GetTypeStoreSizeInBits(killingConstant.Type)
                || !MemoryNotModifiedBetween(dead, killing)) return null;
            if (!killing.IsUnordered || !dead.IsUnordered || killing.Ordering != dead.Ordering) return null;
            var deadValue = deadConstant.Value;
            var killingValue = killingConstant.Value;
            uint killingBits = killingValue.BitWidth;
            Debug.Assert(deadValue.BitWidth > killingValue.BitWidth);
            killingValue = killingValue.ZExt(deadValue.BitWidth);
            uint difference = unchecked((uint)((killingOffset - deadOffset) * 8));
            uint shift = layout.IsBigEndian ? deadValue.BitWidth - difference - killingBits : difference;
            var mask = LlvmAPInt.GetBitsSet(deadValue.BitWidth, shift, shift + killingBits);
            var merged = (deadValue & ~mask) | killingValue.Shl(shift);
            return LlvmConstantInt.Get(deadConstant.Context, merged);
        }
    }
}
