// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeKnownBitsFromCond/ICmpCond/Cmp and adjustKnownBitsForSelectArm.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static LlvmKnownBits AdjustSelectArm(LlvmKnownBits known, LlvmValue condition, LlvmValue arm, bool invert,
        uint depth, LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        if (known.IsConstant) return known;
        LlvmKnownBits implied = FromCondition(arm, condition, invert, depth + 1, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        if (implied.IsUnknown) return known;
        implied = implied.UnionWith(known);
        if (implied.HasConflict || !LlvmValueTracking.IsGuaranteedNotToBeUndef(arm, tree, context, metrics, depth + 1, assumptions, allowEphemerals, conditions)) return known;
        Count(metrics, "known_bits.select.condition_facts");
        return implied;
    }
    private static LlvmKnownBits FromCondition(LlvmValue value, LlvmValue condition, bool invert, uint depth, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        uint width = GetKnownBitWidth(value.Type, layout ?? GetQueryLayout(value, null));
        if (depth < MaxDepth && MatchLogicalCondition(condition, out LlvmValue? logicalLeft, out LlvmValue? logicalRight, out bool isAnd))
        {
            LlvmKnownBits a = FromCondition(value, logicalLeft!, invert, depth + 1, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout), b = FromCondition(value, logicalRight!, invert, depth + 1, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
            return (invert ? !isAnd : isAnd) ? a.UnionWith(b) : a.IntersectWith(b);
        }
        if (condition is LlvmICmpInstruction cmp)
        {
            LlvmCmpPredicate pred = invert ? LlvmCmpInstruction.GetInversePredicate(cmp.Predicate) : cmp.Predicate;
            if (cmp.Left is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc && ReferenceEquals(trunc.Operand, value))
            {
                LlvmKnownBits bits = FromComparison(trunc, pred, trunc, cmp.Right, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
                return trunc.HasNoUnsignedWrap ? bits.ZExt(width) : bits.AnyExt(width);
            }
            return FromComparison(value, pred, cmp.Left, cmp.Right, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        }
        if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } cast && ReferenceEquals(cast.Operand, value))
        {
            LlvmKnownBits bits = LlvmKnownBits.MakeConstant(new(1, invert ? 0UL : 1UL));
            return cast.HasNoUnsignedWrap ? bits.ZExt(width) : bits.AnyExt(width);
        }
        if (depth < MaxDepth && condition is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } not &&
            (GetIntegerSplat(not.Right) is { Value.IsAllOnes: true } || GetIntegerSplat(not.Left) is { Value.IsAllOnes: true }))
            return FromCondition(value, GetIntegerSplat(not.Right) is { Value.IsAllOnes: true } ? not.Left : not.Right, !invert, depth + 1, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        return new(width);
    }
    private static bool MatchLogicalCondition(LlvmValue condition, out LlvmValue? left, out LlvmValue? right, out bool isAnd)
    {
        left = right = null; isAnd = false;
        if (!condition.Type.GetScalarType().IsIntegerTy(1)) return false;
        if (condition is LlvmBinaryOperator { Opcode: LlvmOpcode.And or LlvmOpcode.Or } binary)
        { left = binary.Left; right = binary.Right; isAnd = binary.Opcode == LlvmOpcode.And; return true; }
        if (condition is LlvmSelectInstruction select)
        {
            if (GetIntegerSplat(select.FalseValue) is { IsZero: true })
            { left = select.Condition; right = select.TrueValue; isAnd = true; return true; }
            if (GetIntegerSplat(select.TrueValue) is { IsOne: true })
            { left = select.Condition; right = select.FalseValue; return true; }
        }
        return false;
    }
    private static LlvmKnownBits FromComparison(LlvmValue value, LlvmCmpPredicate pred, LlvmValue lhs, LlvmValue rhs, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        uint width = GetKnownBitWidth(value.Type, layout ?? GetQueryLayout(value, null));
        LlvmKnownBits known = new(width);
        bool IsValue(LlvmValue operand) => ReferenceEquals(value, operand) || operand is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } ptr && ReferenceEquals(ptr.Operand, value) && ptr.Type.GetScalarType().GetIntegerBitWidth() == width;
        if (rhs.Type.IsPointerTy())
        {
            if (!ReferenceEquals(lhs, value) || rhs is not LlvmConstantPointerNull) return known;
            return pred switch
            {
                LlvmCmpPredicate.ICmpEq => LlvmKnownBits.MakeConstant(LlvmAPInt.GetZero(width)),
                LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSgt => known.WithNonNegative(),
                LlvmCmpPredicate.ICmpSlt => known.WithNegative(),
                _ => known
            };
        }
        if (GetIntegerSplat(rhs) is not { } constant) return known;
        LlvmAPInt c = constant.Value;
        LlvmBinaryOperator? binary = lhs as LlvmBinaryOperator;
        LlvmValue? other = binary is null ? null : IsValue(binary.Left) ? binary.Right : IsValue(binary.Right) ? binary.Left : null;
        if (pred == LlvmCmpPredicate.ICmpEq)
        {
            if (IsValue(lhs)) return LlvmKnownBits.MakeConstant(c);
            if (binary is null) return known;
            if (binary.Opcode == LlvmOpcode.And && other is not null)
                return new(GetIntegerSplat(other) is { } mask ? ~c & mask.Value : known.Zero, c);
            if (binary.Opcode == LlvmOpcode.Or && other is not null)
                return new(~c, GetIntegerSplat(other) is { } mask ? c & ~mask.Value : known.One);
            if (IsValue(binary.Left) && GetIntegerSplat(binary.Right) is { } shift && shift.Value.Ult(new(shift.Value.BitWidth, width)))
            {
                uint amount = (uint)shift.Value.GetZExtValue();
                if (binary.Opcode == LlvmOpcode.Shl) return new((~c).LShr(amount), c.LShr(amount));
                if (binary.Opcode is LlvmOpcode.LShr or LlvmOpcode.AShr) return new((~c).Shl(amount), c.Shl(amount));
            }
            return known;
        }
        if (pred == LlvmCmpPredicate.ICmpNe)
            return c.IsZero && binary is { Opcode: LlvmOpcode.And } && IsValue(binary.Left) && GetIntegerSplat(binary.Right) is { Value.IsPowerOf2: true } mask ? new(known.Zero, mask.Value) : known;
        LlvmAPInt? offset = null;
        if (binary is { Opcode: LlvmOpcode.Add } && IsValue(binary.Left) && GetIntegerSplat(binary.Right) is { } addend) offset = addend.Value;
        if (binary is { Opcode: LlvmOpcode.Or, IsDisjoint: true } && IsValue(binary.Left) && GetIntegerSplat(binary.Right) is { } disjoint) offset = disjoint.Value;
        if (IsValue(lhs) || offset.HasValue)
        {
            LlvmConstantRange range = LlvmConstantRange.MakeExactICmpRegion(pred, c);
            if (offset is LlvmAPInt amount) range = range.Sub(new LlvmConstantRange(amount));
            known = known.UnionWith(range.ToKnownBits());
        }
        if (pred is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge)
        {
            if ((binary is { Opcode: LlvmOpcode.And } && other is not null) || (binary is { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true } && IsValue(binary.Left)))
                known = new(known.Zero, known.One | LlvmAPInt.GetHighBitsSet(width, (c + new LlvmAPInt(width, pred == LlvmCmpPredicate.ICmpUgt ? 1UL : 0UL)).CountLeadingOnes()));
        }
        if (pred is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle)
        {
            if ((binary is { Opcode: LlvmOpcode.Or } or { Opcode: LlvmOpcode.Add, HasNoUnsignedWrap: true }) && other is not null)
                known = new(known.Zero | LlvmAPInt.GetHighBitsSet(width, (c - new LlvmAPInt(width, pred == LlvmCmpPredicate.ICmpUlt ? 1UL : 0UL)).CountLeadingZeros()), known.One);
        }
        return known;
    }
}
