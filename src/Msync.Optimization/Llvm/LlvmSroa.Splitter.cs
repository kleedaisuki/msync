// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp SliceSplitter and findOverlappingCopySlice.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    private readonly record struct SplitPart(int Index, ulong Offset, ulong Size);

    // Upstream SmallPtrSet keeps ordinary one-slice instructions inline.
    private readonly struct SliceGroup(Slice first, HashSet<Slice>? overflow = null)
    {
        internal Slice First { get; } = first;
        internal HashSet<Slice>? Overflow { get; } = overflow;
    }

    private sealed class SliceSplitter(LlvmTransformBuilder builder, LlvmDataLayout layout,
        IReadOnlyList<ulong> splits, uint size)
    {
        internal IEnumerable<SplitPart> Parts()
        {
            for (int index = 0; index <= splits.Count; ++index)
            {
                ulong offset = index == 0 ? 0 : splits[index - 1];
                ulong next = index < splits.Count ? splits[index] : size;
                yield return new(index, offset, next - offset);
            }
        }

        internal LlvmLoadInstruction CreateLoad(LlvmValue pointer, SplitPart part, LlvmInstruction original)
        {
            var type = LlvmIntegerType.Get(original.Context, unchecked((uint)(part.Size * 8)));
            uint addressSpace = ((LlvmPointerType)pointer.Type).AddressSpace;
            LlvmValue adjusted = GetAdjustedPtr(builder, layout, pointer,
                new LlvmAPInt(layout.GetIndexSizeInBits(addressSpace), part.Offset), pointer.Type, pointer.Name + ".");
            return builder.CreateAlignedLoad(type, adjusted, GetAdjustedAlignment(original, part.Offset), original.Name);
        }

        internal LlvmStoreInstruction CreateStore(LlvmValue value, LlvmValue pointer, SplitPart part,
            LlvmInstruction original)
        {
            uint addressSpace = ((LlvmPointerType)pointer.Type).AddressSpace;
            LlvmValue adjusted = GetAdjustedPtr(builder, layout, pointer,
                new LlvmAPInt(layout.GetIndexSizeInBits(addressSpace), part.Offset), pointer.Type, pointer.Name + ".");
            return builder.CreateAlignedStore(value, adjusted, GetAdjustedAlignment(original, part.Offset));
        }

        internal static Slice CreateSlice(LlvmInstruction instruction, ulong baseOffset, SplitPart part)
        {
            int pointerIndex = instruction is LlvmLoadInstruction ? 0 : 1;
            Debug.Assert(instruction is LlvmLoadInstruction or LlvmStoreInstruction);
            return new(baseOffset + part.Offset, baseOffset + part.Offset + part.Size,
                instruction.GetOperandUse(pointerIndex));
        }
    }

    private static Slice? FindOverlappingCopySlice(Slice slice,
        IReadOnlyDictionary<LlvmInstruction, SliceGroup> sliceMap)
    {
        if (slice.EndOffset - slice.BeginOffset == 1) return null;
        LlvmInstruction instruction = (LlvmInstruction)slice.Use!.User;
        LlvmInstruction? paired;
        if (instruction is LlvmLoadInstruction load)
        {
            if (!load.HasOneUser) return null;
            paired = (LlvmInstruction)load.Users.First();
        }
        else if (instruction is LlvmStoreInstruction store)
        {
            paired = store.ValueOperand as LlvmInstruction;
            if (paired is null || !paired.HasOneUser) return null;
        }
        else return null;
        if (!sliceMap.TryGetValue(paired, out var pairedSlices)) return null;
        if (PartiallyOverlaps(slice, pairedSlices.First)) return pairedSlices.First;
        if (pairedSlices.Overflow is not { } overflow) return null;
        foreach (Slice candidate in overflow)
            if (PartiallyOverlaps(slice, candidate)) return candidate;
        return null;
    }

    private static bool PartiallyOverlaps(Slice slice, Slice candidate) =>
        slice.BeginOffset > candidate.BeginOffset && slice.BeginOffset < candidate.EndOffset ||
        candidate.BeginOffset > slice.BeginOffset && candidate.BeginOffset < slice.EndOffset;
}
