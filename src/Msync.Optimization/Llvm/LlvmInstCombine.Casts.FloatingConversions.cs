// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 2512–2738.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitFPExt(LlvmCastInstruction instruction)
        {
            Count("casts.fpext.queries");
            if (instruction.Operand is LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } cast && IsKnownExactCastIntToFP(cast))
            { Count("casts.fpext.integer_direct"); return new LlvmCastInstruction(cast.Opcode, cast.Operand, instruction.Type); }
            return CommonCastTransforms(instruction);
        }
        private LlvmInstruction? FoldItoFPtoI(LlvmCastInstruction instruction)
        {
            Count("casts.int_fp_int.queries");
            if (instruction.Operand is not LlvmCastInstruction { Opcode: LlvmOpcode.UIToFP or LlvmOpcode.SIToFP } cast) return null;
            if (!IsKnownExactCastIntToFP(cast) && (int)instruction.Type.GetScalarSizeInBits() > cast.Type.GetFPMantissaWidth()) return null;
            var value = cast.Operand;
            uint from = value.Type.GetScalarSizeInBits(), to = instruction.Type.GetScalarSizeInBits();
            Count("casts.int_fp_int.rewrites");
            if (to > from)
                return new LlvmCastInstruction(cast.Opcode == LlvmOpcode.SIToFP && instruction.Opcode == LlvmOpcode.FPToSI ? LlvmOpcode.SExt : LlvmOpcode.ZExt, value, instruction.Type);
            if (to < from) return new LlvmCastInstruction(LlvmOpcode.Trunc, value, instruction.Type);
            return ReplaceInstUsesWith(instruction, value);
        }
        private LlvmInstruction? FoldFPtoI(LlvmCastInstruction instruction)
        {
            Count("casts.fp_to_int.queries");
            var mask = instruction.Opcode == LlvmOpcode.FPToUI ? LlvmFPClassTest.PosNormal : LlvmFPClassTest.Normal;
            var known = LlvmKnownFPClasses.Compute(instruction.Operand, mask, instruction, DT, Metrics,
                assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC);
            if (known.IsKnownNever(mask))
            { Count("casts.fp_to_int.non_normal_zero"); return ReplaceInstUsesWith(instruction, LlvmConstant.GetNullValue(instruction.Type)); }
            if (instruction.Operand is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.Trunc)
            { Count("casts.fp_to_int.remove_trunc"); return ReplaceOperand(instruction, 0, call.GetArgument(0)); }
            bool signed = instruction.Opcode == LlvmOpcode.FPToSI;
            if (instruction.Operand is not LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, HasOneUse: true } division ||
                division.Left is not LlvmCastInstruction cast || cast.Opcode != (signed ? LlvmOpcode.SIToFP : LlvmOpcode.UIToFP) ||
                division.Right is not LlvmConstantFP constant || instruction.Type != cast.Operand.Type) return null;
            uint width = cast.Operand.Type.GetScalarSizeInBits();
            if (LlvmAPFloat.SemanticsPrecision(constant.Value.Semantics) + (signed ? 1u : 0) < width || !constant.Value.IsInteger()) return null;
            var divisor = constant.Value.ConvertToInteger(width, signed, LlvmRoundingMode.TowardZero);
            if (!divisor.IsExact || divisor.Value.IsZero || signed && divisor.Value.IsAllOnes) return null;
            Count("casts.fp_to_int.integer_division");
            return new LlvmBinaryOperator(signed ? LlvmOpcode.SDiv : LlvmOpcode.UDiv, cast.Operand, LlvmConstantInt.Get(instruction.Context, divisor.Value));
        }
        private LlvmInstruction? VisitFPToUI(LlvmCastInstruction instruction) =>
            FoldItoFPtoI(instruction) ?? FoldFPtoI(instruction) ?? CommonCastTransforms(instruction);
        private LlvmInstruction? VisitFPToSI(LlvmCastInstruction instruction) =>
            FoldItoFPtoI(instruction) ?? FoldFPtoI(instruction) ?? CommonCastTransforms(instruction);
        private LlvmInstruction? VisitUIToFP(LlvmCastInstruction instruction)
        {
            Count("casts.uitofp.queries");
            if (CommonCastTransforms(instruction) is { } common) return common;
            // Source query has no instruction context here.
            if (!instruction.IsNonNegative && IsKnownNonNegative(instruction.Operand))
            { instruction.IsNonNegative = true; Count("casts.uitofp.nonnegative"); return instruction; }
            if (instruction.Operand is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } logic ||
                logic.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true } trunc ||
                logic.Right is not LlvmConstantData mask || DL.IsLegalInteger(logic.Type.GetScalarSizeInBits()) ||
                !ShouldChangeType(logic.Type.GetScalarSizeInBits(), trunc.Operand.Type.GetScalarSizeInBits())) return null;
            var masked = Builder.CreateAnd(trunc.Operand, Builder.CreateZExt(mask, trunc.Operand.Type));
            Count("casts.uitofp.widen_mask");
            return new LlvmCastInstruction(LlvmOpcode.UIToFP, masked, instruction.Type) { IsNonNegative = instruction.IsNonNegative };
        }
        private LlvmInstruction? VisitSIToFP(LlvmCastInstruction instruction)
        {
            Count("casts.sitofp.queries");
            if (CommonCastTransforms(instruction) is { } common) return common;
            if (!IsKnownNonNegative(instruction.Operand)) return null;
            Count("casts.sitofp.to_unsigned");
            return new LlvmCastInstruction(LlvmOpcode.UIToFP, instruction.Operand, instruction.Type)
            { IsNonNegative = true, FastMathFlags = instruction.FastMathFlags & (LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoSignedZeros) };
        }
    }
}
