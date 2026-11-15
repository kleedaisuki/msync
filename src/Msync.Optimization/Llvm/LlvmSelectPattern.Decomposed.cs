// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// matchDecomposedSelectPattern, lookThroughCast/Const (represented scalar
// comparison/cast paths), getMinMaxPred/getInverseMinMaxFlavor/getMinMaxLimit.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSelectPattern
{
    public static LlvmSelectPatternResult MatchDecomposed(LlvmCmpInstruction comparison,
        LlvmValue whenTrue, LlvmValue whenFalse, LlvmFastMathFlags flags = LlvmFastMathFlags.None,
        bool lookThroughCasts = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "select_pattern.decomposed.queries");
        if (comparison is not LlvmICmpInstruction || !comparison.Left.Type.IsIntegerTy())
        { Count(metrics, "select_pattern.decomposed.unported_fp_or_vector"); return default; }
        LlvmCmpPredicate predicate = comparison.Predicate;
        if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) return default;
        LlvmValue cmpLeft = comparison.Left, cmpRight = comparison.Right;
        LlvmOpcode? castOpcode = null;
        if (lookThroughCasts && !ReferenceEquals(cmpLeft.Type, whenTrue.Type))
        {
            LlvmValue? casted = LookThroughCast(comparison, whenTrue, whenFalse, ref castOpcode, metrics);
            if (casted is not null)
                return MatchInteger(predicate, cmpLeft, cmpRight, ((LlvmCastInstruction)whenTrue).Operand, casted, metrics)
                    with { CastOpcode = castOpcode };
            casted = LookThroughCast(comparison, whenFalse, whenTrue, ref castOpcode, metrics);
            if (casted is not null)
                return MatchInteger(predicate, cmpLeft, cmpRight, casted, ((LlvmCastInstruction)whenFalse).Operand, metrics)
                    with { CastOpcode = castOpcode };
        }
        return MatchInteger(predicate, cmpLeft, cmpRight, whenTrue, whenFalse, metrics) with { CastOpcode = castOpcode };
    }
    private static LlvmValue? LookThroughCast(LlvmCmpInstruction comparison, LlvmValue first, LlvmValue second,
        ref LlvmOpcode? opcode, Dictionary<string, ulong>? metrics)
    {
        if (first is not LlvmCastInstruction cast) return null;
        Count(metrics, "select_pattern.cast.queries");
        opcode = cast.Opcode;
        LlvmType sourceType = cast.Operand.Type;
        if (second is LlvmCastInstruction other)
            return opcode == other.Opcode && ReferenceEquals(sourceType, other.Operand.Type) ? other.Operand : null;
        if (second is LlvmConstant constant) return LookThroughCastConstant(comparison, sourceType, constant, opcode.Value, metrics);
        if (opcode == LlvmOpcode.Trunc && comparison.Right is LlvmCastInstruction
            { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } extended && ReferenceEquals(extended.Operand, second))
        {
            Debug.Assert(ReferenceEquals(second.Type, cast.Type));
            return comparison.Right;
        }
        return null;
    }
    private static LlvmConstant? LookThroughCastConstant(LlvmCmpInstruction comparison, LlvmType sourceType,
        LlvmConstant constant, LlvmOpcode opcode, Dictionary<string, ulong>? metrics)
    {
        LlvmDataLayout layout = comparison.Parent!.Parent!.Parent!.DataLayout;
        bool signed = comparison.Predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge or
            LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle;
        bool unsigned = comparison.Predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge or
            LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle;
        LlvmConstant? casted = null;
        switch (opcode)
        {
            case LlvmOpcode.ZExt:
                if (unsigned) casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.Trunc, constant, sourceType, layout, metrics);
                break;
            case LlvmOpcode.SExt:
                if (signed) casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.Trunc, constant, sourceType, layout, metrics);
                break;
            case LlvmOpcode.Trunc:
                casted = comparison.Right is LlvmConstant bound && ReferenceEquals(bound.Type, sourceType) ? bound :
                    LlvmConstantFolding.ConstantFoldCastOperand(signed ? LlvmOpcode.SExt : LlvmOpcode.ZExt, constant, sourceType, layout, metrics);
                break;
            case LlvmOpcode.FPTrunc:
                casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.FPExt, constant, sourceType, layout, metrics); break;
            case LlvmOpcode.FPExt:
                casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.FPTrunc, constant, sourceType, layout, metrics); break;
            case LlvmOpcode.FPToUI:
                casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.UIToFP, constant, sourceType, layout, metrics); break;
            case LlvmOpcode.FPToSI:
                casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.SIToFP, constant, sourceType, layout, metrics); break;
            case LlvmOpcode.UIToFP:
                casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.FPToUI, constant, sourceType, layout, metrics); break;
            case LlvmOpcode.SIToFP:
                casted = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.FPToSI, constant, sourceType, layout, metrics); break;
        }
        if (casted is null) return null;
        LlvmConstant? castBack = LlvmConstantFolding.ConstantFoldCastOperand(opcode, casted, constant.Type, layout, metrics);
        if (castBack is not null && !ReferenceEquals(castBack, constant))
        { Count(metrics, "select_pattern.cast.roundtrip_changed"); return null; }
        return casted;
    }
    public static LlvmCmpPredicate GetMinMaxPred(LlvmSelectPatternFlavor flavor, bool ordered = false) => flavor switch
    {
        LlvmSelectPatternFlavor.SMin => LlvmCmpPredicate.ICmpSlt,
        LlvmSelectPatternFlavor.UMin => LlvmCmpPredicate.ICmpUlt,
        LlvmSelectPatternFlavor.SMax => LlvmCmpPredicate.ICmpSgt,
        LlvmSelectPatternFlavor.UMax => LlvmCmpPredicate.ICmpUgt,
        LlvmSelectPatternFlavor.FMinNum => ordered ? LlvmCmpPredicate.FCmpOlt : LlvmCmpPredicate.FCmpUlt,
        LlvmSelectPatternFlavor.FMaxNum => ordered ? LlvmCmpPredicate.FCmpOgt : LlvmCmpPredicate.FCmpUgt,
        _ => throw new UnreachableException("Unhandled select pattern flavor")
    };
    public static LlvmSelectPatternFlavor GetInverseMinMaxFlavor(LlvmSelectPatternFlavor flavor) => flavor switch
    {
        LlvmSelectPatternFlavor.SMin => LlvmSelectPatternFlavor.SMax,
        LlvmSelectPatternFlavor.UMin => LlvmSelectPatternFlavor.UMax,
        LlvmSelectPatternFlavor.SMax => LlvmSelectPatternFlavor.SMin,
        LlvmSelectPatternFlavor.UMax => LlvmSelectPatternFlavor.UMin,
        _ => throw new UnreachableException("Unhandled select pattern flavor")
    };
    public static LlvmAPInt GetMinMaxLimit(LlvmSelectPatternFlavor flavor, uint width) => flavor switch
    {
        LlvmSelectPatternFlavor.SMax => LlvmAPInt.GetSignedMaxValue(width),
        LlvmSelectPatternFlavor.SMin => LlvmAPInt.GetSignedMinValue(width),
        LlvmSelectPatternFlavor.UMax => LlvmAPInt.GetMaxValue(width),
        LlvmSelectPatternFlavor.UMin => LlvmAPInt.GetMinValue(width),
        _ => throw new UnreachableException("Unexpected select pattern flavor")
    };
}
