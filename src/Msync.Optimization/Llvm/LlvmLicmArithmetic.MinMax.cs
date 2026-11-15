// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp hoistMinMax;
// Instructions.cpp CmpPredicate::getMatching; PatternMatch.h logical operators.
using Msync.Core.Llvm;
using P = Msync.Core.Llvm.LlvmCmpPredicate;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicmArithmetic
{
    private readonly record struct InvariantCompare(P Predicate, bool SameSign, LlvmValue Variant, LlvmValue Invariant);
    private static bool HoistMinMax(LlvmInstruction instruction, in Query query)
    {
        bool inverse;
        LlvmValue first, second;
        if (LogicalOperands(instruction, false, out first, out second)) inverse = true;
        else if (LogicalOperands(instruction, true, out first, out second)) inverse = false;
        else return false;
        var lhs = MatchInvariantCompare(first, inverse, query);
        var rhs = MatchInvariantCompare(second, inverse, query);
        if (lhs is not { } left || rhs is not { } right || !ReferenceEquals(left.Variant, right.Variant)) return false;
        var matching = MatchingPredicate(left, right);
        if (matching is not { } predicate) return false;
        bool min = predicate is P.ICmpSlt or P.ICmpSle or P.ICmpUlt or P.ICmpUle;
        bool signed = Signed(predicate);
        var kind = signed ? min ? LlvmIntrinsicKind.SMin : LlvmIntrinsicKind.SMax
                          : min ? LlvmIntrinsicKind.UMin : LlvmIntrinsicKind.UMax;
        LlvmValue rightInvariant = right.Invariant;
        if (instruction is LlvmSelectInstruction)
        {
            var freeze = new LlvmFreezeInstruction(rightInvariant) { Source = instruction.Source };
            freeze.SetName(rightInvariant.Name + ".fr");
            query.InsertPoint.Parent!.InsertBefore(freeze, query.InsertPoint);
            rightInvariant = freeze;
            Count(query, "minmax.poison_blocking_operand_frozen");
        }
        var invariant = LlvmIntrinsic.CreateBinary(kind, left.Invariant, rightInvariant, query.Loop.Header.Parent!.Parent!, query.Metrics);
        invariant.SetName("invariant." + (signed ? "s" : "u") + (min ? "min" : "max"));
        invariant.Source = instruction.Source;
        query.InsertPoint.Parent!.InsertBefore(invariant, query.InsertPoint);
        if (inverse) predicate = LlvmCmpInstruction.GetInversePredicate(predicate);
        var result = new LlvmICmpInstruction(predicate, left.Variant, invariant) { Source = instruction.Source };
        instruction.Parent!.InsertBefore(result, instruction);
        result.TakeName(instruction);
        instruction.ReplaceAllUsesWith(result);
        query.Erase(instruction);
        query.Erase((LlvmInstruction)first);
        query.Erase((LlvmInstruction)second);
        return true;
    }

    private static InvariantCompare? MatchInvariantCompare(LlvmValue value, bool inverse, in Query query)
    {
        if (value is not LlvmICmpInstruction compare || !compare.HasOneUse || !compare.Left.Type.IsIntegerTy() ||
            compare.Predicate is P.ICmpEq or P.ICmpNe) return null;
        P predicate = compare.Predicate;
        LlvmValue variant = compare.Left, invariant = compare.Right;
        if (query.Loop.IsLoopInvariant(variant))
        { (variant, invariant) = (invariant, variant); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        if (query.Loop.IsLoopInvariant(variant) || !query.Loop.IsLoopInvariant(invariant)) return null;
        if (inverse) predicate = LlvmCmpInstruction.GetInversePredicate(predicate);
        return new(predicate, compare.SameSign, variant, invariant);
    }
    private static P? MatchingPredicate(InvariantCompare first, InvariantCompare second)
    {
        if (first.Predicate == second.Predicate) return first.Predicate;
        if (first.SameSign && first.Predicate == FlipSignedness(second.Predicate)) return second.Predicate;
        if (second.SameSign && second.Predicate == FlipSignedness(first.Predicate)) return first.Predicate;
        return null;
    }
    private static P FlipSignedness(P predicate) => predicate switch
    {
        P.ICmpSlt => P.ICmpUlt, P.ICmpSle => P.ICmpUle, P.ICmpSgt => P.ICmpUgt, P.ICmpSge => P.ICmpUge,
        P.ICmpUlt => P.ICmpSlt, P.ICmpUle => P.ICmpSle, P.ICmpUgt => P.ICmpSgt, P.ICmpUge => P.ICmpSge,
        _ => predicate
    };
    private static bool LogicalOperands(LlvmInstruction instruction, bool and, out LlvmValue left, out LlvmValue right)
    {
        left = right = null!;
        if (!instruction.Type.IsIntegerTy(1)) return false;
        if (instruction is LlvmBinaryOperator binary && binary.Opcode == (and ? LlvmOpcode.And : LlvmOpcode.Or))
        { left = binary.Left; right = binary.Right; return true; }
        if (instruction is not LlvmSelectInstruction select || !ReferenceEquals(select.Condition.Type, select.Type)) return false;
        if (and && select.FalseValue is LlvmConstantInt { IsZero: true })
        { left = select.Condition; right = select.TrueValue; return true; }
        if (!and && select.TrueValue is LlvmConstantInt { IsOne: true })
        { left = select.Condition; right = select.FalseValue; return true; }
        return false;
    }
}
