// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp
// Constant::toConstantRange. A vector range describes every nonpoison lane.
namespace Msync.Core.Llvm;

public abstract partial class LlvmConstant
{
    public LlvmConstantRange ToConstantRange()
    {
        if (this is LlvmConstantInt integer) return new(integer.Value);
        uint width = Type.GetScalarSizeInBits();
        if (Type is not LlvmVectorType) return LlvmConstantRange.Full(width);
        var splat = GetSplatValue(allowPoison: true);
        if (splat is LlvmConstantInt splatInteger) return new(splatInteger.Value);
        if (splat is LlvmConstantByte splatByte) return new(splatByte.Value);
        var range = LlvmConstantRange.Empty(width);
        if (this is LlvmConstantDataVector data)
        {
            for (uint index = 0; index < data.NumElements; ++index)
                range = range.UnionWith(new(data.GetElementAsAPInt(index)));
            return range;
        }
        if (this is LlvmConstantVector vector)
        {
            for (int index = 0; index < vector.OperandCount; ++index)
            {
                var element = vector.GetOperand(index);
                if (element is null) return LlvmConstantRange.Full(width);
                if (element is LlvmPoisonValue) continue;
                if (element is LlvmConstantInt item) range = range.UnionWith(new(item.Value));
                else if (element is LlvmConstantByte itemByte) range = range.UnionWith(new(itemByte.Value));
                else return LlvmConstantRange.Full(width);
            }
            return range;
        }
        return LlvmConstantRange.Full(width);
    }
}
