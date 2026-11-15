// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: visitOr local scalar patterns.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldOrMaskedConstants(LlvmBinaryOperator i, LlvmValue a, LlvmAPInt c0, LlvmValue b, LlvmAPInt c1)
        {
            if (c0 == ~c1)
            {
                if (LogicOther(a, LlvmOpcode.Or, b, out var x0)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateAnd(x0, LogicC(i.Type, c0)), b), "or.complement_masks_or_lhs");
                if (LogicOther(b, LlvmOpcode.Or, a, out var x1)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateAnd(x1, LogicC(i.Type, c1)), a), "or.complement_masks_or_rhs");
                if (LogicOther(a, LlvmOpcode.Xor, b, out var x2)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateAnd(x2, LogicC(i.Type, c0)), b), "or.complement_masks_xor_lhs");
                if (LogicOther(b, LlvmOpcode.Xor, a, out var x3)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateAnd(x3, LogicC(i.Type, c1)), a), "or.complement_masks_xor_rhs");
            }
            if ((c0 & c1).IsZero)
            {
                if (LogicOther(a, LlvmOpcode.Or, b, out var x0) && LogicMaskedZero(x0, ~c0, i)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, a, LogicC(i.Type, c0 | c1)), "or.disjoint_masks_or_lhs");
                if (LogicOther(b, LlvmOpcode.Or, a, out var x1) && LogicMaskedZero(x1, ~c1, i)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, b, LogicC(i.Type, c0 | c1)), "or.disjoint_masks_or_rhs");
                if (a is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt c2 } or0
                    && b is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt c3 } or1 && ReferenceEquals(or0.Left, or1.Left)
                    && (c2.Value & ~c0).IsZero && (c3.Value & ~c1).IsZero)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateOr(or0.Left, LogicC(i.Type, c2.Value | c3.Value), "bitfield"), LogicC(i.Type, c0 | c1)), "or.bitfield_constants");
            }
            if (LogicNarrowShiftPair(a, b, c1)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, a, LogicC(i.Type, c0 | c1)), "or.truncated_shift_masks");
            if (LogicNarrowShiftPair(b, a, c0)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, b, LogicC(i.Type, c0 | c1)), "or.truncated_shift_masks");
            return null;
        }
        private static bool LogicNarrowShiftPair(LlvmValue a, LlvmValue b, LlvmAPInt bMask)
        {
            if (a is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt amount } wide } trunc
                || b is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } narrow, Right: LlvmConstantInt other }
                || !ReferenceEquals(wide.Left, narrow.Operand)) return false;
            uint width = trunc.Type.GetScalarSizeInBits();
            return amount.Value.Ult(new(amount.BitWidth, width)) && other.Value.ZExtOrTrunc(amount.BitWidth) == amount.Value && bMask.GetActiveBits() <= width - (uint)amount.GetZExtValue();
        }
        private LlvmInstruction? FoldOrXorChains(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var pair = orientation == 0 ? i.Left : i.Right; var chain = orientation == 0 ? i.Right : i.Left;
                if (pair is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor) continue;
                if (LogicOther(chain, LlvmOpcode.Xor, xor.Left, out var inner) && LogicOther(inner, LlvmOpcode.Xor, xor.Right, out var c))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, pair, c), "or.xor_chain");
                if (LogicOther(chain, LlvmOpcode.Xor, xor.Right, out inner) && LogicOther(inner, LlvmOpcode.Xor, xor.Left, out c))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, pair, c), "or.reverse_xor_chain");
            }
            return null;
        }
        private LlvmInstruction? FoldOrXorIdentities(LlvmBinaryOperator i)
        {
            LlvmValue left = i.Left, right = i.Right;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor }) (left, right) = (right, left);
            if (right is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor) return null;
            var a = xor.Left; var b = xor.Right;
            if (LogicOther(left, LlvmOpcode.Or, a, out _)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, left, b), "or.xor_or_absorption_a");
            if (LogicOther(left, LlvmOpcode.Or, b, out _)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, left, a), "or.xor_or_absorption_b");
            if (LogicPair(left, LlvmOpcode.And, a, b)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, a, b), "or.and_xor_to_or");
            if (left.HasOneUse || right.HasOneUse)
            {
                if (LogicNot(left, out var inverted) && (ReferenceEquals(inverted, a) || ReferenceEquals(inverted, b)))
                    return LogicResult(LogicNotInstruction(Builder.CreateAnd(a, b)), "or.not_xor_to_nand");
                if (LogicNot(left, out inverted) && LogicOther(inverted, LlvmOpcode.And, a, out _))
                    return LogicResult(LogicNotInstruction(Builder.CreateAnd(inverted, b)), "or.not_and_xor_to_nand_a");
                if (LogicNot(left, out inverted) && LogicOther(inverted, LlvmOpcode.And, b, out _))
                    return LogicResult(LogicNotInstruction(Builder.CreateAnd(inverted, a)), "or.not_and_xor_to_nand_b");
            }
            if (left.HasOneUse && right.HasOneUse)
                for (int orientation = 0; orientation < 2; ++orientation)
                    if (LogicNotPairOperands(left, LlvmOpcode.Or, orientation != 0, out var c, out var not) && (ReferenceEquals(not, a) || ReferenceEquals(not, b)))
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateNot(Builder.CreateAnd(a, b), "nand"), c), "or.not_or_xor_to_nand");
            return null;
        }
        private LlvmInstruction? FoldOrMiddlePatterns(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } sext && sext.Operand.Type.IsIntegerTy(1)) return LogicSelect(sext.Operand, LogicOnes(i.Type), rhs, "or.sext_bool");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (rhs is LlvmConstantInt || lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true, Right: LlvmConstantInt constant } inner) continue;
                var newInner = Builder.CreateOr(inner.Left, rhs);
                if (newInner is LlvmBinaryOperator newOr) newOr.IsDisjoint = i.IsDisjoint;
                LogicTakeName(newInner, inner);
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, newInner, constant) { IsDisjoint = i.IsDisjoint && inner.IsDisjoint }, "or.reassociate_constant");
            }
            if (i.Left is LlvmSelectInstruction { HasOneUse: true } a && i.Right is LlvmSelectInstruction { HasOneUse: true } b && ReferenceEquals(a.Condition, b.Condition))
                return LogicSelect(a.Condition, Builder.CreateOr(a.TrueValue, b.TrueValue), Builder.CreateOr(a.FalseValue, b.FalseValue), "or.common_select_condition");
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var shifted = orientation == 0 ? i.Left : i.Right; var x = orientation == 0 ? i.Right : i.Left;
                if (shifted is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, HasOneUse: true, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } sub, Right: LlvmConstantInt amount }
                    && ReferenceEquals(sub.Right, x) && amount.Value == new LlvmAPInt(amount.BitWidth, i.Type.GetScalarSizeInBits() - 1))
                    return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, x, sub.Left), LogicOnes(i.Type), x, "or.nsw_difference_sign");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (LogicXorAndSelf(lhs, out var and, out var aValue, out var bValue) && LogicXorAndPair(rhs, aValue, bValue))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, aValue, bValue), "or.xor_and_pair");
            }
            return null;
        }
        private static bool LogicXorAndSelf(LlvmValue value, out LlvmBinaryOperator and, out LlvmValue a, out LlvmValue b)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
                for (int orientation = 0; orientation < 2; ++orientation)
                {
                    var candidate = orientation == 0 ? xor.Left : xor.Right; var other = orientation == 0 ? xor.Right : xor.Left;
                    if (candidate is LlvmBinaryOperator { Opcode: LlvmOpcode.And } binary && ReferenceEquals(other, binary.Left))
                    { and = binary; a = binary.Left; b = binary.Right; return true; }
                }
            and = null!; a = b = null!; return false;
        }
        private static bool LogicXorAndPair(LlvmValue value, LlvmValue a, LlvmValue b)
        {
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor) return false;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var candidate = orientation == 0 ? xor.Left : xor.Right; var other = orientation == 0 ? xor.Right : xor.Left;
                if (candidate is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && ReferenceEquals(and.Left, a) && ReferenceEquals(and.Right, b) && ReferenceEquals(other, b)) return true;
            }
            return false;
        }
        private LlvmInstruction? FoldOrLowMask(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } shift,
                    Right: LlvmConstantInt { IsMinusOne: true } } || rhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } other
                    || !ReferenceEquals(other.Right, shift.Right) || (!lhs.HasOneUse && !rhs.HasOneUse)) continue;
                var sub = Builder.CreateSub(LogicC(i.Type, i.Type.GetScalarSizeInBits() - 1), shift.Right);
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.LShr, LogicOnes(i.Type), sub), "or.low_mask_up_to_bit");
            }
            return null;
        }
        private LlvmInstruction? FoldOrSharedAndTree(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and || rhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } or) continue;
                bool Common(LlvmValue value) => value.HasOneUse && (LogicOther(value, LlvmOpcode.And, and.Left, out _) || LogicOther(value, LlvmOpcode.And, and.Right, out _));
                if (Common(or.Right)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, or.Left, Builder.CreateOr(or.Right, Builder.CreateAnd(and.Left, and.Right))), "or.shared_and_tree_rhs");
                if (Common(or.Left)) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, Builder.CreateOr(or.Left, Builder.CreateAnd(and.Left, and.Right)), or.Right), "or.shared_and_tree_lhs");
            }
            return null;
        }
        private LlvmInstruction? FoldOrFloatingSign(LlvmBinaryOperator i)
        {
            if (!F.NoImplicitFloat && i.Left is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast && cast.Operand.Type.IsFloatingPointTy() && i.Right is LlvmConstantInt c && c.Value == LlvmAPInt.GetSignedMinValue(i.Type.GetScalarSizeInBits()))
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.BitCast, Builder.CreateFNeg(Builder.CreateFAbs(cast.Operand)), i.Type), "or.float_sign_set");
            return null;
        }
        private LlvmInstruction? FoldOrSignum(LlvmBinaryOperator i)
        {
            if (!i.Left.HasOneUse && !i.Right.HasOneUse) return null;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt amount } shift
                    || amount.Value != new LlvmAPInt(amount.BitWidth, i.Type.GetScalarSizeInBits() - 1)
                    || rhs is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSgt, Right: LlvmConstantInt { IsZero: true } } cmp }
                    || !ReferenceEquals(cmp.Left, shift.Left)) continue;
                return LogicReplace(i, Builder.CreateIntrinsic(LlvmIntrinsicKind.SCmp, [shift.Left, LogicZero(i.Type)], (LlvmIntegerType)i.Type), "or.signum");
            }
            return null;
        }
    }
}
