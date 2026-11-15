// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp,
// isKnownNonEqual, getInvertibleOperands and isNonEqual{PHIs,Select,Mul,Shl,URem}.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    public static bool IsKnownNonEqual(LlvmValue left, LlvmValue right, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        bool result = IsKnownNonEqualImpl(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        Count(metrics, result ? "value_facts.nonequal.proven" : "value_facts.nonequal.not_proven");
        return result;
    }

    private static bool IsKnownNonEqualImpl(LlvmValue left, LlvmValue right, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "value_facts.nonequal.visits");
        if (left == right || left.Type != right.Type) return false;
        if (depth >= MaxDepth) { Count(metrics, "value_facts.nonequal.depth_limit"); return false; }
        if (GetInvertibleIntegerOperands(left, right) is { } operands)
        {
            Count(metrics, "value_facts.nonequal.invertible");
            return IsKnownNonEqual(operands.Left, operands.Right, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        }
        if (left is LlvmPhiInstruction lp && right is LlvmPhiInstruction rp &&
            IsNonEqualIntegerPhis(lp, rp, depth, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (IsModifyingNonZeroIntegerBin(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) ||
            IsModifyingNonZeroIntegerBin(right, left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (IsNonEqualIntegerMultiply(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) ||
            IsNonEqualIntegerMultiply(right, left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (IsNonEqualIntegerShl(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) ||
            IsNonEqualIntegerShl(right, left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (left.Type is LlvmIntegerType)
        {
            var knownLeft = Compute(left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
            if (!knownLeft.IsUnknown)
            {
                var knownRight = Compute(right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                if (!((knownLeft.Zero & knownRight.One) | (knownRight.Zero & knownLeft.One)).IsZero)
                { Count(metrics, "value_facts.nonequal.contradictory_bits"); return true; }
            }
        }
        if (IsNonEqualIntegerSelect(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) ||
            IsNonEqualIntegerSelect(right, left, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (left.Type.IsPointerTy())
        {
            var layout = context?.Parent?.Parent?.Parent?.DataLayout ?? new LlvmDataLayout();
            if (IsNonEqualRecursiveGep(left, right, layout, metrics) ||
                IsNonEqualRecursiveGep(right, left, layout, metrics)) return true;
        }
        if (left is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } lc &&
            right is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } rc)
        {
            var layout = context?.Parent?.Parent?.Parent?.DataLayout ?? new LlvmDataLayout();
            if (left.Type.GetIntegerBitWidth() == layout.GetPointerTypeSizeInBits(lc.Operand.Type) &&
                right.Type.GetIntegerBitWidth() == layout.GetPointerTypeSizeInBits(rc.Operand.Type))
                return IsKnownNonEqual(lc.Operand, rc.Operand, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        }
        if (IsNonEqualIntegerURem(left, right, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) ||
            IsNonEqualIntegerURem(right, left, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        if (IsKnownNonEqualFromDomConditions(left, right, depth, context, tree, metrics, conditions, condContext)) return true;
        if (IsKnownNonEqualFromAssume(left, right, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        return false;
    }

    private static (LlvmValue Left, LlvmValue Right)? GetInvertibleIntegerOperands(LlvmValue left, LlvmValue right)
    {
        if (left is LlvmBinaryOperator a && right is LlvmBinaryOperator b && a.Opcode == b.Opcode)
        {
            switch (a.Opcode)
            {
                case LlvmOpcode.Or when !a.IsDisjoint || !b.IsDisjoint: return null;
                case LlvmOpcode.Or:
                case LlvmOpcode.Xor:
                case LlvmOpcode.Add:
                    if (a.Left == b.Left) return (a.Right, b.Right);
                    if (a.Left == b.Right) return (a.Right, b.Left);
                    if (a.Right == b.Left) return (a.Left, b.Right);
                    if (a.Right == b.Right) return (a.Left, b.Left);
                    return null;
                case LlvmOpcode.Sub:
                    if (a.Left == b.Left) return (a.Right, b.Right);
                    if (a.Right == b.Right) return (a.Left, b.Left);
                    return null;
                case LlvmOpcode.Mul:
                    if (!((a.HasNoUnsignedWrap && b.HasNoUnsignedWrap) || (a.HasNoSignedWrap && b.HasNoSignedWrap))) return null;
                    return a.Right == b.Right && a.Right is LlvmConstantInt { IsZero: false } ? (a.Left, b.Left) : null;
                case LlvmOpcode.Shl:
                    if (!((a.HasNoUnsignedWrap && b.HasNoUnsignedWrap) || (a.HasNoSignedWrap && b.HasNoSignedWrap))) return null;
                    return a.Right == b.Right ? (a.Left, b.Left) : null;
                case LlvmOpcode.AShr:
                case LlvmOpcode.LShr:
                    return a.IsExact && b.IsExact && a.Right == b.Right ? (a.Left, b.Left) : null;
            }
        }
        if (left is LlvmCastInstruction ca && right is LlvmCastInstruction cb && ca.Opcode == cb.Opcode &&
            ca.Opcode is LlvmOpcode.SExt or LlvmOpcode.ZExt && ca.Operand.Type == cb.Operand.Type)
            return (ca.Operand, cb.Operand);
        if (left is LlvmPhiInstruction pa && right is LlvmPhiInstruction pb && pa.Parent == pb.Parent &&
            MatchSimpleRecurrence(pa, out var ba, out var sa, out _) &&
            MatchSimpleRecurrence(pb, out var bb, out var sb, out _) &&
            GetInvertibleIntegerOperands(ba, bb) is { } recurrence && recurrence.Left == pa && recurrence.Right == pb)
            return (sa, sb);
        return null;
    }

    private static bool IsModifyingNonZeroIntegerBin(LlvmValue value, LlvmValue original, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (value is not LlvmBinaryOperator bin || bin.Opcode is not (LlvmOpcode.Add or LlvmOpcode.Xor or LlvmOpcode.Or) ||
            (bin.Opcode == LlvmOpcode.Or && !bin.IsDisjoint)) return false;
        var other = bin.Left == original ? bin.Right : bin.Right == original ? bin.Left : null;
        return other is not null && IsKnownNonZero(other, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
    }

    private static bool IsNonEqualIntegerMultiply(LlvmValue original, LlvmValue value, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) =>
        value is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt c } bin && bin.Left == original &&
        (bin.HasNoSignedWrap || bin.HasNoUnsignedWrap) && !c.IsZero && !c.IsOne &&
        IsKnownNonZero(original, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);

    private static bool IsNonEqualIntegerShl(LlvmValue original, LlvmValue value, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) =>
        value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt { IsZero: false } } bin && bin.Left == original &&
        (bin.HasNoSignedWrap || bin.HasNoUnsignedWrap) && IsKnownNonZero(original, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);

    private static bool IsNonEqualIntegerPhis(LlvmPhiInstruction left, LlvmPhiInstruction right, uint depth,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (left.Parent != right.Parent) return false;
        HashSet<LlvmBasicBlock> visited = new(ReferenceEqualityComparer.Instance);
        bool usedRecursion = false;
        for (int i = 0; i < left.IncomingCount; ++i)
        {
            var block = left.GetIncomingBlock(i);
            if (!visited.Add(block)) continue;
            Count(metrics, "value_facts.nonequal.phi_incoming");
            var a = left.GetIncomingValue(i);
            LlvmValue b = null!;
            for (int j = 0; j < right.IncomingCount; ++j)
                if (right.GetIncomingBlock(j) == block) { b = right.GetIncomingValue(j); break; }
            if (a is LlvmConstantInt ac && b is LlvmConstantInt bc && ac.Value != bc.Value) continue;
            if (usedRecursion || !IsKnownNonEqual(a, b, depth + 1, block.GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null)) return false;
            usedRecursion = true;
        }
        return true;
    }

    private static bool IsNonEqualIntegerSelect(LlvmValue value, LlvmValue other, uint depth,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (value is not LlvmSelectInstruction select) return false;
        if (other is LlvmSelectInstruction rhs && select.Condition == rhs.Condition)
            return IsKnownNonEqual(select.TrueValue, rhs.TrueValue, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) &&
                IsKnownNonEqual(select.FalseValue, rhs.FalseValue, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
        return IsKnownNonEqual(select.TrueValue, other, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext) &&
            IsKnownNonEqual(select.FalseValue, other, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
    }

    private static bool IsNonEqualIntegerURem(LlvmValue value, LlvmValue remainder,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (remainder is not LlvmBinaryOperator { Opcode: LlvmOpcode.URem } rem || rem.Left != value) return false;
        if (IsTrueNonEqualPredicate(LlvmCmpPredicate.ICmpUle, rem.Right, value)) return true;
        if (context is null) return false;
        var layout = context.Parent?.Parent?.Parent?.DataLayout ?? new LlvmDataLayout();
        return LlvmInstructionSimplify.IsImpliedByDomCondition(LlvmCmpPredicate.ICmpUge, value, rem.Right,
            new(layout, context, metrics, tree, UseInstructionInfo: useInstructionInfo)) == true;
    }
}
