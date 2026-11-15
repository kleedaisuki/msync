// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldFabsWithFcmpZero/foldSqrtWithFcmpZero/foldFCmpFAbsFSubIntToFP/
// foldFCmpWithFloorAndCeil/isMinMaxCmpSelectEliminable.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmCallInstruction? FCmpIntrinsic(LlvmValue value, LlvmIntrinsicKind kind) =>
            value is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call && descriptor.Kind == kind ? call : null;

        private LlvmInstruction FCmpReplacePredicateAndLeft(LlvmFCmpInstruction comparison, LlvmCmpPredicate predicate, LlvmValue left, string reason)
        {
            comparison.Predicate = predicate;
            if (Metrics is not null) Count("fcmp.rewrite." + reason);
            return ReplaceOperand(comparison, 0, left);
        }

        private LlvmInstruction? FoldFabsWithFcmpZero(LlvmFCmpInstruction comparison)
        {
            if (FCmpIntrinsic(comparison.Left, LlvmIntrinsicKind.FAbs) is not { } absolute || comparison.Right is not LlvmConstantFP constant) return null;
            Count("fcmp.fabs_zero.queries");
            LlvmValue x = absolute.GetArgument(0);
            if (!constant.IsZero || constant.IsNegative)
            {
                if (!FCmpIsSmallestNormalized(constant.Value) || !FCmpInputFlushesDenormals(constant.Type)) return null;
                LlvmCmpPredicate? rewritten = comparison.Predicate switch
                {
                    LlvmCmpPredicate.FCmpOlt => LlvmCmpPredicate.FCmpOeq,
                    LlvmCmpPredicate.FCmpUge => LlvmCmpPredicate.FCmpUne,
                    LlvmCmpPredicate.FCmpOge => LlvmCmpPredicate.FCmpOne,
                    LlvmCmpPredicate.FCmpUlt => LlvmCmpPredicate.FCmpUeq,
                    _ => null
                };
                if (!rewritten.HasValue) return null;
                Count("fcmp.rewrite.fabs_smallest_normalized_with_flush");
                return NewFCmp(rewritten.Value, x, FCmpZero(x.Type), comparison);
            }
            LlvmCmpPredicate predicate = comparison.Predicate switch
            {
                LlvmCmpPredicate.FCmpOgt => LlvmCmpPredicate.FCmpOne,
                LlvmCmpPredicate.FCmpUgt => LlvmCmpPredicate.FCmpUne,
                LlvmCmpPredicate.FCmpOle => LlvmCmpPredicate.FCmpOeq,
                LlvmCmpPredicate.FCmpUle => LlvmCmpPredicate.FCmpUeq,
                LlvmCmpPredicate.FCmpOge => LlvmCmpPredicate.FCmpOrd,
                LlvmCmpPredicate.FCmpUlt => LlvmCmpPredicate.FCmpUno,
                LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpUeq or LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUne or
                    LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno => comparison.Predicate,
                LlvmCmpPredicate.FCmpUge or LlvmCmpPredicate.FCmpOlt => throw new UnreachableException("fabs compare should have simplified"),
                _ => comparison.Predicate
            };
            if (comparison.Predicate is LlvmCmpPredicate.FCmpTrue or LlvmCmpPredicate.FCmpFalse) return null;
            return FCmpReplacePredicateAndLeft(comparison, predicate, x, "fabs_zero");
        }

        private LlvmInstruction? FoldSqrtWithFcmpZero(LlvmFCmpInstruction comparison)
        {
            if (FCmpIntrinsic(comparison.Left, LlvmIntrinsicKind.Sqrt) is not { } squareRoot || comparison.Right is not LlvmConstantFP { IsZero: true, IsNegative: false }) return null;
            Count("fcmp.sqrt_zero.queries");
            LlvmValue x = squareRoot.GetArgument(0);
            if (!FCmpHasFlag(squareRoot, LlvmFastMathFlags.NoInfs)) comparison.FastMathFlags &= ~LlvmFastMathFlags.NoInfs;
            LlvmCmpPredicate predicate = comparison.Predicate switch
            {
                LlvmCmpPredicate.FCmpUlt or LlvmCmpPredicate.FCmpUle or LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpOge or
                    LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpUne => comparison.Predicate,
                LlvmCmpPredicate.FCmpOle => LlvmCmpPredicate.FCmpOeq,
                LlvmCmpPredicate.FCmpUgt => LlvmCmpPredicate.FCmpUne,
                LlvmCmpPredicate.FCmpUeq => LlvmCmpPredicate.FCmpUle,
                LlvmCmpPredicate.FCmpOne => LlvmCmpPredicate.FCmpOgt,
                LlvmCmpPredicate.FCmpOrd => LlvmCmpPredicate.FCmpOge,
                LlvmCmpPredicate.FCmpUno => LlvmCmpPredicate.FCmpUlt,
                _ => throw new UnreachableException("sqrt compare should have simplified")
            };
            return FCmpReplacePredicateAndLeft(comparison, predicate, x, "sqrt_zero");
        }

        private LlvmInstruction? FoldFCmpFAbsFSubIntToFP(LlvmFCmpInstruction comparison)
        {
            if (FCmpIntrinsic(comparison.Left, LlvmIntrinsicKind.FAbs) is not { } absolute ||
                comparison.Right is not LlvmConstantFP constant || constant.Value.IsNaN || constant.Value.IsInfinity || constant.IsZero) return null;
            Count("fcmp.fabs_integer_distance.queries");
            LlvmCmpPredicate predicate = comparison.Predicate;
            bool strictLess = predicate is LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpUlt;
            bool lessEqual = predicate is LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUle;
            bool strictGreater = predicate is LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpUgt;
            bool greaterEqual = predicate is LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUge;
            if (!strictLess && !strictGreater && !greaterEqual) return null;
            LlvmFloatCompare compared = constant.Value.Compare(FCmpOne(constant.Value.Semantics));
            if (strictLess && compared == LlvmFloatCompare.GreaterThan || greaterEqual && compared == LlvmFloatCompare.GreaterThan ||
                lessEqual && compared != LlvmFloatCompare.GreaterThan || strictGreater && compared != LlvmFloatCompare.LessThan) return null;
            if (absolute.GetArgument(0) is not LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmCastInstruction left, Right: LlvmCastInstruction right } subtraction ||
                left.Opcode != right.Opcode || left.Opcode is not (LlvmOpcode.SIToFP or LlvmOpcode.UIToFP) || left.Operand.Type != right.Operand.Type) return null;
            bool signed = left.Opcode == LlvmOpcode.SIToFP;
            if (!CanBeCastedExactlyIntToFP(left.Operand, subtraction.Type, signed, comparison) || !CanBeCastedExactlyIntToFP(right.Operand, subtraction.Type, signed, comparison)) return null;
            Count("fcmp.rewrite.fabs_integer_distance");
            return new LlvmICmpInstruction(strictLess || lessEqual ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe, left.Operand, right.Operand);
        }

        private LlvmInstruction? FoldFCmpWithFloorAndCeil(LlvmFCmpInstruction comparison)
        {
            LlvmValue left = comparison.Left, right = comparison.Right;
            LlvmCmpPredicate predicate = comparison.Predicate;
            bool IsUnaryOf(LlvmValue value, LlvmIntrinsicKind kind, LlvmValue operand) => FCmpIntrinsic(value, kind)?.GetArgument(0) == operand;
            bool floor = IsUnaryOf(left, LlvmIntrinsicKind.Floor, right), ceil = IsUnaryOf(left, LlvmIntrinsicKind.Ceil, right);
            if (!floor && !ceil)
            {
                floor = IsUnaryOf(right, LlvmIntrinsicKind.Floor, left);
                ceil = !floor && IsUnaryOf(right, LlvmIntrinsicKind.Ceil, left);
                if (floor || ceil) { (left, right) = (right, left); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
            }
            if (!floor && !ceil) return null;
            Count("fcmp.floor_ceil.queries");
            if (FCmpIsCommutative(predicate) && left.HasOneUse)
            {
                LlvmValue truncated = Builder.CreateIntrinsic(LlvmIntrinsicKind.Trunc, [right]);
                Count("fcmp.rewrite.floor_ceil_to_trunc");
                return NewFCmp(predicate, truncated, right, comparison);
            }
            if (floor)
            {
                switch (predicate)
                {
                    case LlvmCmpPredicate.FCmpOle: Count("fcmp.rewrite.floor_ordered"); return NewFCmp(LlvmCmpPredicate.FCmpOrd, right, FCmpZero(left.Type), comparison);
                    case LlvmCmpPredicate.FCmpOgt: return FCmpBoolean(comparison, false, "floor_never_greater");
                    case LlvmCmpPredicate.FCmpUle: return FCmpBoolean(comparison, true, "floor_unordered_less_equal");
                    case LlvmCmpPredicate.FCmpUgt: Count("fcmp.rewrite.floor_unordered"); return NewFCmp(LlvmCmpPredicate.FCmpUno, right, FCmpZero(left.Type), comparison);
                }
            }
            else
            {
                switch (predicate)
                {
                    case LlvmCmpPredicate.FCmpOge: Count("fcmp.rewrite.ceil_ordered"); return NewFCmp(LlvmCmpPredicate.FCmpOrd, right, FCmpZero(left.Type), comparison);
                    case LlvmCmpPredicate.FCmpOlt: return FCmpBoolean(comparison, false, "ceil_never_less");
                    case LlvmCmpPredicate.FCmpUge: return FCmpBoolean(comparison, true, "ceil_unordered_greater_equal");
                    case LlvmCmpPredicate.FCmpUlt: Count("fcmp.rewrite.ceil_unordered"); return NewFCmp(LlvmCmpPredicate.FCmpUno, right, FCmpZero(left.Type), comparison);
                }
            }
            return null;
        }

        private static bool IsMinMaxCmpSelectEliminable(LlvmSelectPatternFlavor flavor, LlvmValue a, LlvmValue b)
        {
            if (b is not LlvmConstantFP constant || a is not LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } conversion) return false;
            bool unsigned = conversion.Opcode == LlvmOpcode.UIToFP;
            uint width = conversion.Operand.Type.GetIntegerBitWidth();
            bool maximum = flavor == LlvmSelectPatternFlavor.FMaxNum;
            LlvmAPInt boundary = maximum ? unsigned ? LlvmAPInt.GetMinValue(width) : LlvmAPInt.GetSignedMinValue(width)
                : unsigned ? LlvmAPInt.GetMaxValue(width) : LlvmAPInt.GetSignedMaxValue(width);
            LlvmFloatIntegerResult converted = constant.Value.ConvertToInteger(width, !unsigned, LlvmRoundingMode.TowardZero);
            return converted.Status == LlvmFloatStatus.OK && converted.IsExact && converted.Value == boundary;
        }
    }
}
