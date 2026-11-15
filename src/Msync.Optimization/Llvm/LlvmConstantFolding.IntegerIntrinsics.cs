// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// ConstantFoldScalarCall1 and ConstantFoldIntrinsicCall2 integer source cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    private static LlvmConstant? FoldIntegerIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmConstant> arguments,
        LlvmType type, Dictionary<string, ulong>? metrics)
    {
        if (kind == LlvmIntrinsicKind.BSwap || kind == LlvmIntrinsicKind.BitReverse)
        {
            if (arguments[0] is LlvmUndefValue) return arguments[0];
            if (arguments[0] is not LlvmConstantInt bits) return null;
            Count(metrics, "constant_fold.intrinsic.bit_permutation");
            return LlvmConstantInt.Get(type.Context, kind == LlvmIntrinsicKind.BSwap ? bits.Value.ByteSwap() : bits.Value.ReverseBits());
        }
        if (arguments.Count != 2 || arguments.Any(value => value is not (LlvmConstantInt or LlvmUndefValue))) return null;
        var left = arguments[0] as LlvmConstantInt;
        var right = arguments[1] as LlvmConstantInt;
        if (LlvmIntrinsic.IsOverflowIntrinsic(kind)) return FoldOverflowIntrinsic(kind, left, right, (LlvmStructType)type, metrics);
        if (type is not LlvmIntegerType integer) return null;
        if (kind == LlvmIntrinsicKind.SMulH || kind == LlvmIntrinsicKind.UMulH)
        {
            Count(metrics, "constant_fold.intrinsic.multiply_high");
            if (left is null || right is null) return LlvmConstant.GetNullValue(type);
            uint width = integer.BitWidth;
            LlvmAPInt lhs = kind == LlvmIntrinsicKind.SMulH ? left.Value.SExt(width * 2) : left.Value.ZExt(width * 2);
            LlvmAPInt rhs = kind == LlvmIntrinsicKind.SMulH ? right.Value.SExt(width * 2) : right.Value.ZExt(width * 2);
            LlvmAPInt product = lhs * rhs;
            return LlvmConstantInt.Get(type.Context, product.ExtractBits(width, width));
        }
        if (kind == LlvmIntrinsicKind.Abs || kind == LlvmIntrinsicKind.CTLZ)
        {
            if (right is null) return null;
            Count(metrics, "constant_fold.intrinsic.abs_or_ctlz");
            bool poison = right.IsOne && (left is null || (kind == LlvmIntrinsicKind.Abs ? left.Value.IsMinSignedValue : left.IsZero));
            if (poison) return LlvmPoisonValue.Get(type);
            if (left is null) return LlvmConstant.GetNullValue(type);
            return kind == LlvmIntrinsicKind.Abs
                ? LlvmConstantInt.Get(type.Context, left.Value.IsNegative ? -left.Value : left.Value)
                : LlvmConstantInt.Get(integer, left.Value.CountLeadingZeros());
        }
        if (kind == LlvmIntrinsicKind.UAddSat || kind == LlvmIntrinsicKind.SAddSat)
        {
            Count(metrics, "constant_fold.intrinsic.saturating_add");
            if (left is null || right is null) return LlvmConstantInt.Get(type.Context, LlvmAPInt.GetAllOnes(integer.BitWidth));
            return LlvmConstantInt.Get(type.Context, kind == LlvmIntrinsicKind.UAddSat
                ? left.Value.UAddSat(right.Value) : left.Value.SAddSat(right.Value));
        }
        if (kind == LlvmIntrinsicKind.USubSat || kind == LlvmIntrinsicKind.SSubSat)
        {
            Count(metrics, "constant_fold.intrinsic.saturating_sub");
            if (left is null || right is null) return LlvmConstant.GetNullValue(type);
            return LlvmConstantInt.Get(type.Context, kind == LlvmIntrinsicKind.USubSat
                ? left.Value.USubSat(right.Value) : left.Value.SSubSat(right.Value));
        }
        if (kind == LlvmIntrinsicKind.UShlSat || kind == LlvmIntrinsicKind.SShlSat)
        {
            if (left is null || right is null) return null;
            Count(metrics, "constant_fold.intrinsic.saturating_shift");
            if (right.Value.Uge(new LlvmAPInt(right.BitWidth, right.BitWidth))) return LlvmPoisonValue.Get(type);
            return LlvmConstantInt.Get(type.Context, kind == LlvmIntrinsicKind.UShlSat
                ? left.Value.UShlSat(right.Value) : left.Value.SShlSat(right.Value));
        }
        return null;
    }

    private static LlvmConstant FoldOverflowIntrinsic(LlvmIntrinsicKind kind, LlvmConstantInt? left, LlvmConstantInt? right,
        LlvmStructType type, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "constant_fold.intrinsic.with_overflow");
        if (left is null || right is null)
        {
            if (kind != LlvmIntrinsicKind.SAddWithOverflow && kind != LlvmIntrinsicKind.UAddWithOverflow)
                return LlvmConstant.GetNullValue(type);
            return LlvmConstantStruct.Get(type,
                [LlvmConstantInt.Get(type.Context, LlvmAPInt.GetAllOnes(type.Elements[0].GetIntegerBitWidth())),
                 LlvmConstantInt.GetFalse(type.Context)]);
        }
        bool overflow;
        LlvmAPInt result;
        if (kind == LlvmIntrinsicKind.UAddWithOverflow) result = left.Value.UAddOverflow(right.Value, out overflow);
        else if (kind == LlvmIntrinsicKind.SAddWithOverflow) result = left.Value.SAddOverflow(right.Value, out overflow);
        else if (kind == LlvmIntrinsicKind.USubWithOverflow) result = left.Value.USubOverflow(right.Value, out overflow);
        else if (kind == LlvmIntrinsicKind.SSubWithOverflow) result = left.Value.SSubOverflow(right.Value, out overflow);
        else if (kind == LlvmIntrinsicKind.UMulWithOverflow) result = left.Value.UMulOverflow(right.Value, out overflow);
        else result = left.Value.SMulOverflow(right.Value, out overflow);
        return LlvmConstantStruct.Get(type,
            [LlvmConstantInt.Get(type.Context, result), LlvmConstantInt.GetBool(type.Context, overflow)]);
    }
}
