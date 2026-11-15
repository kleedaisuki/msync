// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 5537-6021.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpBinOp(LlvmICmpInstruction compare, LlvmInstructionSimplify.BinaryQuery query)
        {
            Count("icmp.binop.queries");
            query = query with { Context = compare };
            LlvmValue op0 = compare.Left, op1 = compare.Right;
            var b0 = op0 as LlvmBinaryOperator; var b1 = op1 as LlvmBinaryOperator;
            if (b0 is null && b1 is null) return null;
            if (FoldICmpXNegX(compare) is { } neg) return neg;
            var pred = compare.Predicate;
            for (int side = 0; side < 2; side++)
            {
                var value = side == 0 ? op0 : op1; var other = side == 0 ? op1 : op0;
                var p = side == 0 ? pred : LlvmCmpInstruction.GetSwappedPredicate(pred);
                if (ICmpIsEquality(pred) && value is LlvmBinaryOperator { Opcode: LlvmOpcode.URem } rem && rem.Left == other)
                    return BinCmp(pred == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge, rem.Left, rem.Right, "urem-self");
                if (p is not (LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge)) continue;
                if (value.HasOneUse && BinMatchCommon(value, LlvmOpcode.Add, other, out var x))
                    return BinCmp(p, Builder.CreateNot(other), x, "add-overflow");
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstant offset, HasOneUse: true } outer &&
                    BinMatchCommon(outer.Left, LlvmOpcode.Add, other, out x))
                    return BinCmp(p, Builder.CreateSub(LlvmConstantExpr.GetNot(offset), x), other, "add-offset-overflow");
            }
            if (ICmpIsEquality(pred) && b0 is { Opcode: LlvmOpcode.And } && ICmpConstantMatches(op1, bits => bits.IsMinSignedValue) && ICmpConstantMatches(b0.Right, bits => bits.IsZero || (-bits).IsPowerOf2))
                return BinCmp(pred == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge, b0.Left, Builder.CreateSub(op1, b0.Right), "and-signmask");
            for (int side = 0; side < 2; side++)
            {
                var value = side == 0 ? op0 : op1; var other = side == 0 ? op1 : op0;
                var p = side == 0 ? pred : LlvmCmpInstruction.GetSwappedPredicate(pred);
                if (p is not (LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge)) continue;
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add } mask &&
                    ICmpAPInt(mask.Right) is { } maskC && !maskC.Value.IsZero && (maskC.Value & (maskC.Value + new LlvmAPInt(maskC.Value.BitWidth, 1))).IsZero && add.Left == other &&
                    ICmpAPInt(add.Right, true) is { } addC && addC.Value == maskC.Value)
                    return BinCmp(p == LlvmCmpPredicate.ICmpUlt ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq, other, ICmpZero(other), "add-mask-overflow");
            }
            bool nw0 = HasNoWrapProblem(b0, pred, out bool nsw0, out bool nuw0);
            bool nw1 = HasNoWrapProblem(b1, pred, out bool nsw1, out bool nuw1);
            LlvmValue? a = BinAddLike(b0) ? b0!.Left : null, b = BinAddLike(b0) ? b0!.Right : null;
            LlvmValue? c = BinAddLike(b1) ? b1!.Left : null, d = BinAddLike(b1) ? b1!.Right : null;
            if ((a == op1 || b == op1) && nw0) return BinCmp(pred, a == op1 ? b! : a!, ICmpZero(op1), "add-self-left");
            if ((c == op0 || d == op0) && nw1) return BinCmp(pred, ICmpZero(op0), c == op0 ? d! : c!, "add-self-right");
            if (MatchCommonBinOpOperands(op0, op1, pred, out var lhs, out var rhs, query)) return BinCmp(pred, lhs, rhs, "common-binop");
            if (!ICmpIsEquality(pred))
            {
                bool negative = pred is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge;
                if (a is not null && nw0 && (!negative || ICmpIsSigned(pred)) && ShareCommonDivisor(a, op1, b!, negative, compare))
                    return BinCmp(BinFlipStrict(pred), a, op1, "offset-divisor-left");
                negative = pred is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSle;
                if (c is not null && nw1 && ShareCommonDivisor(op0, c, d!, negative, compare))
                    return BinCmp(BinFlipStrict(pred), op0, c, "offset-divisor-right");
            }
            if (a is not null && c is not null && nw0 && nw1 && (op0.HasOneUse || op1.HasOneUse) && !ICmpIsUnsigned(pred))
            {
                var ap1 = ICmpAPInt(b!, true); var ap2 = ICmpAPInt(d!, true);
                if (ap1 is not null && ap2 is not null && ap1.Value.IsNegative == ap2.Value.IsNegative)
                {
                    var v1 = ap1.Value; var v2 = ap2.Value;
                    if ((v1.IsNegative ? -v1 : v1).Uge(v2.IsNegative ? -v2 : v2))
                    {
                        var diff = v1 - v2;
                        return BinCmp(pred, Builder.CreateAdd(a, BinConstant(op0.Type, diff), "", nuw0 && diff.Ule(v1), nsw0), c, "merge-offset-left");
                    }
                    else
                    {
                        var diff = v2 - v1;
                        return BinCmp(pred, a, Builder.CreateAdd(c, BinConstant(op0.Type, diff), "", nuw1 && diff.Ule(v2), nsw1), "merge-offset-right");
                    }
                }
                if (b is LlvmConstant c1 && d is LlvmConstant c2 && ICmpIsEquality(pred))
                    return BinCmp(pred, a, Builder.CreateAdd(c, LlvmConstantExpr.GetSub(c2, c1)), "merge-constant-offset");
            }
            a = b0?.Opcode == LlvmOpcode.Sub ? b0.Left : null; b = a is not null ? b0!.Right : null;
            c = b1?.Opcode == LlvmOpcode.Sub ? b1.Left : null; d = c is not null ? b1!.Right : null;
            if (a == op1 && nw0) return BinCmp(pred, ICmpZero(op1), b!, "sub-self-left");
            if (c == op0 && nw1) return BinCmp(pred, d!, ICmpZero(op0), "sub-self-right");
            if (a == op1 && pred is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle) return BinCmp(pred, b!, a!, "sub-overflow-left");
            if (c == op0 && pred is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUge) return BinCmp(pred, c!, d!, "sub-overflow-right");
            if (a == op1 && pred is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUlt && IsKnownNonZero(b!, compare))
                return BinCmp(BinFlipStrict(pred), b!, a!, "sub-nonzero-left");
            if (c == op0 && pred is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUgt && IsKnownNonZero(d!, compare))
                return BinCmp(BinFlipStrict(pred), c!, d!, "sub-nonzero-right");
            if (nw0 && ICmpIsSigned(pred) && b0 is { Opcode: LlvmOpcode.Sub } && ICmpConstantMatches(b0.Left, bits => bits.IsZero) &&
                op1 is LlvmConstant rc && ICmpConstantMatches(rc, bits => !bits.IsMinSignedValue))
                return BinCmp(LlvmCmpInstruction.GetSwappedPredicate(pred), b0.Right, LlvmConstantExpr.GetNeg(rc), "neg-constant");
            if (FoldICmpXorXX(compare, query) is { } xor) return xor;
            if (FoldICmpOrXX(compare, query) is { } or) return or;
            if (FoldICmpCommonMul(compare, b0, b1, nsw0, nsw1, nuw0, nuw1, query) is { } mul) return mul;
            var srem = b0 is { Opcode: LlvmOpcode.SRem } && op1 == b0.Right ? b0 : b1 is { Opcode: LlvmOpcode.SRem } && op0 == b1.Right ? b1 : null;
            if (srem is not null)
            {
                var p = srem == b0 ? LlvmCmpInstruction.GetSwappedPredicate(pred) : pred;
                if (ICmpIsEquality(p)) return ReplaceInstUsesWith(compare, BinConstant(compare.Type, new LlvmAPInt(1, p == LlvmCmpPredicate.ICmpNe ? 1UL : 0UL)));
                if (p is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge) return BinCmp(LlvmCmpPredicate.ICmpSgt, srem.Right, ICmpOnes(srem), "srem-divisor-positive");
                if (p is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle) return BinCmp(LlvmCmpPredicate.ICmpSlt, srem.Right, ICmpZero(srem), "srem-divisor-negative");
            }
            if (b0 is not null && b1 is not null && b0.Opcode == b1.Opcode && (b0.HasOneUse || b1.HasOneUse) && b0.Right == b1.Right &&
                FoldICmpSameBinOp(compare, b0, b1, nsw0 && nsw1, nuw0 && nuw1) is { } same) return same;
            if (b0 is { Opcode: LlvmOpcode.And } && pred == LlvmCmpPredicate.ICmpUlt &&
                (BinMatchCommon(b0.Left, LlvmOpcode.Add, op1, out var minus) && ICmpConstantMatches(minus, bits => bits.IsAllOnes) ||
                 BinMatchCommon(b0.Right, LlvmOpcode.Add, op1, out minus) && ICmpConstantMatches(minus, bits => bits.IsAllOnes)))
                return BinCmp(LlvmCmpPredicate.ICmpNe, op1, ICmpZero(op1), "and-predecessor");
            if (!ICmpIsSigned(pred))
            {
                if (b0 is { Opcode: LlvmOpcode.Shl } && b0.Left == op1 && ICmpConstantMatches(b0.Right, bits => bits.IsOne))
                    return BinCmp(ICmpSignedPredicate(pred), op1, ICmpZero(op1), "self-shl-left");
                if (b1 is { Opcode: LlvmOpcode.Shl } && b1.Left == op0 && ICmpConstantMatches(b1.Right, bits => bits.IsOne))
                    return BinCmp(ICmpSignedPredicate(pred), ICmpZero(op0), op0, "self-shl-right");
            }
            if (FoldMultiplicationOverflowCheck(compare) is { } overflow) return ReplaceInstUsesWith(compare, overflow);
            if (FoldICmpAndXX(compare, query) is { } and) return and;
            if (FoldICmpWithTruncSignExtendedVal(compare) is { } trunc) return ReplaceInstUsesWith(compare, trunc);
            if (FoldShiftIntoShiftInAnotherHandOfAndInICmp(compare, query) is { } shift) return ReplaceInstUsesWith(compare, shift);
            return null;
        }
        private static LlvmConstant BinConstant(LlvmType type, LlvmAPInt value) => LlvmConstantInt.Get(type, value);
        private bool ShareCommonDivisor(LlvmValue x, LlvmValue y, LlvmValue z, bool negative, LlvmInstruction context)
        {
            if (ICmpAPInt(z) is not { } constant) return false;
            var c = constant.Value;
            if (negative ? c.IsAllOnes : c.IsOne) return true;
            if (negative) c = -c;
            if (c.IsNegative || c.IsZero || !c.IsPowerOf2) return false;
            var mask = c - new LlvmAPInt(c.BitWidth, 1);
            return MaskedValueIsZero(x, mask, context) && MaskedValueIsZero(y, mask, context);
        }
        private LlvmInstruction? FoldICmpCommonMul(LlvmICmpInstruction compare, LlvmBinaryOperator? b0, LlvmBinaryOperator? b1,
            bool nsw0, bool nsw1, bool nuw0, bool nuw1, LlvmInstructionSimplify.BinaryQuery query)
        {
            if (b0 is not { Opcode: LlvmOpcode.Mul } || b1 is not { Opcode: LlvmOpcode.Mul } ||
                !Common(b0.Left, b0.Right, b1.Left, b1.Right, out var x, out var y, out var z)) return null;
            var pred = compare.Predicate;
            if (ICmpIsSigned(pred) && nsw0 && nsw1)
            {
                if (LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpSlt, x, y, query) is { } lt && ICmpConstantMatches(lt, bits => bits.IsOne))
                    return BinCmp(LlvmCmpInstruction.GetSwappedPredicate(pred), z, ICmpZero(z), "mul-ordered-lt");
                if (LlvmInstructionSimplify.SimplifyICmp(LlvmCmpPredicate.ICmpSgt, x, y, query) is { } gt && ICmpConstantMatches(gt, bits => bits.IsOne))
                    return BinCmp(pred, z, ICmpZero(z), "mul-ordered-gt");
            }
            if (!ICmpIsEquality(pred)) return null;
            if ((nsw0 && nsw1 || nuw0 && nuw1) && LlvmKnownBitsAnalysis.IsKnownNonEqual(x, y, context: query.Context, tree: query.Tree,
                metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext))
                return BinCmp(pred, z, ICmpZero(z), "mul-distinct");
            var known = ComputeKnownBits(z, compare);
            if (known.CountMaxTrailingZeros() == 0 || (!known.One.IsZero || IsKnownNonZero(z, compare)) && (nsw0 && nsw1 || nuw0 && nuw1))
                return BinCmp(pred, x, y, "mul-cancel");
            return null;
        }
        private LlvmInstruction? FoldICmpSameBinOp(LlvmICmpInstruction compare, LlvmBinaryOperator left, LlvmBinaryOperator right, bool nsw, bool nuw)
        {
            var pred = compare.Predicate; var c = ICmpAPInt(left.Right);
            switch (left.Opcode)
            {
                case LlvmOpcode.Add: case LlvmOpcode.Sub: case LlvmOpcode.Xor:
                    if (ICmpIsEquality(pred)) return BinCmp(pred, left.Left, right.Left, "same-bijective");
                    if (c is not null && (c.Value.IsMinSignedValue || left.Opcode == LlvmOpcode.Xor && c.Value.IsMaxSignedValue))
                    {
                        var p = LlvmCmpPredicateInfo.GetFlippedSignednessPredicate(pred);
                        if (!c.Value.IsMinSignedValue) p = LlvmCmpInstruction.GetSwappedPredicate(p);
                        return BinCmp(p, left.Left, right.Left, "same-sign-offset");
                    }
                    break;
                case LlvmOpcode.Mul:
                    if (ICmpIsEquality(pred) && c is not null && !c.IsZero && !c.Value.IsOne && c.Value.CountTrailingZeros() != 0)
                    {
                        var mask = BinConstant(left.Type, LlvmAPInt.GetAllOnes(c.Value.BitWidth).LShr(c.Value.CountTrailingZeros()));
                        return BinCmp(pred, Builder.CreateAnd(left.Left, mask), Builder.CreateAnd(right.Left, mask), "same-mul-mask");
                    }
                    break;
                case LlvmOpcode.UDiv: case LlvmOpcode.LShr:
                    if (!ICmpIsSigned(pred) && left.IsExact && right.IsExact) return BinCmp(pred, left.Left, right.Left, "same-unsigned-exact");
                    break;
                case LlvmOpcode.SDiv:
                    if ((ICmpIsEquality(pred) || ICmpConstantMatches(left.Right, bits => bits.IsNonNegative)) && left.IsExact && right.IsExact)
                        return BinCmp(pred, left.Left, right.Left, "same-sdiv-exact");
                    break;
                case LlvmOpcode.AShr:
                    if (left.IsExact && right.IsExact) return BinCmp(pred, left.Left, right.Left, "same-ashr-exact");
                    break;
                case LlvmOpcode.Shl:
                    if ((nuw || nsw) && (nsw || !ICmpIsSigned(pred))) return BinCmp(pred, left.Left, right.Left, "same-shl-nowrap");
                    break;
            }
            return null;
        }
    }
}
