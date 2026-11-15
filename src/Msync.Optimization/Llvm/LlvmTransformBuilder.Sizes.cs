// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IRBuilder.h CreateVScale; IRBuilder.cpp CreateVScaleMultiple,
// CreateElementCount and CreateTypeSize.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmValue CreateVScale(LlvmType type, string name = "") =>
        CreateIntrinsic(LlvmIntrinsicKind.VScale, [], (LlvmIntegerType)type, name);

    public LlvmValue CreateElementCount(LlvmType type, LlvmElementCount count)
    {
        Count("transform_builder.element_count.queries");
        return count.IsFixed || count.IsZero
            ? LlvmConstantInt.Get((LlvmIntegerType)type, count.KnownMinValue)
            : CreateVScaleMultiple(type, count.KnownMinValue);
    }

    public LlvmValue CreateTypeSize(LlvmType type, LlvmTypeSize size)
    {
        Count("transform_builder.type_size.queries");
        return size.IsFixed || size.IsZero
            ? LlvmConstantInt.Get((LlvmIntegerType)type, size.KnownMinValue)
            : CreateVScaleMultiple(type, size.KnownMinValue);
    }

    private LlvmValue CreateVScaleMultiple(LlvmType type, ulong scale)
    {
        LlvmValue value = CreateVScale(type);
        if (scale == 1) return value;
        return CreateMul(value, LlvmConstantInt.Get((LlvmIntegerType)type, scale), nuw: true);
    }
}
