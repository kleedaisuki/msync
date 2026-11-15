// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/InstructionSimplify.cpp
// simplifyICmpInst, simplifyICmpOfBools, simplifyICmpWithZero and simplifyCastInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    public static LlvmValue? SimplifyICmp(LlvmCmpPredicate predicate, LlvmValue lhs, LlvmValue rhs,
        LlvmDataLayout layout, LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) =>
        SimplifyIntegerCompare(predicate, lhs, rhs, new(layout, context, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals, DC: conditions, CondContext: condContext), 3);

    public static LlvmValue? SimplifyCast(LlvmOpcode opcode, LlvmValue operand, LlvmType destination,
        LlvmDataLayout layout, LlvmInstruction? context = null, Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) =>
        SimplifyIntegerCast(opcode, operand, destination, new(layout, context, metrics, Assumptions: assumptions, AllowEphemerals: allowEphemerals), 3);

    private static bool IsIntegerEquality(LlvmCmpPredicate p) => p is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
    private static bool IsIntegerSignedPredicate(LlvmCmpPredicate p) => p is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;
    private static LlvmCmpPredicate IntegerUnsignedPredicate(LlvmCmpPredicate p) => IsIntegerSignedPredicate(p) ? (LlvmCmpPredicate)((byte)p - 4) : p;
    private static bool IntegerTrueWhenEqual(LlvmCmpPredicate p) => p is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUge
        or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle;
    private static LlvmConstantInt CompareBool(LlvmValue value, bool result) => LlvmConstantInt.GetBool(value.Context, result);
    private static bool IsIntegerCmpTrue(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth) =>
        SimplifyIntegerCompare(p, lhs, rhs, q, depth) is LlvmConstantInt { IsOne: true };

    private static LlvmValue? SimplifyIntegerCompare(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.icmp.queries");
        if (lhs is LlvmConstant lc)
        {
            if (rhs is LlvmConstant rc)
            {
                if (LlvmConstantFolding.ConstantFoldCompareInstOperands(p, lc, rc, q.Layout,
                    q.Context?.Parent?.Parent, q.Metrics) is { } folded) return folded;
            }
            else { (lhs, rhs) = (rhs, lhs); p = LlvmCmpInstruction.GetSwappedPredicate(p); }
        }
        var type = LlvmType.GetInt1Ty(lhs.Context);
        if (rhs is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
        if (q.IsUndef(rhs) && IsIntegerEquality(p)) return LlvmUndefValue.Get(type);
        if (lhs == rhs || q.IsUndef(rhs)) return CompareBool(lhs, IntegerTrueWhenEqual(p));
        if (SimplifyIntegerCompareBools(p, lhs, rhs, q) is { } boolean)
        { Count(q.Metrics, "simplify.icmp.boolean"); return boolean; }
        if (SimplifyIntegerComparePointerCasts(p, lhs, rhs, q, depth) is { } ptrcast)
        { Count(q.Metrics, "simplify.icmp.pointer_cast"); return ptrcast; }
        if (lhs.Type is LlvmIntegerType)
        {
            if (SimplifyIntegerCompareZero(p, lhs, rhs, q) is { } zero)
            { Count(q.Metrics, "simplify.icmp.zero"); return zero; }
            if (SimplifyIntegerCompareConstant(p, lhs, rhs, q) is { } constant)
            { Count(q.Metrics, "simplify.icmp.constant_range"); return constant; }
            if (SimplifyIntegerCompareCasts(p, lhs, rhs, q, depth) is { } cast)
            { Count(q.Metrics, "simplify.icmp.extension"); return cast; }
            if (IsIntegerEquality(p) && !IsIntegerZero(rhs) &&
                LlvmKnownBitsAnalysis.IsKnownNonEqual(lhs, rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, useInstructionInfo: q.UseInstructionInfo, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext))
                return CompareBool(lhs, p == LlvmCmpPredicate.ICmpNe);
            if (SimplifyIntegerCompareBinOps(p, lhs, rhs, q, depth) is { } binop)
            { Count(q.Metrics, "simplify.icmp.binop"); return binop; }
            if (SimplifyIntegerCompareMonotonic(p, lhs, rhs, q) is { } monotonic)
            { Count(q.Metrics, "simplify.icmp.monotonic"); return monotonic; }
        }
        if (IsImpliedByDomCondition(p, lhs, rhs, q) is { } implied)
        { Count(q.Metrics, "simplify.icmp.dom_condition"); return CompareBool(lhs, implied); }
        if (lhs.Type.IsPointerTy() && SimplifyIntegerPointerCompare(p, lhs, rhs, q) is { } pointer) return pointer;
        if ((lhs is LlvmSelectInstruction || rhs is LlvmSelectInstruction) && ScalarCmpSelect(p, lhs, rhs, q, depth) is { } select) return select;
        if ((lhs is LlvmPhiInstruction || rhs is LlvmPhiInstruction) && ScalarCmpPhi(p, lhs, rhs, q, depth) is { } phi) return phi;
        Count(q.Metrics, "simplify.icmp.no_fold");
        return null;
    }

    private static LlvmValue? SimplifyIntegerCompareBools(LlvmCmpPredicate p, LlvmValue lhs, LlvmValue rhs, BinaryQuery q)
    {
        if (!lhs.Type.IsIntegerTy(1)) return null;
        if (rhs is LlvmConstantInt c)
        {
            if (c.IsZero)
                return p switch
                {
                    LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSlt => lhs,
                    LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge => ExtractIntegerNot(lhs),
                    LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSgt => CompareBool(lhs, false),
                    LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle => CompareBool(lhs, true),
                    _ => null
                };
            return p switch
            {
                LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle => lhs,
                LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSgt => ExtractIntegerNot(lhs),
                LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSlt => CompareBool(lhs, false),
                LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge => CompareBool(lhs, true),
                _ => null
            };
        }
        bool? imp = p switch
        {
            LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle => IsImpliedCondition(rhs, lhs, q),
            LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge => IsImpliedCondition(lhs, rhs, q),
            _ => null
        };
        return imp == true ? CompareBool(lhs, true) : null;
    }

    private static LlvmValue? ExtractIntegerNot(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } b
        ? IsIntegerAllOnes(b.Right) ? b.Left : IsIntegerAllOnes(b.Left) ? b.Right : null : null;

    private static LlvmValue? SimplifyIntegerCast(LlvmOpcode op, LlvmValue value, LlvmType type, BinaryQuery q, uint depth)
    {
        Count(q.Metrics, "simplify.cast.queries");
        if (value is LlvmConstant constant)
            return LlvmConstantFolding.ConstantFoldCastOperand(op, constant, type, q.Layout, q.Metrics);
        if (value is LlvmCastInstruction cast && cast.Operand.Type == type &&
            LlvmCastInstruction.IsEliminableCastPair(cast.Opcode, op, cast.Operand.Type, cast.Type, type, q.Layout) == LlvmOpcode.BitCast) return cast.Operand;
        if (op == LlvmOpcode.BitCast && value.Type == type) return value;
        if (op == LlvmOpcode.PtrToAddr && value is LlvmGetElementPtrInstruction gep &&
            gep.SourceElementType.IsIntegerTy(8) && gep.IndexCount == 1 &&
            gep.GetIndex(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } offset &&
            offset.Right is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } pointer &&
            pointer.Operand == gep.PointerOperand && offset.Left.Type == type) return offset.Left;
        if (op is LlvmOpcode.ZExt or LlvmOpcode.SExt && value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc &&
            trunc.Operand.Type == type && (op == LlvmOpcode.ZExt ? trunc.HasNoUnsignedWrap : trunc.HasNoSignedWrap)) return trunc.Operand;
        Count(q.Metrics, "simplify.cast.no_fold");
        return null;
    }



}
