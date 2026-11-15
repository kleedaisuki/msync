// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 5210-5536.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmICmpInstruction BinCmp(LlvmCmpPredicate pred, LlvmValue left, LlvmValue right, string reason)
        { if (Metrics is not null) Count("icmp.binop." + reason); return new(pred, left, right); }
        private static LlvmCmpPredicate BinFlipStrict(LlvmCmpPredicate pred) => pred switch
        {
            LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUle, LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUlt,
            LlvmCmpPredicate.ICmpUgt => LlvmCmpPredicate.ICmpUge, LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUgt,
            LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSle, LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSlt,
            LlvmCmpPredicate.ICmpSgt => LlvmCmpPredicate.ICmpSge, LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSgt, _ => pred
        };
        private static bool BinMatchCommon(LlvmValue value, LlvmOpcode op, LlvmValue common, out LlvmValue other)
        {
            if (value is LlvmBinaryOperator binary && binary.Opcode == op)
            {
                if (binary.Left == common) { other = binary.Right; return true; }
                if (binary.Right == common) { other = binary.Left; return true; }
            }
            other = null!; return false;
        }
        private static bool BinAddLike(LlvmBinaryOperator? value) => value is { Opcode: LlvmOpcode.Add } or { Opcode: LlvmOpcode.Or, IsDisjoint: true };
        private static bool HasNoWrapProblem(LlvmBinaryOperator? binary, LlvmCmpPredicate pred, out bool nsw, out bool nuw)
        {
            nsw = binary?.HasNoSignedWrap ?? false; nuw = binary?.HasNoUnsignedWrap ?? false;
            if (binary is { Opcode: LlvmOpcode.Or }) { nsw = nuw = true; return true; }
            return binary is { Opcode: LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl } &&
                (ICmpIsEquality(pred) || ICmpIsSigned(pred) && nsw || ICmpIsUnsigned(pred) && nuw);
        }
        private static bool Common(LlvmValue a, LlvmValue b, LlvmValue c, LlvmValue d,
            out LlvmValue lhs, out LlvmValue rhs, out LlvmValue common)
        {
            if (a == c) { lhs = b; rhs = d; common = a; return true; }
            if (a == d) { lhs = b; rhs = c; common = a; return true; }
            if (b == c) { lhs = a; rhs = d; common = b; return true; }
            if (b == d) { lhs = a; rhs = c; common = b; return true; }
            lhs = rhs = common = null!; return false;
        }
        private LlvmInstruction? FoldICmpXNegX(LlvmICmpInstruction compare)
        {
            for (int side = 0; side < 2; side++)
            {
                var neg = side == 0 ? compare.Left : compare.Right; var x = side == 0 ? compare.Right : compare.Left;
                var pred = side == 0 ? compare.Predicate : LlvmCmpInstruction.GetSwappedPredicate(compare.Predicate);
                if (neg is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub || !ICmpConstantMatches(sub.Left, bits => bits.IsZero) || sub.Right != x) continue;
                if (sub.HasNoSignedWrap)
                {
                    if (ICmpIsSigned(pred)) pred = LlvmCmpInstruction.GetSwappedPredicate(pred);
                    else if (ICmpIsUnsigned(pred)) pred = ICmpSignedPredicate(pred);
                    return BinCmp(pred, x, ICmpZero(x), "neg-nsw");
                }
                if (ICmpIsEquality(pred) && sub.HasOneUse)
                    return BinCmp(pred, Builder.CreateAnd(x, BinConstant(x.Type, LlvmAPInt.GetSignedMaxValue(x.Type.GetScalarSizeInBits()))), ICmpZero(x), "neg-equality");
            }
            return null;
        }
        private LlvmInstruction? FoldICmpAndXX(LlvmICmpInstruction compare, LlvmInstructionSimplify.BinaryQuery query)
        {
            LlvmValue op0 = compare.Left, op1 = compare.Right;
            var pred = compare.Predicate;
            if (BinMatchCommon(op1, LlvmOpcode.And, op0, out _)) { (op0, op1) = (op1, op0); pred = LlvmCmpInstruction.GetSwappedPredicate(pred); }
            if (!BinMatchCommon(op0, LlvmOpcode.And, op1, out var a)) return null;
            if (pred == LlvmCmpPredicate.ICmpUlt) return BinCmp(LlvmCmpPredicate.ICmpNe, op0, op1, "and-ult");
            if (pred == LlvmCmpPredicate.ICmpUge) return BinCmp(LlvmCmpPredicate.ICmpEq, op0, op1, "and-uge");
            if (ICmpIsEquality(pred) && op0.HasOneUse)
            {
                if (op1 is not LlvmConstant && GetFreelyInverted(op1, !op1.HasNUsesOrMore(3), Builder) is { } not1)
                    return BinCmp(pred, Builder.CreateOr(a, not1), ICmpOnes(op1), "and-invert-common");
                if (GetFreelyInverted(a, a.HasOneUse, Builder) is { } notA)
                    return BinCmp(pred, Builder.CreateAnd(op1, notA), ICmpZero(op1), "and-invert-other");
            }
            if (!ICmpIsSigned(pred)) return null;
            var known = ComputeKnownBits(a, compare);
            if (known.IsNegative) return BinCmp(ICmpUnsignedPredicate(pred), op0, op1, "and-negative-mask");
            if (pred is not (LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSgt)) return null;
            if (known.IsNonNegative) return BinCmp(LlvmCmpInstruction.GetSwappedPredicate(pred), op1, ICmpZero(op1), "and-positive-mask");
            return IsKnownNegative(op1, compare) ? BinCmp(BinFlipStrict(pred), a, ICmpZero(a), "and-negative-value") : null;
        }
        private LlvmInstruction? FoldICmpOrXX(LlvmICmpInstruction compare, LlvmInstructionSimplify.BinaryQuery query)
        {
            LlvmValue op0 = compare.Left, op1 = compare.Right; var pred = compare.Predicate;
            if (BinMatchCommon(op1, LlvmOpcode.Or, op0, out _)) { (op0, op1) = (op1, op0); pred = LlvmCmpInstruction.GetSwappedPredicate(pred); }
            if (!BinMatchCommon(op0, LlvmOpcode.Or, op1, out var a)) return null;
            if (pred == LlvmCmpPredicate.ICmpUle) return BinCmp(LlvmCmpPredicate.ICmpEq, op0, op1, "or-ule");
            if (pred == LlvmCmpPredicate.ICmpUgt) return BinCmp(LlvmCmpPredicate.ICmpNe, op0, op1, "or-ugt");
            if (!ICmpIsEquality(pred) || !op0.HasOneUse) return null;
            if (GetFreelyInverted(op1, op1 is not LlvmConstant && !op1.HasNUsesOrMore(3), Builder) is { } not1)
                return BinCmp(pred, Builder.CreateAnd(a, not1), ICmpZero(op1), "or-invert-common");
            return GetFreelyInverted(a, a.HasOneUse, Builder) is { } notA
                ? BinCmp(pred, Builder.CreateOr(op1, notA), ICmpOnes(op1), "or-invert-other") : null;
        }
        private LlvmInstruction? FoldICmpXorXX(LlvmICmpInstruction compare, LlvmInstructionSimplify.BinaryQuery query)
        {
            LlvmValue op0 = compare.Left, op1 = compare.Right; var pred = compare.Predicate;
            if (BinMatchCommon(op1, LlvmOpcode.Xor, op0, out _)) { (op0, op1) = (op1, op0); pred = LlvmCmpInstruction.GetSwappedPredicate(pred); }
            if (!BinMatchCommon(op0, LlvmOpcode.Xor, op1, out var a)) return null;
            var strict = IccStrictPredicate(pred);
            if (strict != pred && IsKnownNonZero(a, query.Context)) return BinCmp(strict, op0, op1, "xor-nonzero-strict");
            if (!ICmpConstantMatches(a, bits => bits.IsNegative)) return null;
            return strict switch
            {
                LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpUgt => BinCmp(LlvmCmpPredicate.ICmpSlt, op0, ICmpZero(op0), "xor-negative-lt"),
                LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpUlt => BinCmp(LlvmCmpPredicate.ICmpSge, op0, ICmpZero(op0), "xor-negative-ge"), _ => null
            };
        }
    }
}
