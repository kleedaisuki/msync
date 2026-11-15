// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineCompares.cpp foldFCmpIntToFPConst.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool FCmpIsEquality(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.FCmpOeq or LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUeq or LlvmCmpPredicate.FCmpUne;
        private static bool FCmpIsCommutative(LlvmCmpPredicate predicate) => FCmpIsEquality(predicate) || predicate is LlvmCmpPredicate.FCmpFalse or LlvmCmpPredicate.FCmpTrue or LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno;
        private static LlvmConstantFP FCmpZero(LlvmType type) => LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(type.GetScalarType().GetFltSemantics()));
        private static LlvmFCmpInstruction NewFCmp(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right, LlvmFCmpInstruction? flagsSource = null) =>
            new(predicate, left, right) { FastMathFlags = flagsSource?.FastMathFlags ?? LlvmFastMathFlags.None };

        private LlvmInstruction? FCmpBoolean(LlvmFCmpInstruction comparison, bool value, string reason)
        {
            if (Metrics is not null) Count("fcmp.rewrite." + reason);
            return ReplaceInstUsesWith(comparison, LlvmConstantInt.GetBool(comparison.Context, value));
        }

        private LlvmInstruction? FoldFCmpIntToFPConst(LlvmFCmpInstruction comparison, LlvmInstruction leftInstruction, LlvmConstant rightConstant)
        {
            Count("fcmp.int_to_fp.queries");
            if (rightConstant is not LlvmConstantFP floating) return null;
            LlvmAPFloat right = floating.Value;
            int mantissa = leftInstruction.Type.GetFPMantissaWidth();
            if (mantissa == -1) return null;
            LlvmValue integer = leftInstruction.GetOperand(0)!;
            uint width = integer.Type.GetIntegerBitWidth();
            bool unsigned = leftInstruction.Opcode == LlvmOpcode.UIToFP;
            if (FCmpIsEquality(comparison.Predicate))
            {
                LlvmFloatIntegerResult converted = right.ConvertToInteger(width, !unsigned, LlvmRoundingMode.NearestTiesToEven);
                if (!converted.IsExact)
                {
                    LlvmAPFloat rounded = right.RoundToIntegral(LlvmRoundingMode.NearestTiesToEven).Value;
                    if (right.Compare(rounded) != LlvmFloatCompare.Equal)
                        return FCmpBoolean(comparison, comparison.Predicate is LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUne, "fractional_equality");
                }
            }
            if (width > mantissa)
            {
                int exponent = right.ILogB();
                if (exponent == LlvmAPFloat.IEK_Inf)
                {
                    int maximumExponent = LlvmAPFloat.GetLargest(right.Semantics).ILogB();
                    if (maximumExponent < width - (unsigned ? 0 : 1)) { Count("fcmp.int_to_fp.retained_conversion_may_create_infinity"); return null; }
                }
                else if (mantissa <= exponent && exponent <= width - (unsigned ? 0 : 1))
                { Count("fcmp.int_to_fp.retained_rounding_affects_comparison"); return null; }
            }
            Debug.Assert(!right.IsNaN);
            LlvmCmpPredicate predicate;
            switch (comparison.Predicate)
            {
                case LlvmCmpPredicate.FCmpUeq: case LlvmCmpPredicate.FCmpOeq: predicate = LlvmCmpPredicate.ICmpEq; break;
                case LlvmCmpPredicate.FCmpUgt: case LlvmCmpPredicate.FCmpOgt: predicate = unsigned ? LlvmCmpPredicate.ICmpUgt : LlvmCmpPredicate.ICmpSgt; break;
                case LlvmCmpPredicate.FCmpUge: case LlvmCmpPredicate.FCmpOge: predicate = unsigned ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpSge; break;
                case LlvmCmpPredicate.FCmpUlt: case LlvmCmpPredicate.FCmpOlt: predicate = unsigned ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpSlt; break;
                case LlvmCmpPredicate.FCmpUle: case LlvmCmpPredicate.FCmpOle: predicate = unsigned ? LlvmCmpPredicate.ICmpUle : LlvmCmpPredicate.ICmpSle; break;
                case LlvmCmpPredicate.FCmpUne: case LlvmCmpPredicate.FCmpOne: predicate = LlvmCmpPredicate.ICmpNe; break;
                case LlvmCmpPredicate.FCmpOrd: return FCmpBoolean(comparison, true, "int_to_fp_ordered");
                case LlvmCmpPredicate.FCmpUno: return FCmpBoolean(comparison, false, "int_to_fp_unordered");
                default: throw new UnreachableException();
            }
            LlvmAPInt maximum = unsigned ? LlvmAPInt.GetMaxValue(width) : LlvmAPInt.GetSignedMaxValue(width);
            LlvmAPFloat floatingMaximum = LlvmAPFloat.FromAPInt(right.Semantics, maximum, !unsigned, LlvmRoundingMode.NearestTiesToEven).Value;
            if (floatingMaximum.Compare(right) == LlvmFloatCompare.LessThan)
                return FCmpBoolean(comparison, predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle, "above_integer_maximum");
            LlvmAPInt minimum = unsigned ? LlvmAPInt.GetMinValue(width) : LlvmAPInt.GetSignedMinValue(width);
            LlvmAPFloat floatingMinimum = LlvmAPFloat.FromAPInt(right.Semantics, minimum, !unsigned, LlvmRoundingMode.NearestTiesToEven).Value;
            if (floatingMinimum.Compare(right) == LlvmFloatCompare.GreaterThan)
                return FCmpBoolean(comparison, predicate is LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge, "below_integer_minimum");
            LlvmFloatIntegerResult integral = right.ConvertToInteger(width, !unsigned, LlvmRoundingMode.TowardZero);
            if (!right.IsZero && !integral.IsExact)
            {
                Count("fcmp.int_to_fp.fractional_predicate_adjustment");
                switch (predicate)
                {
                    case LlvmCmpPredicate.ICmpNe: return FCmpBoolean(comparison, true, "fractional_inequality");
                    case LlvmCmpPredicate.ICmpEq: return FCmpBoolean(comparison, false, "fractional_equality");
                    case LlvmCmpPredicate.ICmpUle: if (right.IsNegative) return FCmpBoolean(comparison, false, "unsigned_fractional_negative"); break;
                    case LlvmCmpPredicate.ICmpSle: if (right.IsNegative) predicate = LlvmCmpPredicate.ICmpSlt; break;
                    case LlvmCmpPredicate.ICmpUlt:
                        if (right.IsNegative) return FCmpBoolean(comparison, false, "unsigned_fractional_negative");
                        predicate = LlvmCmpPredicate.ICmpUle; break;
                    case LlvmCmpPredicate.ICmpSlt: if (!right.IsNegative) predicate = LlvmCmpPredicate.ICmpSle; break;
                    case LlvmCmpPredicate.ICmpUgt: if (right.IsNegative) return FCmpBoolean(comparison, true, "unsigned_fractional_negative"); break;
                    case LlvmCmpPredicate.ICmpSgt: if (right.IsNegative) predicate = LlvmCmpPredicate.ICmpSge; break;
                    case LlvmCmpPredicate.ICmpUge:
                        if (right.IsNegative) return FCmpBoolean(comparison, true, "unsigned_fractional_negative");
                        predicate = LlvmCmpPredicate.ICmpUgt; break;
                    case LlvmCmpPredicate.ICmpSge: if (!right.IsNegative) predicate = LlvmCmpPredicate.ICmpSgt; break;
                    default: throw new UnreachableException();
                }
            }
            Count("fcmp.rewrite.int_to_integer_compare");
            return new LlvmICmpInstruction(predicate, integer, LlvmConstantInt.Get(integer.Context, integral.Value));
        }
    }
}
