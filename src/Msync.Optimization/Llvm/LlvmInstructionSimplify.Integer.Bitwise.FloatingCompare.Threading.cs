// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp same-compare/select-case helpers and
// threadCmpOverSelect / threadCmpOverPHI (scalar result).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyScalarCompare(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth) => (byte)predicate >= 32
            ? SimplifyIntegerCompare(predicate, lhs, rhs, q, depth)
            : SimplifyFloatingCompare(predicate, lhs, rhs, LlvmFastMathFlags.None, q, depth);

    private static bool ScalarSameCompare(LlvmValue value, LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs) =>
        value is LlvmCmpInstruction compare &&
        (compare.Predicate == predicate && compare.Left == lhs && compare.Right == rhs ||
         compare.Predicate == LlvmCmpInstruction.GetSwappedPredicate(predicate) && compare.Left == rhs && compare.Right == lhs);

    private static LlvmValue? ScalarCmpSelectCase(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        LlvmValue condition, bool whenTrue, BinaryQuery q, uint depth)
    {
        var result = SimplifyScalarCompare(predicate, lhs, rhs, q, depth);
        if (result == condition || result is null && ScalarSameCompare(condition, predicate, lhs, rhs))
            return LlvmConstantInt.GetBool(condition.Context, whenTrue);
        return result;
    }

    private static LlvmValue? ScalarCmpSelect(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.compare.select_thread");
        if (depth == 0) return null;
        --depth;
        if (lhs is not LlvmSelectInstruction)
        { (lhs, rhs) = (rhs, lhs); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        var select = (LlvmSelectInstruction)lhs;
        var trueCompare = ScalarCmpSelectCase(predicate, select.TrueValue, rhs, select.Condition, true, q, depth);
        if (trueCompare is null) return null;
        var falseCompare = ScalarCmpSelectCase(predicate, select.FalseValue, rhs, select.Condition, false, q, depth);
        if (falseCompare is null) return null;
        if (trueCompare == falseCompare) return trueCompare;
        return ScalarOtherCmpSelect(trueCompare, falseCompare, select.Condition, q, depth);
    }

    private static LlvmValue? ScalarOtherCmpSelect(LlvmValue whenTrue, LlvmValue whenFalse, LlvmValue condition,
        BinaryQuery q, uint depth)
    {
        if (IsIntegerZero(whenFalse) && LlvmValueTracking.ImpliesPoison(whenTrue, condition, metrics: q.Metrics) &&
            IntegerBin(LlvmOpcode.And, condition, whenTrue, q, depth) is { } and) return and;
        if (IsIntegerOne(whenTrue) && LlvmValueTracking.ImpliesPoison(whenFalse, condition, metrics: q.Metrics) &&
            IntegerBin(LlvmOpcode.Or, condition, whenFalse, q, depth) is { } or) return or;
        if (IsIntegerOne(whenFalse) && IsIntegerZero(whenTrue))
            return IntegerBin(LlvmOpcode.Xor, condition, IntegerAllOnes(condition), q, depth);
        return null;
    }

    private static LlvmValue? ScalarCmpPhi(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.compare.phi_thread");
        if (depth == 0) return null;
        --depth;
        if (lhs is not LlvmPhiInstruction)
        { (lhs, rhs) = (rhs, lhs); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        var phi = (LlvmPhiInstruction)lhs;
        if (!ValueDominatesPhi(rhs, phi, q.Tree)) return null;
        LlvmValue? common = null;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            LlvmValue incoming = phi.GetIncomingValue(index);
            var edge = phi.GetIncomingBlock(index).GetTerminator();
            if (incoming == phi) continue;
            var value = SimplifyScalarCompare(predicate, incoming, rhs, q with { Context = edge }, depth);
            if (value is null || common is not null && value != common) return null;
            common = value;
        }
        return common;
    }
}
