// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Instructions.h constant-mask adapters.
namespace Msync.Core.Llvm;

public sealed partial class LlvmShuffleVectorInstruction
{
    public static bool IsSingleSourceMask(LlvmConstant mask, int sourceElements) => IsSingleSourceMask(GetShuffleMask(mask), sourceElements);
    public static bool IsIdentityMask(LlvmConstant mask, int sourceElements) => mask.Type is not LlvmScalableVectorType && IsIdentityMask(GetShuffleMask(mask), sourceElements);
    public static bool IsReverseMask(LlvmConstant mask, int sourceElements) => IsReverseMask(GetShuffleMask(mask), sourceElements);
    public static bool IsZeroEltSplatMask(LlvmConstant mask, int sourceElements) => IsZeroEltSplatMask(GetShuffleMask(mask), sourceElements);
    public static bool IsSelectMask(LlvmConstant mask, int sourceElements) => IsSelectMask(GetShuffleMask(mask), sourceElements);
    public static bool IsTransposeMask(LlvmConstant mask, int sourceElements) => IsTransposeMask(GetShuffleMask(mask), sourceElements);
    public static bool IsSpliceMask(LlvmConstant mask, int sourceElements, out int index) => IsSpliceMask(GetShuffleMask(mask), sourceElements, out index);
    public static bool IsExtractSubvectorMask(LlvmConstant mask, int sourceElements, out int index)
    {
        index = -1;
        return mask.Type is not LlvmScalableVectorType && IsExtractSubvectorMask(GetShuffleMask(mask), sourceElements, out index);
    }
    public static bool IsInsertSubvectorMask(LlvmConstant mask, int sourceElements, out int subElements, out int index)
    {
        subElements = 0; index = -1;
        return mask.Type is not LlvmScalableVectorType && IsInsertSubvectorMask(GetShuffleMask(mask), sourceElements, out subElements, out index);
    }
    public static bool IsReplicationMask(LlvmConstant mask, out int replication, out int width)
    {
        replication = 0; width = 0;
        return mask.Type is not LlvmScalableVectorType && IsReplicationMask(GetShuffleMask(mask), out replication, out width);
    }
}
