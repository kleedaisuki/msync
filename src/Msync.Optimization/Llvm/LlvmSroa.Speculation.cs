// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Scalar/SROA.cpp findCommonTypeThroughPHIOrSelect and speculation carriers.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal readonly record struct SelectHandSpeculativity(byte Bits = 0)
    {
        internal SelectHandSpeculativity SetAsSpeculatable(bool isTrueValue) =>
            new((byte)(Bits | (isTrueValue ? 1 : 2)));
        internal bool IsSpeculatable(bool isTrueValue) => (Bits & (isTrueValue ? 1 : 2)) != 0;
        internal bool AreAllSpeculatable => IsSpeculatable(true) && IsSpeculatable(false);
        internal bool AreAnySpeculatable => IsSpeculatable(true) || IsSpeculatable(false);
        internal bool AreNoneSpeculatable => !AreAnySpeculatable;
    }

    internal readonly record struct RewriteableMemOp(LlvmInstruction Instruction, SelectHandSpeculativity Spec = default);
    internal sealed class RewriteableMemOps : List<RewriteableMemOp> { }

    internal static (LlvmType? CommonType, LlvmIntegerType? IntegerType) FindCommonType(IEnumerable<Slice> slices, ulong endOffset)
    {
        LlvmType? common = null;
        LlvmIntegerType? largestInteger = null;
        bool commonType = true, first = true;
        ulong firstBegin = 0;
        foreach (Slice slice in slices)
        {
            if (first) { firstBegin = slice.BeginOffset; first = false; }
            LlvmUser user = slice.Use!.User;
            if (user is LlvmCallBase { CalledFunction.IntrinsicDescriptor: not null }) continue;
            if (slice.BeginOffset != firstBegin || slice.EndOffset != endOffset) continue;
            LlvmType? type = user switch
            {
                LlvmLoadInstruction load => load.Type,
                LlvmStoreInstruction store => store.ValueOperand.Type,
                LlvmPhiInstruction or LlvmSelectInstruction => FindCommonTypeThroughPHIOrSelect((LlvmInstruction)user),
                _ => null
            };
            if (type is LlvmIntegerType integer)
            {
                if (integer.BitWidth % 8 != 0 || integer.BitWidth / 8 > endOffset - firstBegin) continue;
                if (largestInteger is null || largestInteger.BitWidth < integer.BitWidth) largestInteger = integer;
            }
            if (type is null || common is not null && common != type) commonType = false;
            else common = type;
        }
        return (commonType ? common : null, largestInteger);
    }

    private static LlvmType? FindCommonTypeThroughPHIOrSelect(LlvmInstruction instruction)
    {
        LlvmType? common = null;
        foreach (LlvmUser user in instruction.Users)
        {
            LlvmType? type = user switch
            {
                LlvmLoadInstruction load => load.Type,
                LlvmStoreInstruction store => store.ValueOperand.Type,
                _ => null
            };
            if (type is null || common is not null && common != type) return null;
            common = type;
        }
        return common;
    }
}
