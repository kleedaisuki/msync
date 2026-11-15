// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantRange.cpp unsignedMulMayOverflow.
namespace Msync.Core.Llvm;
public sealed partial record LlvmConstantRange
{
    public LlvmOverflowResult UnsignedMulMayOverflow(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return LlvmOverflowResult.MayOverflow;
        _ = GetUnsignedMin().UMulOverflow(other.GetUnsignedMin(), out bool overflow);
        if (overflow) return LlvmOverflowResult.AlwaysOverflowsHigh;
        _ = GetUnsignedMax().UMulOverflow(other.GetUnsignedMax(), out overflow);
        return overflow ? LlvmOverflowResult.MayOverflow : LlvmOverflowResult.NeverOverflows;
    }
}
