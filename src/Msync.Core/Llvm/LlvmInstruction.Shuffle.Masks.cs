// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Instructions.cpp shuffle masks.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmShuffleVectorInstruction
{
    private int SourceElements => (int)((LlvmVectorType)Left.Type).ElementCount.KnownMinValue;
    public bool IsSingleSource => !ChangesLength && IsSingleSourceMask(mask, mask.Length);
    public bool IsIdentity => Type is not LlvmScalableVectorType && !ChangesLength && IsIdentityMask(mask, mask.Length);
    public bool IsSelect => !ChangesLength && IsSelectMask(mask, mask.Length);
    public bool IsReverse => !ChangesLength && IsReverseMask(mask, mask.Length);
    public bool IsZeroEltSplat => !ChangesLength && IsZeroEltSplatMask(mask, mask.Length);
    public bool IsTranspose => !ChangesLength && IsTransposeMask(mask, mask.Length);

    private static bool IsSingleSourceMaskImpl(IReadOnlyList<int> values, int sourceElements, int offset, int count)
    {
        Debug.Assert(count != 0);
        bool left = false, right = false;
        for (int index = offset; index < offset + count; ++index)
        {
            int value = values[index];
            if (value == PoisonMaskElem) continue;
            Debug.Assert(value >= 0 && value < sourceElements * 2L);
            left |= value < sourceElements;
            right |= value >= sourceElements;
            if (left && right) return false;
        }
        return left || right;
    }
    public static bool IsSingleSourceMask(IReadOnlyList<int> values, int sourceElements) =>
        IsSingleSourceMaskImpl(values, sourceElements, 0, values.Count);
    private static bool IsIdentityMaskImpl(IReadOnlyList<int> values, int sourceElements, int offset, int count)
    {
        if (!IsSingleSourceMaskImpl(values, sourceElements, offset, count)) return false;
        for (int index = 0; index < count; ++index)
        {
            int value = values[offset + index];
            if (value != PoisonMaskElem && value != index && value != sourceElements + index) return false;
        }
        return true;
    }
    public static bool IsIdentityMask(IReadOnlyList<int> values, int sourceElements) =>
        values.Count == sourceElements && IsIdentityMaskImpl(values, sourceElements, 0, values.Count);
    public static bool IsReverseMask(IReadOnlyList<int> values, int sourceElements)
    {
        if (values.Count != sourceElements || sourceElements < 2 || !IsSingleSourceMask(values, sourceElements)) return false;
        for (int index = 0; index < values.Count; ++index)
            if (values[index] != PoisonMaskElem && values[index] != sourceElements - 1 - index && values[index] != sourceElements * 2 - 1 - index)
                return false;
        return true;
    }
    public static bool IsZeroEltSplatMask(IReadOnlyList<int> values, int sourceElements)
    {
        if (values.Count != sourceElements || !IsSingleSourceMask(values, sourceElements)) return false;
        foreach (int value in values) if (value != PoisonMaskElem && value != 0 && value != sourceElements) return false;
        return true;
    }
    public static bool IsSelectMask(IReadOnlyList<int> values, int sourceElements)
    {
        if (values.Count != sourceElements || IsSingleSourceMask(values, sourceElements)) return false;
        for (int index = 0; index < values.Count; ++index)
            if (values[index] != PoisonMaskElem && values[index] != index && values[index] != sourceElements + index) return false;
        return true;
    }
    public static bool IsTransposeMask(IReadOnlyList<int> values, int sourceElements)
    {
        int count = values.Count;
        if (count != sourceElements || count < 2 || !BitOperations.IsPow2((uint)count)) return false;
        if (values[0] is not (0 or 1) || values[1] - values[0] != sourceElements) return false;
        for (int index = 2; index < count; ++index)
            if (values[index] == PoisonMaskElem || values[index] - values[index - 2] != 2) return false;
        return true;
    }
    public static bool IsSpliceMask(IReadOnlyList<int> values, int sourceElements, out int start)
    {
        start = -1;
        if (values.Count != sourceElements) return false;
        for (int index = 0; index < values.Count; ++index)
        {
            int value = values[index];
            if (value == PoisonMaskElem) continue;
            if (start == -1)
            {
                if (value < index || sourceElements <= value - index) return false;
                start = value - index;
            }
            else if (value != start + index) return false;
        }
        return start != -1;
    }
    public bool IsSplice(out int index)
    {
        index = -1;
        return !ChangesLength && IsSpliceMask(mask, mask.Length, out index);
    }
    public static bool IsExtractSubvectorMask(IReadOnlyList<int> values, int sourceElements, out int start)
    {
        start = -1;
        if (!IsSingleSourceMask(values, sourceElements) || sourceElements <= values.Count) return false;
        int subIndex = -1;
        for (int index = 0; index < values.Count; ++index)
        {
            if (values[index] < 0) continue;
            int offset = values[index] % sourceElements - index;
            if (subIndex >= 0 && subIndex != offset) return false;
            subIndex = offset;
        }
        if (subIndex < 0 || subIndex + values.Count > sourceElements) return false;
        start = subIndex;
        return true;
    }
    public bool IsExtractSubvectorMask(out int index)
    {
        index = -1;
        return Type is not LlvmScalableVectorType && IsExtractSubvectorMask(mask, SourceElements, out index);
    }
    public static bool IsInsertSubvectorMask(IReadOnlyList<int> values, int sourceElements, out int subElements, out int start)
    {
        subElements = 0; start = -1;
        int count = values.Count;
        if (count < sourceElements || IsSingleSourceMask(values, sourceElements)) return false;
        var undef = LlvmAPInt.GetZero((uint)count);
        var left = undef; var right = undef;
        bool leftIdentity = true, rightIdentity = true;
        for (int index = 0; index < count; ++index)
        {
            var bit = LlvmAPInt.GetOneBitSet((uint)count, (uint)index);
            int value = values[index];
            if (value < 0) { undef |= bit; continue; }
            if (value < sourceElements) { left |= bit; leftIdentity &= value == index; continue; }
            right |= bit;
            rightIdentity &= value == index + sourceElements;
        }
        Debug.Assert((left | right | undef).IsAllOnes && !left.IsZero && !right.IsZero);
        int leftLow = (int)left.CountTrailingZeros(), rightLow = (int)right.CountTrailingZeros();
        int leftHigh = count - (int)left.CountLeadingZeros(), rightHigh = count - (int)right.CountLeadingZeros();
        if (leftIdentity && IsIdentityMaskImpl(values, sourceElements, rightLow, rightHigh - rightLow))
        { subElements = rightHigh - rightLow; start = rightLow; return true; }
        if (rightIdentity && IsIdentityMaskImpl(values, sourceElements, leftLow, leftHigh - leftLow))
        { subElements = leftHigh - leftLow; start = leftLow; return true; }
        return false;
    }
    public bool IsInsertSubvectorMask(out int subElements, out int index)
    {
        subElements = 0; index = -1;
        return Type is not LlvmScalableVectorType && IsInsertSubvectorMask(mask, SourceElements, out subElements, out index);
    }
    public bool IsIdentityWithPadding()
    {
        if (Type is LlvmScalableVectorType || mask.Length <= SourceElements ||
            !IsIdentityMaskImpl(mask, SourceElements, 0, mask.Length)) return false;
        for (int index = SourceElements; index < mask.Length; ++index) if (mask[index] != PoisonMaskElem) return false;
        return true;
    }
    public bool IsIdentityWithExtract() => Type is not LlvmScalableVectorType && mask.Length < SourceElements &&
        IsIdentityMaskImpl(mask, SourceElements, 0, mask.Length);
    public bool IsConcat() => Left is not LlvmUndefValue && Right is not LlvmUndefValue && Type is not LlvmScalableVectorType &&
        mask.Length == SourceElements * 2 && IsIdentityMaskImpl(mask, mask.Length, 0, mask.Length);
}
