// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// computePointerICmp's same-base offset fold; Value.cpp stripAndAccumulateConstantOffsets.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyIntegerComparePointerCasts(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (depth == 0 || lhs is not LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } cast ||
            cast.Operand.Type is not LlvmPointerType source || cast.Type is not LlvmIntegerType destination ||
            destination.BitWidth != q.Layout.GetAddressSizeInBits(source.AddressSpace)) return null;
        if (rhs is LlvmConstant constant &&
            LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.IntToPtr, constant, source, q.Layout, q.Metrics) is { } ptr)
            return SimplifyIntegerCompare(p, cast.Operand, ptr, q, depth - 1);
        if (rhs is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } right && right.Operand.Type == source)
            return SimplifyIntegerCompare(p, cast.Operand, right.Operand, q, depth - 1);
        return null;
    }

    private static LlvmValue? SimplifyIntegerPointerCompare(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        if (IsIntegerSignedPredicate(p)) return null;
        uint width = q.Layout.GetIndexTypeSizeInBits(lhs.Type);
        var lo = LlvmAPInt.GetZero(width);
        var ro = LlvmAPInt.GetZero(width);
        var lbase = StripIntegerComparePointerOffsets(lhs, ref lo, IsIntegerEquality(p), q);
        var rbase = StripIntegerComparePointerOffsets(rhs, ref ro, IsIntegerEquality(p), q);
        if (lbase != rbase) return null;
        var signed = p is >= LlvmCmpPredicate.ICmpUgt and <= LlvmCmpPredicate.ICmpUle ? (LlvmCmpPredicate)((byte)p + 4) : p;
        Count(q.Metrics, "simplify.icmp.pointer_same_base");
        return CompareBool(lhs, LlvmConstantFolding.CompareInteger(lo, ro, signed));
    }

    private static LlvmValue StripIntegerComparePointerOffsets(LlvmValue value, ref LlvmAPInt offset, bool allowNonInbounds, BinaryQuery q) =>
        LlvmValueTracking.StripAndAccumulateConstantOffsets(value, q.Layout, ref offset, allowNonInbounds, metrics: q.Metrics);
}
