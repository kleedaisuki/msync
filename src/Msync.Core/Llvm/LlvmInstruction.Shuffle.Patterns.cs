// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Instructions.cpp shuffle patterns.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmShuffleVectorInstruction
{
    private static bool IsReplicationMaskWithParameters(IReadOnlyList<int> values, int replication, int width)
    {
        Debug.Assert(values.Count == replication * width);
        for (int element = 0; element < width; ++element)
            for (int index = 0; index < replication; ++index)
                if (values[element * replication + index] != PoisonMaskElem && values[element * replication + index] != element) return false;
        return true;
    }
    public static bool IsReplicationMask(IReadOnlyList<int> values, out int replication, out int width)
    {
        replication = 0; width = 0;
        if (!values.Contains(PoisonMaskElem))
        {
            while (replication < values.Count && values[replication] == 0) ++replication;
            if (replication == 0 || values.Count % replication != 0) return false;
            width = values.Count / replication;
            return IsReplicationMaskWithParameters(values, replication, width);
        }
        int largest = -1;
        foreach (int value in values)
        {
            if (value == PoisonMaskElem) continue;
            if (value < largest) return false;
            largest = Math.Max(largest, value);
        }
        for (int factor = values.Count; factor >= 1; --factor)
        {
            if (values.Count % factor != 0) continue;
            int candidateWidth = values.Count / factor;
            if (!IsReplicationMaskWithParameters(values, factor, candidateWidth)) continue;
            replication = factor; width = candidateWidth;
            return true;
        }
        return false;
    }
    public bool IsReplicationMask(out int replication, out int width)
    {
        replication = 0; width = 0;
        if (Type is LlvmScalableVectorType) return false;
        width = SourceElements;
        if (mask.Length % width != 0) return false;
        replication = mask.Length / width;
        return IsReplicationMaskWithParameters(mask, replication, width);
    }
    public static bool IsOneUseSingleSourceMask(IReadOnlyList<int> values, int width)
    {
        if (width <= 0 || values.Count < width || values.Count % width != 0) return false;
        for (int offset = 0; offset < values.Count; offset += width)
        {
            bool allPoison = true;
            for (int index = 0; index < width; ++index) allPoison &= values[offset + index] == PoisonMaskElem;
            if (allPoison) continue;
            var used = LlvmAPInt.GetZero((uint)width);
            for (int index = 0; index < width; ++index)
            {
                int value = values[offset + index];
                if (value != PoisonMaskElem && value < width) used |= LlvmAPInt.GetOneBitSet((uint)width, (uint)value);
            }
            if (!used.IsAllOnes) return false;
        }
        return true;
    }
    public bool IsOneUseSingleSourceMask(int width) => Type is not LlvmScalableVectorType &&
        IsSingleSourceMask(mask, width) && IsOneUseSingleSourceMask(mask, width);
    public bool IsInterleave(uint factor) => Left.Type is LlvmFixedVectorType type &&
        IsInterleaveMask(mask, factor, type.NumElements * 2, out _);
    public static bool IsInterleaveMask(IReadOnlyList<int> values, uint factor, uint inputElements) =>
        IsInterleaveMask(values, factor, inputElements, out _);
    public static bool IsInterleaveMask(IReadOnlyList<int> values, uint factor, uint inputElements, out uint[] starts)
    {
        starts = [];
        uint count = (uint)values.Count;
        if (count % factor != 0) return false;
        uint laneLength = count / factor;
        if (!BitOperations.IsPow2(laneLength)) return false;
        starts = new uint[factor];
        for (uint index = 0; index < factor; ++index)
        {
            uint savedValue = 0, savedPoison = 0, element;
            for (element = 0; element < laneLength - 1; ++element)
            {
                uint lane = element * factor + index;
                int value = values[(int)lane], next = values[(int)(lane + factor)];
                if (value >= 0 && next >= 0 && value + 1 != next) break;
                if (value >= 0 && next < 0) { savedValue = (uint)value; savedPoison = 1; }
                if (savedPoison == 0 || value >= 0) continue;
                ++savedPoison;
                if (next >= 0 && savedValue + savedPoison != (uint)next) break;
            }
            if (element < laneLength - 1) return false;
            int start = 0;
            if (values[(int)index] >= 0) start = values[(int)index];
            else if (values[(int)((laneLength - 1) * factor + index)] >= 0)
                start = unchecked(values[(int)((laneLength - 1) * factor + index)] - (int)element);
            else if (savedPoison > 0) start = unchecked((int)(savedValue - (laneLength - 1 - savedPoison)));
            if (start < 0 || start + laneLength > inputElements) return false;
            starts[index] = (uint)start;
        }
        return true;
    }
    public static bool IsDeInterleaveMaskOfFactor(IReadOnlyList<int> values, uint factor, out uint start)
    {
        start = 0;
        for (uint candidate = 0; candidate < factor; ++candidate)
        {
            int index = 0;
            while (index < values.Count && (values[index] < 0 || (uint)values[index] == candidate + (uint)index * factor)) ++index;
            if (index != values.Count) continue;
            start = candidate;
            return true;
        }
        return false;
    }
    public static bool IsDeInterleaveMaskOfFactor(IReadOnlyList<int> values, uint factor) =>
        IsDeInterleaveMaskOfFactor(values, factor, out _);
    private static int MatchShuffleAsBitRotate(IReadOnlyList<int> values, int subElements)
    {
        Debug.Assert(values.Count % subElements == 0);
        int rotation = -1;
        for (int offset = 0; offset != values.Count; offset += subElements)
            for (int index = 0; index != subElements; ++index)
            {
                int value = values[offset + index];
                if (value < 0) continue;
                if (value < offset || value >= offset + subElements) return -1;
                int distance = (subElements - (value - (offset + index))) % subElements;
                if (rotation >= 0 && distance != rotation) return -1;
                rotation = distance;
            }
        return rotation;
    }
    public static bool IsBitRotateMask(IReadOnlyList<int> values, uint elementBits, uint minimumSubElements, uint maximumSubElements,
        out uint subElements, out uint rotation)
    {
        rotation = 0;
        for (subElements = minimumSubElements; subElements <= maximumSubElements; subElements *= 2)
        {
            int amount = MatchShuffleAsBitRotate(values, (int)subElements);
            if (amount < 0) continue;
            rotation = (uint)amount * elementBits;
            return true;
        }
        return false;
    }
}
