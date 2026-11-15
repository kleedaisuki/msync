// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isTruePredicate and isImpliedCondOperands, scalar non-intrinsic cases.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static bool ImpliedIntegerCompareOperands(LlvmCmpPredicate p, LlvmValue a0, LlvmValue a1, LlvmValue b0, LlvmValue b1) => p switch
    {
        LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle =>
            TrueIntegerPredicate(LlvmCmpPredicate.ICmpSle, b0, a0) && TrueIntegerPredicate(LlvmCmpPredicate.ICmpSle, a1, b1),
        LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge =>
            TrueIntegerPredicate(LlvmCmpPredicate.ICmpSle, a0, b0) && TrueIntegerPredicate(LlvmCmpPredicate.ICmpSle, b1, a1),
        LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle =>
            TrueIntegerPredicate(LlvmCmpPredicate.ICmpUle, b0, a0) && TrueIntegerPredicate(LlvmCmpPredicate.ICmpUle, a1, b1),
        LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge =>
            TrueIntegerPredicate(LlvmCmpPredicate.ICmpUle, a0, b0) && TrueIntegerPredicate(LlvmCmpPredicate.ICmpUle, b1, a1),
        _ => false
    };

    private static bool TrueIntegerPredicate(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs)
    {
        if (IntegerTrueWhenEqual(p) && lhs == rhs) return true;
        var l = lhs as LlvmBinaryOperator;
        var r = rhs as LlvmBinaryOperator;
        if (p == LlvmCmpPredicate.ICmpSle)
        {
            if (r is { Left: var x, Right: LlvmConstantInt c } && x == lhs &&
                (r.Opcode == LlvmOpcode.Add && r.HasNoSignedWrap || r.Opcode == LlvmOpcode.Or)) return !c.Value.IsNegative;
            return l is { Opcode: LlvmOpcode.Add, HasNoSignedWrap: true, Right: LlvmConstantInt lc } &&
                r is { Opcode: LlvmOpcode.Add, HasNoSignedWrap: true, Right: LlvmConstantInt rc } &&
                l.Left == r.Left && lc.Value.Sle(rc.Value);
        }
        if (p != LlvmCmpPredicate.ICmpUle) return false;
        if (r is not null && (r.Left == lhs || r.Right == lhs) &&
            (r.Opcode == LlvmOpcode.Add && r.HasNoUnsignedWrap || r.Opcode == LlvmOpcode.Or)) return true;
        if (l is { Opcode: LlvmOpcode.LShr } && l.Left == rhs) return true;
        if (l is { Opcode: LlvmOpcode.UDiv, Right: LlvmConstantInt divisor } && l.Left == rhs &&
            !divisor.IsZero && !divisor.IsOne) return true;
        if (l is { Opcode: LlvmOpcode.And } && (l.Left == rhs || l.Right == rhs)) return true;
        return l is { Opcode: LlvmOpcode.Add, HasNoUnsignedWrap: true, Right: LlvmConstantInt a } &&
            r is { Opcode: LlvmOpcode.Add, HasNoUnsignedWrap: true, Right: LlvmConstantInt b } &&
            l.Left == r.Left && a.Value.Ule(b.Value);
    }
}
