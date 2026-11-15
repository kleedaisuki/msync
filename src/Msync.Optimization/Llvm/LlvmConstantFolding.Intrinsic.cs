// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// ConstantFoldIntrinsic/ConstantFoldScalarCall1/IntrinsicCall2/ScalarCall3.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmConstant> operands,
        LlvmType type, Dictionary<string, ulong>? metrics = null, LlvmCallBase? call = null, LlvmFunction? contextFunction = null)
    {
        Count(metrics, "constant_fold.intrinsic.queries");
        if (call?.IsNoBuiltin == true || !CanConstantFoldIntrinsic(kind, call?.IsStrictFP ?? contextFunction?.StrictFP ?? true))
        { Count(metrics, "constant_fold.intrinsic.source_gate"); return null; }
        if (type is LlvmStructType aggregate && (kind == LlvmIntrinsicKind.Frexp || kind == LlvmIntrinsicKind.SinCos))
            return FoldFloatingAggregate(kind, operands, aggregate, metrics, call);
        if (type is LlvmFixedVectorType vector) return FoldFixedVectorIntrinsic(kind, operands, vector, metrics, call);
        return FoldScalarIntrinsic(kind, operands, type, metrics, call);
    }

    private static LlvmConstant? FoldScalarIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmConstant> operands,
        LlvmType type, Dictionary<string, ulong>? metrics, LlvmCallBase? call)
    {
        if (LlvmIntrinsic.IsFloatingIntrinsic(kind)) return ConstantFoldFloatingIntrinsic(kind, operands, type, metrics, call);
        if (LlvmIntrinsic.IsMemoryFamilyIntrinsic(kind)) return ConstantFoldMemoryIntrinsic(kind, operands, call, metrics);
        LlvmIntrinsicDescriptor descriptor = LlvmIntrinsic.GetDescriptor(kind);
        if (descriptor.PropagatesPoison && operands.Any(value => value is LlvmPoisonValue))
        { Count(metrics, "constant_fold.intrinsic.poison"); return LlvmPoisonValue.Get(type); }
        if (FoldIntegerIntrinsic(kind, operands, type, metrics) is { } integerFold) return integerFold;
        if (type is not LlvmIntegerType integer) return null;
        if (operands.Any(value => value is not (LlvmConstantInt or LlvmUndefValue))) return null;
        if (kind == LlvmIntrinsicKind.CTPop)
        {
            if (operands.Count != 1) return null;
            if (operands[0] is LlvmUndefValue) return LlvmConstantInt.Get(integer, 0);
            if (operands[0] is not LlvmConstantInt bits) return null;
            Count(metrics, "constant_fold.intrinsic.ctpop");
            return LlvmConstantInt.Get(integer, bits.Value.PopCount());
        }
        if (kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr)
            return FoldFunnelShift(operands, integer, kind == LlvmIntrinsicKind.FShr, metrics);
        if (operands.Count != 2) return null;
        var left = operands[0] as LlvmConstantInt;
        var right = operands[1] as LlvmConstantInt;
        if (kind == LlvmIntrinsicKind.CTTZ)
        {
            if (right is null) return null;
            Count(metrics, "constant_fold.intrinsic.cttz");
            if (right.Value.IsOne && (left is null || left.Value.IsZero)) return LlvmPoisonValue.Get(type);
            return LlvmConstantInt.Get(integer, left?.Value.CountTrailingZeros() ?? 0);
        }
        if (kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp)
        {
            Count(metrics, "constant_fold.intrinsic.cmp");
            if (left is null || right is null) return LlvmConstantInt.Get(integer, 0);
            int result = kind == LlvmIntrinsicKind.SCmp
                ? left.Value.Sgt(right.Value) ? 1 : left.Value.Slt(right.Value) ? -1 : 0
                : left.Value.Ugt(right.Value) ? 1 : left.Value.Ult(right.Value) ? -1 : 0;
            return LlvmConstantInt.Get(type.Context, new LlvmAPInt(integer.BitWidth, unchecked((ulong)(long)result), isSigned: true));
        }
        if (!LlvmIntrinsic.IsMinMax(kind)) return null;
        if (left is null || right is null)
        {
            Count(metrics, "constant_fold.intrinsic.undef_saturation");
            return LlvmConstantInt.Get(type.Context, LlvmIntrinsic.GetSaturationPoint(kind, integer.BitWidth));
        }
        Count(metrics, "constant_fold.intrinsic.minmax");
        bool chooseLeft = LlvmIntrinsic.GetMinMaxPredicate(kind) switch
        {
            LlvmCmpPredicate.ICmpSlt => left.Value.Slt(right.Value), LlvmCmpPredicate.ICmpSgt => left.Value.Sgt(right.Value),
            LlvmCmpPredicate.ICmpUlt => left.Value.Ult(right.Value), _ => left.Value.Ugt(right.Value)
        };
        return LlvmConstantInt.Get(type.Context, chooseLeft ? left.Value : right.Value);
    }

    private static LlvmConstant? FoldFunnelShift(IReadOnlyList<LlvmConstant> operands, LlvmIntegerType type, bool rightShift,
        Dictionary<string, ulong>? metrics)
    {
        if (operands.Count != 3) return null;
        var left = operands[0] as LlvmConstantInt;
        var right = operands[1] as LlvmConstantInt;
        var shift = operands[2] as LlvmConstantInt;
        Count(metrics, rightShift ? "constant_fold.intrinsic.fshr" : "constant_fold.intrinsic.fshl");
        if (shift is null) return operands[rightShift ? 1 : 0];
        if (left is null && right is null) return LlvmUndefValue.Get(type);
        uint width = shift.BitWidth;
        uint amount = (uint)shift.Value.URem(new LlvmAPInt(width, width)).GetZExtValue();
        if (amount == 0) return operands[rightShift ? 1 : 0];
        uint leftAmount = rightShift ? width - amount : amount;
        uint rightAmount = rightShift ? amount : width - amount;
        if (left is null) return LlvmConstantInt.Get(type.Context, right!.Value.LShr(rightAmount));
        if (right is null) return LlvmConstantInt.Get(type.Context, left.Value.Shl(leftAmount));
        return LlvmConstantInt.Get(type.Context, left.Value.Shl(leftAmount) | right.Value.LShr(rightAmount));
    }
}
