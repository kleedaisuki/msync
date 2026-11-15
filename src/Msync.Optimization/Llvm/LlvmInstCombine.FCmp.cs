// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineCompares.cpp visitFCmpInst (represented scalar path).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool FCmpPositiveZero(LlvmValue value) => value is LlvmConstantFP { IsZero: true, IsNegative: false };

        private LlvmInstruction? VisitFCmpInst(LlvmFCmpInstruction comparison)
        {
            Count("fcmp.queries");
            bool changed = false;
            if (GetComplexity(comparison.Left) < GetComplexity(comparison.Right))
            { comparison.SwapOperands(); changed = true; Count("fcmp.operands_commuted"); }
            LlvmCmpPredicate predicate = comparison.Predicate;
            LlvmValue left = comparison.Left, right = comparison.Right;
            if (LlvmInstructionSimplify.SimplifyInstruction(comparison, Query(comparison)) is { } simplified)
                return ReplaceInstUsesWith(comparison, simplified);
            LlvmType operandType = left.Type;
            if (left == right)
            {
                if (predicate is LlvmCmpPredicate.FCmpUno or LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUgt or LlvmCmpPredicate.FCmpUne)
                {
                    comparison.Predicate = LlvmCmpPredicate.FCmpUno;
                    comparison.Right = FCmpZero(operandType);
                    Count("fcmp.rewrite.self_to_unordered"); return comparison;
                }
                if (predicate is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpOle)
                {
                    comparison.Predicate = LlvmCmpPredicate.FCmpOrd;
                    comparison.Right = FCmpZero(operandType);
                    Count("fcmp.rewrite.self_to_ordered"); return comparison;
                }
            }
            if (FCmpIsCommutative(predicate) && MatchSymmetricPair(left, right) is { } pair)
            {
                ReplaceOperand(comparison, 0, pair.First); ReplaceOperand(comparison, 1, pair.Second);
                Count("fcmp.rewrite.symmetric_pair"); return comparison;
            }
            if (predicate is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno)
            {
                if (!FCmpPositiveZero(left) && FCmpNeverNaN(left, comparison)) return ReplaceOperand(comparison, 0, FCmpZero(operandType));
                if (!FCmpPositiveZero(right) && FCmpNeverNaN(right, comparison)) return ReplaceOperand(comparison, 1, FCmpZero(operandType));
            }
            if (FCmpNegatedOperand(left) is { } negativeLeft && FCmpNegatedOperand(right) is { } negativeRight)
            {
                Count("fcmp.rewrite.negated_pair");
                return NewFCmp(LlvmCmpInstruction.GetSwappedPredicate(predicate), negativeLeft, negativeRight, comparison);
            }
            if (FoldFCmpFNegCommonOp(comparison) is { } commonNegation) return commonNegation;
            if (comparison.HasOneUse && comparison.Users.Single() is LlvmSelectInstruction selectUser)
            {
                LlvmSelectPatternResult pattern = LlvmSelectPattern.Match(selectUser, Metrics);
                bool redundant = (pattern.Flavor is LlvmSelectPatternFlavor.FMaxNum or LlvmSelectPatternFlavor.FMinNum) &&
                    IsMinMaxCmpSelectEliminable(pattern.Flavor, pattern.LHS!, pattern.RHS!);
                if (pattern.Flavor != LlvmSelectPatternFlavor.Unknown && !redundant)
                { Count("fcmp.retained.minmax_select_idiom"); return null; }
            }
            if (right is LlvmConstantFP { IsZero: true, IsNegative: true }) return ReplaceOperand(comparison, 1, FCmpZero(operandType));
            if (right is LlvmConstantFP { Value.IsInfinity: true } infinity)
            {
                LlvmCmpPredicate normalized = infinity.IsNegative ? LlvmCmpInstruction.GetSwappedPredicate(predicate) : predicate;
                Count("fcmp.infinity.queries");
                switch (normalized)
                {
                    case LlvmCmpPredicate.FCmpOlt: return NewFCmp(LlvmCmpPredicate.FCmpOne, left, right, comparison);
                    case LlvmCmpPredicate.FCmpOle: return NewFCmp(LlvmCmpPredicate.FCmpOrd, left, FCmpZero(operandType), comparison);
                    case LlvmCmpPredicate.FCmpOge: return NewFCmp(LlvmCmpPredicate.FCmpOeq, left, right, comparison);
                    case LlvmCmpPredicate.FCmpUlt: return NewFCmp(LlvmCmpPredicate.FCmpUne, left, right, comparison);
                    case LlvmCmpPredicate.FCmpUgt: return NewFCmp(LlvmCmpPredicate.FCmpUno, left, FCmpZero(operandType), comparison);
                    case LlvmCmpPredicate.FCmpUge: return NewFCmp(LlvmCmpPredicate.FCmpUeq, left, right, comparison);
                    case LlvmCmpPredicate.FCmpOrd: case LlvmCmpPredicate.FCmpUno: case LlvmCmpPredicate.FCmpTrue:
                    case LlvmCmpPredicate.FCmpFalse: case LlvmCmpPredicate.FCmpOgt: case LlvmCmpPredicate.FCmpUle:
                        throw new UnreachableException("infinity comparison should have simplified");
                }
            }
            if (FCmpPositiveZero(right) && left is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true } bitcast &&
                bitcast.Operand.Type.IsIntegerTy() && !FCmpInputsMayBeZero(operandType) &&
                predicate is LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpUne)
            {
                LlvmValue integer = bitcast.Operand;
                LlvmAPInt mask = ~LlvmAPInt.GetSignedMinValue(integer.Type.GetIntegerBitWidth());
                LlvmValue masked = Builder.CreateAnd(integer, LlvmConstantInt.Get(integer.Context, mask));
                Count("fcmp.rewrite.bitcast_integer_magnitude_zero");
                return new LlvmICmpInstruction(predicate == LlvmCmpPredicate.FCmpOeq ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe,
                    masked, LlvmConstant.GetNullValue(integer.Type));
            }
            if (left is LlvmInstruction leftInstruction && right is LlvmConstant rightConstant)
            {
                LlvmInstruction? folded = FoldFCmpInstructionConstant(comparison, leftInstruction, rightConstant);
                if (folded is not null) return folded;
            }
            if (FoldFabsWithFcmpZero(comparison) is { } absolute) return absolute;
            if (FoldFCmpFAbsFSubIntToFP(comparison) is { } distance) return distance;
            if (FoldSqrtWithFcmpZero(comparison) is { } squareRoot) return squareRoot;
            if (FoldFCmpWithFloorAndCeil(comparison) is { } integral) return integral;
            if (FoldCmpSelectOfConstants(comparison) is { } table) return table;
            if (FCmpNegatedOperand(left) is { } x && right is LlvmConstant constant &&
                LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, constant, DL, Metrics) is { } negated)
            {
                Count("fcmp.rewrite.negated_constant");
                return NewFCmp(LlvmCmpInstruction.GetSwappedPredicate(predicate), x, negated, comparison);
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FAdd, Right: LlvmConstantFP { IsZero: true } } addLeft)
            { Count("fcmp.rewrite.add_zero_left"); return NewFCmp(predicate, addLeft.Left, right, comparison); }
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FAdd, Right: LlvmConstantFP { IsZero: true } } addRight)
            { Count("fcmp.rewrite.add_zero_right"); return NewFCmp(predicate, left, addRight.Left, comparison); }
            if ((predicate is LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno) &&
                left is LlvmCastInstruction { Opcode: LlvmOpcode.FPTrunc } truncLeft &&
                right is LlvmCastInstruction { Opcode: LlvmOpcode.FPTrunc } truncRight && truncLeft.Operand.Type == truncRight.Operand.Type)
            { Count("fcmp.rewrite.trunc_ordered_pair"); return NewFCmp(predicate, truncLeft.Operand, truncRight.Operand, comparison); }
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } extension)
            {
                if (right is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } other && extension.Operand.Type == other.Operand.Type)
                { Count("fcmp.rewrite.extended_pair"); return NewFCmp(predicate, extension.Operand, other.Operand, comparison); }
                if (right is LlvmConstantFP floating && FoldFCmpExtensionConstant(comparison, extension, floating) is { } reduced) return reduced;
            }
            if (FCmpIntrinsic(left, LlvmIntrinsicKind.CopySign) is { HasOneUse: true } copy && copy.GetArgument(0) is LlvmConstantFP magnitude &&
                right is LlvmConstantFP { IsZero: true } && !magnitude.IsZero && !magnitude.IsNaN && predicate == LlvmCmpPredicate.FCmpOlt)
            {
                LlvmValue integer = Builder.CreateBitCast(copy.GetArgument(1), LlvmIntegerType.Get(copy.Context, (uint)operandType.GetPrimitiveSizeInBits().GetFixedValue()));
                Count("fcmp.rewrite.copysign_integer_sign_test");
                return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, integer, LlvmConstant.GetNullValue(integer.Type));
            }
            LlvmValue? canonicalLeft = FCmpIntrinsic(left, LlvmIntrinsicKind.Canonicalize)?.GetArgument(0);
            if (canonicalLeft == right) return NewFCmp(predicate, right, right, comparison);
            LlvmValue? canonicalRight = FCmpIntrinsic(right, LlvmIntrinsicKind.Canonicalize)?.GetArgument(0);
            if (canonicalRight == left) return NewFCmp(predicate, left, left, comparison);
            if (canonicalLeft is not null && canonicalRight is not null) return NewFCmp(predicate, canonicalLeft, canonicalRight, comparison);
            Count(changed ? "fcmp.changed_in_place" : "fcmp.unchanged");
            return changed ? comparison : null;
        }

        private LlvmInstruction? FoldFCmpInstructionConstant(LlvmFCmpInstruction comparison, LlvmInstruction left, LlvmConstant right)
        {
            switch (left)
            {
                case LlvmSelectInstruction select:
                    if (FCmpIsEquality(comparison.Predicate) && right is LlvmConstantFP { IsZero: true })
                    {
                        if (FCmpNegatedOperand(select.TrueValue) is { } positive && positive == select.FalseValue) return ReplaceOperand(comparison, 0, positive);
                        if (FCmpNegatedOperand(select.FalseValue) is { } otherPositive && otherPositive == select.TrueValue) return ReplaceOperand(comparison, 0, otherPositive);
                    }
                    return FoldOpIntoSelect(comparison, select);
                case LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, HasOneUse: true }: return FoldFCmpFSubIntoFCmp(comparison, left, right);
                case LlvmPhiInstruction phi: return FoldOpIntoPhi(comparison, phi);
                case LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP }: return FoldFCmpIntToFPConst(comparison, left, right);
                case LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv }: return FoldFCmpReciprocalAndZero(comparison, left, right);
                case LlvmLoadInstruction { PointerOperand: LlvmGetElementPtrInstruction gep } load:
                    return FoldCmpLoadFromIndexedGlobal(load, gep, comparison.Predicate, right, comparison);
                case LlvmCastInstruction { Opcode: LlvmOpcode.FPTrunc } trunc: return FoldFCmpFpTrunc(comparison, trunc, right);
                default: return null;
            }
        }
    }
}
