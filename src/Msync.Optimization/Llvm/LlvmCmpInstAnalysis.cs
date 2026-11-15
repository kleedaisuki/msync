// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/CmpInstAnalysis.cpp decomposeBitTest[ICmp]; IR/Instructions.cpp
// CmpInst::isEquivalence and hasNonZeroFPOperands, scalar PatternMatch predicates.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public readonly record struct LlvmDecomposedBitTest(LlvmValue X, LlvmCmpPredicate Pred, LlvmAPInt Mask, LlvmAPInt C);

public static partial class LlvmCmpInstAnalysis
{
    public static LlvmDecomposedBitTest? DecomposeBitTest(LlvmValue condition,
        bool lookThroughTrunc = true, bool allowNonZeroC = false, bool decomposeAnd = false,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "cmp_analysis.bit_test.queries");
        if (condition is LlvmICmpInstruction compare)
        {
            if (!compare.Left.Type.IsIntOrIntVectorTy()) return null;
            return DecomposeBitTestICmp(compare.Left, compare.Right, compare.Predicate,
                lookThroughTrunc, allowNonZeroC, decomposeAnd, metrics);
        }
        LlvmValue? truncated = null;
        if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } direct) truncated = direct.Operand;
        else if (condition is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } not)
        {
            LlvmValue? inner = not.Left is LlvmConstantInt { IsMinusOne: true } ? not.Right :
                not.Right is LlvmConstantInt { IsMinusOne: true } ? not.Left : null;
            if (inner is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } cast) truncated = cast.Operand;
        }
        if (!condition.Type.IsIntOrIntVectorTy(1) || truncated is null) return null;
        uint width = truncated.Type.GetScalarSizeInBits();
        Count(metrics, "cmp_analysis.bit_test.trunc_i1");
        return new(truncated, condition is LlvmCastInstruction ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq,
            new(width, 1), LlvmAPInt.GetZero(width));
    }
    public static LlvmDecomposedBitTest? DecomposeBitTestICmp(LlvmValue lhs, LlvmValue rhs, LlvmCmpPredicate predicate,
        bool lookThroughTrunc = true, bool allowNonZeroC = false, bool decomposeAnd = false,
        Dictionary<string, ulong>? metrics = null)
    {
        if (IsEquality(predicate) && !decomposeAnd || rhs is not LlvmConstantInt constant) return null;
        bool inverted = false;
        if (predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpUge)
        { inverted = true; predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
        LlvmAPInt c = constant.Value;
        if (predicate is LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpUle)
        {
            if (predicate == LlvmCmpPredicate.ICmpSle ? c.IsMaxSignedValue : c.IsMaxValue) return null;
            c += new LlvmAPInt(c.BitWidth, 1);
            predicate = predicate == LlvmCmpPredicate.ICmpSle ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt;
        }
        LlvmAPInt mask, expected;
        LlvmCmpPredicate resultPredicate;
        switch (predicate)
        {
            case LlvmCmpPredicate.ICmpSlt:
                var sign = LlvmAPInt.GetOneBitSet(c.BitWidth, c.BitWidth - 1);
                if (c.IsZero) { mask = sign; expected = LlvmAPInt.GetZero(c.BitWidth); resultPredicate = LlvmCmpPredicate.ICmpNe; break; }
                var flipped = c ^ sign;
                if (flipped.IsPowerOf2) { mask = -flipped; expected = sign; resultPredicate = LlvmCmpPredicate.ICmpEq; break; }
                if ((-flipped).IsPowerOf2) { mask = flipped; expected = c; resultPredicate = LlvmCmpPredicate.ICmpNe; break; }
                return null;
            case LlvmCmpPredicate.ICmpUlt:
                if (c.IsPowerOf2) { mask = -c; expected = LlvmAPInt.GetZero(c.BitWidth); resultPredicate = LlvmCmpPredicate.ICmpEq; break; }
                if ((-c).IsPowerOf2) { mask = c; expected = c; resultPredicate = LlvmCmpPredicate.ICmpNe; break; }
                return null;
            case LlvmCmpPredicate.ICmpEq:
            case LlvmCmpPredicate.ICmpNe:
                Debug.Assert(decomposeAnd);
                if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt andConstant } and)
                { lhs = and.Left; mask = andConstant.Value; expected = c; resultPredicate = predicate; break; }
                if (lookThroughTrunc && lhs is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc })
                { mask = LlvmAPInt.GetAllOnes(c.BitWidth); expected = c; resultPredicate = predicate; break; }
                return null;
            default: throw new InvalidOperationException("Unexpected predicate");
        }
        if (!allowNonZeroC && !expected.IsZero) return null;
        if (inverted) resultPredicate = LlvmCmpInstruction.GetInversePredicate(resultPredicate);
        if (lookThroughTrunc && lhs is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc)
        {
            lhs = trunc.Operand;
            mask = mask.ZExt(lhs.Type.GetScalarSizeInBits()); expected = expected.ZExt(lhs.Type.GetScalarSizeInBits());
            Count(metrics, "cmp_analysis.bit_test.looked_through_trunc");
        }
        Count(metrics, "cmp_analysis.bit_test.decomposed");
        return new(lhs, resultPredicate, mask, expected);
    }
    public static bool IsEquivalence(LlvmCmpInstruction compare, bool invert = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "cmp_analysis.equivalence.queries");
        var predicate = invert ? LlvmCmpInstruction.GetInversePredicate(compare.Predicate) : compare.Predicate;
        if (predicate == LlvmCmpPredicate.ICmpEq) return true;
        if (predicate == LlvmCmpPredicate.FCmpUeq &&
            (compare is not ILlvmFastMathInstruction fp || (fp.FastMathFlags & LlvmFastMathFlags.NoNaNs) == 0)) return false;
        if (predicate is not (LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpUeq)) return false;
        // Source chooses the LHS constant first, even when both operands are constants.
        var constant = compare.Left as LlvmConstant ?? compare.Right as LlvmConstant;
        if (constant is not LlvmConstantFP floating) return false;
        ulong bits = floating.Value.BitcastToAPInt().GetZExtValue();
        bool denormal = (bits & 0x7ff0000000000000UL) == 0 && (bits & 0x000fffffffffffffUL) != 0;
        return !denormal && !floating.IsZero;
    }
    private static bool IsEquality(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
