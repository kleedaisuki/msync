// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp
// simplifyIntrinsic, simplifyUnaryIntrinsic/ctpop, simplifyBinaryIntrinsic/cttz/scmp/ucmp, fshl.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyScalarIntrinsic(LlvmIntrinsicKind kind, LlvmType resultType,
        IReadOnlyList<LlvmValue> arguments, BinaryQuery q)
    {
        Count(q.Metrics, "simplify.intrinsic.queries");
        if (LlvmIntrinsic.GetDescriptor(kind).PropagatesPoison && arguments.Any(value => value is LlvmPoisonValue))
        { Count(q.Metrics, "simplify.intrinsic.poison"); return LlvmPoisonValue.Get(resultType); }
        if (arguments.All(value => value is LlvmConstant) && LlvmConstantFolding.ConstantFoldIntrinsic(kind,
            arguments.Cast<LlvmConstant>().ToArray(), resultType, q.Metrics) is { } folded)
        { Count(q.Metrics, "simplify.intrinsic.constant"); return folded; }
        if (LlvmIntrinsic.IsMinMax(kind)) return SimplifyMinMaxIntrinsic(kind, arguments[0], arguments[1], q);
        if (kind == LlvmIntrinsicKind.CTPop)
        {
            LlvmValue input = arguments[0];
            if (BitwisePowerOfTwo(input, false, q))
            { Count(q.Metrics, "simplify.intrinsic.ctpop.power_of_two"); return LlvmConstantInt.Get((LlvmIntegerType)resultType, 1); }
            uint width = input.Type.GetIntegerBitWidth();
            if (LlvmKnownBitsAnalysis.MaskedValueIsZero(input, LlvmAPInt.GetHighBitsSet(width, width - 1),
                context: q.Context, tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext))
            { Count(q.Metrics, "simplify.intrinsic.ctpop.low_bit"); return input; }
            return null;
        }
        if (kind == LlvmIntrinsicKind.CTTZ)
        {
            if (arguments[0] is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt one } shift && one.Value.IsOne)
            { Count(q.Metrics, "simplify.intrinsic.cttz.shift_one"); return shift.Right; }
            return null;
        }
        if (kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp)
            return SimplifyThreeWayCompare(kind, resultType, arguments[0], arguments[1], q);
        if (kind == LlvmIntrinsicKind.FShl) return SimplifyFunnelShiftLeft(resultType, arguments, q);
        return null;
    }

    private static LlvmValue? SimplifyThreeWayCompare(LlvmIntrinsicKind kind, LlvmType resultType,
        LlvmValue left, LlvmValue right, BinaryQuery q)
    {
        if (IsIntegerCmpTrue(LlvmCmpPredicate.ICmpEq, left, right, q, 3))
        { Count(q.Metrics, "simplify.intrinsic.cmp.equal"); return LlvmConstant.GetNullValue(resultType); }
        bool signed = kind == LlvmIntrinsicKind.SCmp;
        if (IsIntegerCmpTrue(signed ? LlvmCmpPredicate.ICmpSgt : LlvmCmpPredicate.ICmpUgt, left, right, q, 3))
        { Count(q.Metrics, "simplify.intrinsic.cmp.greater"); return LlvmConstantInt.Get((LlvmIntegerType)resultType, 1); }
        if (IsIntegerCmpTrue(signed ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt, left, right, q, 3))
        { Count(q.Metrics, "simplify.intrinsic.cmp.less"); return LlvmConstantInt.Get(resultType.Context, LlvmAPInt.GetAllOnes(resultType.GetIntegerBitWidth())); }
        return null;
    }

    private static LlvmValue? SimplifyFunnelShiftLeft(LlvmType resultType, IReadOnlyList<LlvmValue> arguments, BinaryQuery q)
    {
        LlvmValue left = arguments[0], right = arguments[1], shift = arguments[2];
        if (q.IsUndef(left) && q.IsUndef(right))
        { Count(q.Metrics, "simplify.intrinsic.fshl.undef_inputs"); return LlvmUndefValue.Get(resultType); }
        if (q.IsUndef(shift))
        { Count(q.Metrics, "simplify.intrinsic.fshl.undef_shift"); return left; }
        if (shift is LlvmConstantInt amount)
        {
            var width = new LlvmAPInt(amount.BitWidth, amount.BitWidth);
            LlvmAPInt reduced = amount.Value.URem(width);
            if (reduced.IsZero)
            { Count(q.Metrics, "simplify.intrinsic.fshl.zero_shift"); return left; }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt c1 } first &&
                right is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt c2 } second &&
                first.Left == second.Left && c1.Value + c2.Value == width && reduced == c1.Value)
            { Count(q.Metrics, "simplify.intrinsic.fshl.complementary_shifts"); return first.Left; }
        }
        if (IsIntegerZero(left) && IsIntegerZero(right))
        { Count(q.Metrics, "simplify.intrinsic.fshl.zero_rotate"); return LlvmConstant.GetNullValue(resultType); }
        if (IsIntegerAllOnes(left) && IsIntegerAllOnes(right))
        { Count(q.Metrics, "simplify.intrinsic.fshl.ones_rotate"); return LlvmConstantInt.Get(resultType.Context, LlvmAPInt.GetAllOnes(resultType.GetIntegerBitWidth())); }
        return null;
    }
}
