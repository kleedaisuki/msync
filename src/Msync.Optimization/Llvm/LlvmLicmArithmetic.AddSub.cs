// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp hoistAdd / hoistSub /
// hoistAddSub, PatternMatch.h NSWAddLike / NUWAddLike.
using Msync.Core.Llvm;
using P = Msync.Core.Llvm.LlvmCmpPredicate;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicmArithmetic
{
    private static bool HoistAddSub(LlvmInstruction instruction, in Query query)
    {
        if (instruction is not LlvmICmpInstruction compare) return false;
        P predicate = compare.Predicate;
        LlvmValue left = compare.Left, right = compare.Right;
        if (query.Loop.IsLoopInvariant(left))
        { (left, right) = (right, left); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        if (query.Loop.IsLoopInvariant(left) || !query.Loop.IsLoopInvariant(right) || !left.HasOneUse) return false;
        return HoistAdd(predicate, left, right, compare, query) || HoistSub(predicate, left, right, compare, query);
    }
    private static bool Signed(P predicate) => predicate is P.ICmpSgt or P.ICmpSge or P.ICmpSlt or P.ICmpSle;
    private static bool HoistAdd(P predicate, LlvmValue variantLeft, LlvmValue invariantRight,
        LlvmICmpInstruction compare, in Query query)
    {
        bool signed = Signed(predicate);
        if (variantLeft is not LlvmBinaryOperator operation ||
            !(operation.Opcode == LlvmOpcode.Add && (signed ? operation.HasNoSignedWrap : operation.HasNoUnsignedWrap) ||
              operation is { Opcode: LlvmOpcode.Or, IsDisjoint: true })) return false;
        LlvmValue variant = operation.Left, invariant = operation.Right;
        if (query.Loop.IsLoopInvariant(variant)) (variant, invariant) = (invariant, variant);
        if (query.Loop.IsLoopInvariant(variant) || !query.Loop.IsLoopInvariant(invariant)) return false;
        var overflow = signed
            ? LlvmValueTracking.ComputeOverflowForSignedSub(invariantRight, invariant, query.Layout, compare, query.Tree, query.Metrics)
            : LlvmValueTracking.ComputeOverflowForUnsignedSub(invariantRight, invariant, query.Layout, compare, query.Tree, query.Metrics);
        if (overflow != LlvmOverflowResult.NeverOverflows) { Count(query, "addsub.invariant_overflow"); return false; }
        LlvmValue result = CreateBinary(LlvmOpcode.Sub, invariantRight, invariant, query.InsertPoint, query, "invariant.op", nuw: !signed, nsw: signed);
        compare.Predicate = predicate; compare.Left = variant; compare.Right = result; compare.SameSign = false;
        query.Erase(operation);
        return true;
    }
    private static bool HoistSub(P predicate, LlvmValue variantLeft, LlvmValue invariantRight,
        LlvmICmpInstruction compare, in Query query)
    {
        bool signed = Signed(predicate);
        if (variantLeft is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } operation ||
            !(signed ? operation.HasNoSignedWrap : operation.HasNoUnsignedWrap)) return false;
        LlvmValue variant = operation.Left, invariant = operation.Right;
        bool variantSubtracted = false;
        if (query.Loop.IsLoopInvariant(variant))
        {
            (variant, invariant) = (invariant, variant); variantSubtracted = true;
            predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
        }
        if (query.Loop.IsLoopInvariant(variant) || !query.Loop.IsLoopInvariant(invariant)) return false;
        LlvmOverflowResult overflow;
        if (variantSubtracted)
            overflow = signed
                ? LlvmValueTracking.ComputeOverflowForSignedSub(invariant, invariantRight, query.Layout, compare, query.Tree, query.Metrics)
                : LlvmValueTracking.ComputeOverflowForUnsignedSub(invariant, invariantRight, query.Layout, compare, query.Tree, query.Metrics);
        else
            overflow = signed
                ? LlvmValueTracking.ComputeOverflowForSignedAdd(invariant, invariantRight, query.Layout, compare, query.Tree, query.Metrics)
                : LlvmValueTracking.ComputeOverflowForUnsignedAdd(invariant, invariantRight, query.Layout, compare, query.Tree, query.Metrics);
        if (overflow != LlvmOverflowResult.NeverOverflows) { Count(query, "addsub.invariant_overflow"); return false; }
        LlvmValue result = CreateBinary(variantSubtracted ? LlvmOpcode.Sub : LlvmOpcode.Add, invariant,
            invariantRight, query.InsertPoint, query, "invariant.op", nuw: !signed, nsw: signed);
        compare.Predicate = predicate; compare.Left = variant; compare.Right = result; compare.SameSign = false;
        query.Erase(operation);
        return true;
    }
}
