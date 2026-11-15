// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SROA.cpp extractVector / insertVector / mergeTwoVectors.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal static LlvmValue ExtractVector(LlvmTransformBuilder builder, LlvmValue value,
        uint beginIndex, uint endIndex, string name)
    {
        var type = (LlvmFixedVectorType)value.Type;
        uint count = endIndex - beginIndex;
        Debug.Assert(count <= type.NumElements);
        if (count == type.NumElements) return value;
        if (count == 1) return builder.CreateExtractElement(value, beginIndex, name + ".extract");
        int[] mask = Enumerable.Range((int)beginIndex, (int)count).ToArray();
        return builder.CreateShuffleVector(value, mask, name + ".extract");
    }

    internal static LlvmValue InsertVector(LlvmTransformBuilder builder, LlvmValue oldValue,
        LlvmValue value, uint beginIndex, string name)
    {
        var oldType = (LlvmVectorType)oldValue.Type;
        if (value.Type is not LlvmVectorType)
            return builder.CreateInsertElement(oldValue, value, beginIndex, name + ".insert");
        var inputType = (LlvmFixedVectorType)value.Type;
        uint count = ((LlvmFixedVectorType)oldType).NumElements;
        Debug.Assert(inputType.NumElements <= count);
        if (inputType.NumElements == count)
        { Debug.Assert(ReferenceEquals(value.Type, oldType)); return value; }
        uint end = beginIndex + inputType.NumElements;
        int[] mask = new int[count];
        for (uint index = 0; index < count; ++index)
            mask[index] = index >= beginIndex && index < end ? (int)(index - beginIndex) : -1;
        value = builder.CreateShuffleVector(value, mask, name + ".expand");
        for (uint index = 0; index < count; ++index)
            mask[index] = index >= beginIndex && index < end ? (int)index : (int)(index + count);
        return builder.CreateShuffleVector(value, oldValue, mask, name + "blend");
    }

    internal static LlvmValue MergeTwoVectors(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmType elementType, LlvmTransformBuilder builder)
    {
        var leftType = (LlvmFixedVectorType)left.Type;
        var rightType = (LlvmFixedVectorType)right.Type;
        BitcastIfNeeded(ref left, ref leftType);
        BitcastIfNeeded(ref right, ref rightType);
        uint leftCount = leftType.NumElements, rightCount = rightType.NumElements;
        List<int> mask = [];
        if (leftCount == rightCount)
            for (uint index = 0; index < leftCount + rightCount; ++index) mask.Add((int)index);
        else
        {
            uint smaller = Math.Min(leftCount, rightCount), larger = Math.Max(leftCount, rightCount);
            bool leftSmaller = leftCount < rightCount;
            LlvmValue extend = leftSmaller ? left : right;
            int[] extension = new int[larger];
            for (uint index = 0; index < smaller; ++index) extension[index] = (int)index;
            for (uint index = smaller; index < larger; ++index) extension[index] = -1;
            extend = builder.CreateShuffleVector(extend, LlvmPoisonValue.Get(extend.Type), extension);
            if (leftSmaller) left = extend; else right = extend;
            for (uint index = 0; index < leftCount; ++index) mask.Add((int)index);
            for (uint index = 0; index < rightCount; ++index) mask.Add((int)(larger + index));
        }
        return builder.CreateShuffleVector(left, right, mask);

        void BitcastIfNeeded(ref LlvmValue value, ref LlvmFixedVectorType type)
        {
            if (ReferenceEquals(type.ElementType, elementType)) return;
            uint bits = (uint)(type.NumElements * layout.GetTypeSizeInBits(type.ElementType).GetFixedValue());
            uint count = (uint)(bits / layout.GetTypeSizeInBits(elementType).GetFixedValue());
            type = LlvmFixedVectorType.Get(elementType, count);
            value = builder.CreateBitCast(value, type);
        }
    }
}
