// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp visitXor.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitXor(LlvmBinaryOperator i)
        {
            Count("instcombine.logic.xor.visited");
            if (LlvmInstructionSimplify.SimplifyInstruction(i, Query(i)) is { } simplified) return ReplaceInstUsesWith(i, simplified);
            if (SimplifyAssociativeOrCommutative(i)) return i;
            if (FoldBinopWithPhiOperands(i) is { } phi) return phi;
            if (FoldXorToXor(i) is { } xor) return xor;
            if (FoldUsingDistributiveLaws(i) is { } distributed) return ReplaceInstUsesWith(i, distributed);
            if (SimplifyDemandedInstructionBits(i)) return i;
            if (FoldNot(i) is { } not) return not;
            if (FoldBinOpShiftWithShift(i) is { } shifted) return shifted;
            if (FoldXorDisjointPatterns(i) is { } disjoint) return disjoint;
            if (VisitMaskedMerge(i) is { } merge) return merge;
            if (i.Right is LlvmConstant constant && FoldXorConstant(i, constant) is { } foldedConstant) return foldedConstant;
            if (i.Right is LlvmConstantInt c && i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true,
                Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt c1 } input, Right: LlvmConstantInt c2 } lshr)
            {
                var amount = c1.Value.LShr(c2.Value) ^ c.Value;
                var shift = Builder.CreateBinary(LlvmOpcode.LShr, input.Left, c2); LogicTakeName(shift, lshr);
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, shift, LogicC(i.Type, amount)), "xor.shifted_xor_constant");
            }
            if (FoldBinOpIntoSelectOrPhi(i) is { } selectPhi) return selectPhi;
            if (FoldBinOpSelectBinOp(i) is { } selectBin) return selectBin;
            if (FoldXorAndOrIdentities(i) is { } identities) return identities;
            if (FoldXorLogicalSelect(i) is { } logical) return logical;
            if (i.Left is LlvmICmpInstruction cmp0 && i.Right is LlvmICmpInstruction cmp1 && FoldXorOfICmps(cmp0, cmp1, i) is { } comparison) return ReplaceInstUsesWith(i, comparison);
            if (FoldCastedBitwiseLogic(i) is { } casts) return casts;
            if (CanonicalizeAbs(i) is { } abs) return abs;
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                // No ConstantExpr class exists in the represented IR.
                if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true, Right: LlvmConstant c3 } inner)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateXor(inner.Left, rhs), c3), "xor.hoist_constant");
            }
            if (ReassociateForUses(i) is { } uses) return uses;
            if (CanonicalizeLogicFirst(i) is { } canonical) return canonical;
            if (FoldLogicOfIsFPClass(i, i.Left, i.Right) is { } fpClass) return fpClass;
            if (CanonicalizeConditionalNegationViaMathToSelect(i) is { } negatedSelect) return negatedSelect;
            if (FoldBinOpOfDisplacedShifts(i) is { } displaced) return displaced;
            if (FoldBitwiseLogicWithIntrinsics(i) is { } intrinsic) return intrinsic;
            return FoldMaskedAddXorPattern(i);
        }
        private LlvmInstruction? FoldXorDisjointPatterns(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true, HasOneUse: true } disjoint) continue;
                if (LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.Xor, disjoint.Left, rhs, Query(i)) is { } left)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, left, disjoint.Right), "xor.disjoint_or_left");
                if (LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.Xor, disjoint.Right, rhs, Query(i)) is { } right)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, right, disjoint.Left), "xor.disjoint_or_right");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var lhs = orientation == 0 ? i.Left : i.Right; var rhs = orientation == 0 ? i.Right : i.Left;
                if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } masked) continue;
                for (int order = 0; order < 2; ++order)
                    if (LogicNot(order == 0 ? masked.Left : masked.Right, out var mask) && LogicOther(rhs, LlvmOpcode.And, mask, out _))
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, i.Left, i.Right) { IsDisjoint = LogicNotUndef(mask, i) }, "xor.complementary_masks_to_or");
            }
            return null;
        }
        private LlvmInstruction? FoldXorConstant(LlvmBinaryOperator i, LlvmConstant c1)
        {
            var op0 = i.Left;
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true, Right: LlvmConstant originalC2 } or)
            {
                LlvmConstant c2 = originalC2 is LlvmUndefValue ? LogicOnes(i.Type) : originalC2;
                var notC2 = LogicConstantNot(c2);
                var orMask = LogicMergeUndefs(notC2, c1);
                var and = Builder.CreateAnd(or.Left, orMask);
                var constant = LogicMergeUndefs(LogicConstantBinary(LlvmOpcode.Xor, c1, c2), c1);
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, and, constant), "xor.or_constant");
            }
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Or or LlvmOpcode.And, HasOneUse: true, Right: LlvmConstant c2other } logic && LogicNot(logic.Left, out var x))
            {
                var operation = logic.Opcode == LlvmOpcode.Or ? Builder.CreateAnd(x, LogicConstantNot(c2other)) : Builder.CreateOr(x, LogicConstantNot(c2other));
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, operation, LogicConstantNot(c1)), "xor.inverted_logic_constant");
            }
            var signValue = LogicCastOrSelf(op0, LlvmOpcode.Trunc);
            if (op0.HasOneUse && signValue is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt shift } ashr
                && shift.Value == new LlvmAPInt(shift.BitWidth, ashr.Left.Type.GetScalarSizeInBits() - 1)
                && c1 is not LlvmConstantInt { IsMinusOne: true })
                return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, ashr.Left, LogicOnes(ashr.Left.Type)), c1, Builder.CreateNot(c1), "xor.sign_shift_constant");
            if (c1 is LlvmConstantInt mask && FoldXorIntegerMask(i, mask.Value) is { } integerMask) return integerMask;
            if (!F.NoImplicitFloat && op0 is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast && cast.Operand.Type.IsFloatingPointTy()
                && c1 is LlvmConstantInt sign && sign.Value == LlvmAPInt.GetSignedMinValue(i.Type.GetScalarSizeInBits()))
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.BitCast, Builder.CreateFNeg(cast.Operand), i.Type), "xor.float_sign_flip");
            return null;
        }
        private LlvmInstruction? FoldXorIntegerMask(LlvmBinaryOperator i, LlvmAPInt mask)
        {
            uint width = i.Type.GetScalarSizeInBits();
            if (mask == LlvmAPInt.GetSignedMinValue(width))
            {
                if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt c } sub)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Sub, LogicC(i.Type, c.Value + mask), sub.Right), "xor.sign_mask_sub");
                if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt addC } add)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Add, add.Left, LogicC(i.Type, addC.Value + mask)), "xor.sign_mask_add");
            }
            if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt orC } or && LogicMaskedZero(or.Left, orC.Value, i))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Xor, or.Left, LogicC(i.Type, orC.Value ^ mask)), "xor.disjoint_or_constant");
            if (i.Left is LlvmCallBase { HasOneUse: true } count && (width & (width - 1)) == 0 && mask == new LlvmAPInt(width, width - 1)
                && count.CalledFunction?.IntrinsicDescriptor is { } descriptor && (descriptor.Kind == LlvmIntrinsicKind.CTLZ || descriptor.Kind == LlvmIntrinsicKind.CTTZ)
                && count.GetArgument(1) is LlvmConstantInt { IsOne: true } && LogicPower(count.GetArgument(0), true, i))
            {
                var opposite = descriptor.Kind == LlvmIntrinsicKind.CTLZ ? LlvmIntrinsicKind.CTTZ : LlvmIntrinsicKind.CTLZ;
                return LogicResult(LlvmIntrinsic.CreateIntrinsic(opposite, [count.GetArgument(0), LlvmConstantInt.GetTrue(i.Context)], F.Parent!, Metrics), "xor.inverse_bit_count");
            }
            if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl or LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt cShift } shift)
            {
                var allOnes = LlvmAPInt.GetAllOnes(width);
                var shiftedMask = shift.Opcode == LlvmOpcode.Shl ? allOnes.Shl(cShift.Value) : allOnes.LShr(cShift.Value);
                if (mask == shiftedMask) return LogicResult(new LlvmBinaryOperator(shift.Opcode, Builder.CreateNot(shift.Left), cShift), "xor.shifted_mask_to_not");
            }
            return null;
        }
        private static LlvmConstant LogicMergeUndefs(LlvmConstant value, LlvmConstant other) => value is LlvmUndefValue ? value : other is LlvmUndefValue ? LlvmUndefValue.Get(value.Type) : value;
        private LlvmConstant LogicConstantNot(LlvmConstant value) => LogicConstantBinary(LlvmOpcode.Xor, value, LogicOnes(value.Type));
        private LlvmConstant LogicConstantBinary(LlvmOpcode opcode, LlvmConstant a, LlvmConstant b) =>
            LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, a, b, DL, Metrics)!;
    }
}
