// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifySelectInst (scalar represented slice).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    public static LlvmValue? SimplifySelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse,
        LlvmFastMathFlags flags, LlvmDataLayout layout, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) =>
        SimplifyScalarSelect(condition, whenTrue, whenFalse, flags, new(layout, context, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals), 3);

    private static LlvmValue? SimplifyScalarSelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse,
        LlvmFastMathFlags flags, BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.select.queries");
        if (condition is LlvmConstant c)
        {
            if (whenTrue is LlvmConstant t && whenFalse is LlvmConstant f &&
                LlvmConstantFolding.ConstantFoldSelectInstruction(c, t, f, q.Metrics) is { } folded) return folded;
            if (c is LlvmPoisonValue) return LlvmPoisonValue.Get(whenTrue.Type);
            if (q.IsUndef(c)) return whenFalse is LlvmConstant ? whenFalse : whenTrue;
            if (IsIntegerOne(c)) return whenTrue;
            if (IsIntegerZero(c)) return whenFalse;
        }
        if (condition.Type == whenTrue.Type && SimplifyScalarBooleanSelect(condition, whenTrue, whenFalse) is { } logical)
            return logical;
        if (whenTrue == whenFalse) return whenTrue;
        if (condition == whenTrue)
        {
            if (IsIntegerZero(whenFalse)) return condition;
            if (IsIntegerOne(whenFalse)) return LlvmConstantInt.GetTrue(condition.Context);
        }
        if (condition == whenFalse)
        {
            if (IsIntegerOne(whenTrue)) return condition;
            if (IsIntegerZero(whenTrue)) return LlvmConstantInt.GetFalse(condition.Context);
        }
        if (whenTrue is LlvmPoisonValue || q.IsUndef(whenTrue) && LlvmValueTracking.ImpliesPoison(whenFalse, condition, metrics: q.Metrics))
            return whenFalse;
        if (whenFalse is LlvmPoisonValue || q.IsUndef(whenFalse) && LlvmValueTracking.ImpliesPoison(whenTrue, condition, metrics: q.Metrics))
            return whenTrue;
        if (SimplifySelectICmp(condition, whenTrue, whenFalse, q, depth) is { } integer) return integer;
        if (LlvmCmpInstAnalysis.DecomposeBitTest(condition, metrics: q.Metrics) is { } test &&
            SimplifySelectBitTest(whenTrue, whenFalse, test.X, test.Mask, test.Pred == LlvmCmpPredicate.ICmpEq) is { } bitTest)
            return bitTest;
        if (SimplifySelectFCmp(condition, whenTrue, whenFalse, flags, q, depth) is { } floating) return floating;
        if (IsImpliedByDomCondition(condition, q) is { } implied) return implied ? whenTrue : whenFalse;
        if (whenTrue is LlvmPhiInstruction tPhi && whenFalse is LlvmPhiInstruction fPhi)
        {
            if (IsSelectWithIdenticalPhi(tPhi, fPhi)) return whenFalse;
            if (IsSelectWithIdenticalPhi(fPhi, tPhi)) return whenTrue;
        }
        return null;
    }

    private static LlvmValue? SimplifyScalarBooleanSelect(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse)
    {
        if (IsIntegerOne(whenTrue) && IsIntegerZero(whenFalse)) return condition;
        if (BitwiseLogicalPair(condition, true, whenTrue, whenFalse)) return whenFalse;
        if (BitwiseLogicalPair(condition, false, whenTrue, whenFalse)) return whenTrue;
        if (IsIntegerZero(whenTrue) && BitwiseLogicalContains(condition, false, whenFalse)) return IntegerZero(condition);
        if (IsIntegerZero(whenFalse))
        {
            if (BitwiseNotOperand(condition) is { } notCondition && BitwiseLogicalContains(notCondition, false, whenTrue)) return IntegerZero(condition);
            if (BitwiseNotOperand(whenTrue) is { } notTrue && BitwiseLogicalContains(notTrue, false, condition)) return IntegerZero(condition);
            if (BitwiseLogicalContains(condition, false, whenTrue)) return whenTrue;
            if (BitwiseLogicalContains(whenTrue, false, condition)) return condition;
            if (BitwiseLogicalNotArm(condition, false, out _, out var y, out var x, false) && BitwiseLogicalPair(whenTrue, false, x, y)) return x;
            if (BitwiseLogicalNotArm(whenTrue, false, out _, out y, out x, false) && BitwiseLogicalPair(condition, false, x, y)) return x;
        }
        if (IsIntegerOne(whenTrue))
        {
            if (BitwiseNotOperand(condition) is { } notCondition && BitwiseLogicalContains(notCondition, true, whenFalse)) return IntegerAllOnes(condition);
            if (BitwiseNotOperand(whenFalse) is { } notFalse && BitwiseLogicalContains(notFalse, true, condition)) return IntegerAllOnes(condition);
            if (BitwiseLogicalContains(condition, true, whenFalse)) return whenFalse;
            if (BitwiseLogicalContains(whenFalse, true, condition)) return condition;
        }
        return null;
    }

    private static bool BitwiseLogicalContains(LlvmValue value, bool isAnd, LlvmValue operand) =>
        BitwiseLogicalOperands(value, isAnd, out var lhs, out var rhs) && (lhs == operand || rhs == operand);

    private static bool IsSelectWithIdenticalPhi(LlvmPhiInstruction phi, LlvmPhiInstruction identical)
    {
        if (phi.Parent != identical.Parent || phi.IncomingCount != 2) return false;
        int differences = 0;
        LlvmBasicBlock? differenceBlock = null;
        for (int index = 0; index < 2; ++index)
        {
            LlvmBasicBlock predecessor = phi.GetIncomingBlock(index);
            if (phi.GetIncomingValue(index) == identical.GetIncomingValue(identical.GetBasicBlockIndex(predecessor))) continue;
            ++differences;
            differenceBlock = predecessor;
        }
        if (differences != 1) return false;
        if (phi.GetIncomingValue(phi.GetBasicBlockIndex(differenceBlock!)) is not LlvmSelectInstruction select ||
            identical.GetIncomingValue(identical.GetBasicBlockIndex(differenceBlock!)) is not LlvmSelectInstruction identicalSelect ||
            select.Condition != identicalSelect.Condition) return false;
        LlvmValue other, identicalOther;
        if (select.TrueValue == identicalSelect.TrueValue) { other = select.FalseValue; identicalOther = identicalSelect.FalseValue; }
        else if (select.FalseValue == identicalSelect.FalseValue) { other = select.TrueValue; identicalOther = identicalSelect.TrueValue; }
        else return false;
        return other is LlvmSelectInstruction nested && identicalOther == identical &&
            (nested.TrueValue == identical && nested.FalseValue == phi || nested.TrueValue == phi && nested.FalseValue == identical);
    }
}
