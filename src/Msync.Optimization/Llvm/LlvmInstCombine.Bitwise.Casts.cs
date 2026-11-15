// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// shouldOptimizeCast / foldLogicCastConstant / foldCastedBitwiseLogic / narrowMaskedBinOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool ShouldOptimizeCast(LlvmCastInstruction cast)
        {
            if (cast.Operand.Type == cast.Type || cast.Operand is LlvmConstant) return false;
            return cast.Operand is not LlvmCastInstruction preceding || IsEliminableCastPair(preceding, cast) is null;
        }
        private LlvmInstruction? FoldLogicCastConstant(LlvmBinaryOperator logic, LlvmCastInstruction cast)
        {
            if (logic.Right is not LlvmConstant constant || !cast.HasOneUse) return null;
            if (cast.Opcode == LlvmOpcode.ZExt)
            {
                var flags = new PreservedCastFlags();
                if (GetLosslessUnsignedTrunc(constant, cast.Operand.Type, DL, flags) is { } narrow)
                {
                    LlvmValue newOp = Builder.CreateBinOp(logic.Opcode, cast.Operand, narrow);
                    return LogicResult(new LlvmCastInstruction(LlvmOpcode.ZExt, newOp, logic.Type) { IsNonNegative = flags.NNeg && cast.IsNonNegative }, "cast_constant_zext");
                }
            }
            if ((cast.Opcode == LlvmOpcode.SExt || (cast.Opcode == LlvmOpcode.ZExt && cast.IsNonNegative)) && GetLosslessSignedTrunc(constant, cast.Operand.Type, DL) is { } signed)
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.SExt, Builder.CreateBinOp(logic.Opcode, cast.Operand, signed), logic.Type), "cast_constant_sext");
            return null;
        }
        private LlvmInstruction? FoldCastedBitwiseLogic(LlvmBinaryOperator instruction)
        {
            LlvmInstruction? FoldSignCompare(LlvmValue left, LlvmValue right)
            {
                if (left is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt amount } shift || amount.Value.GetLimitedValue(left.Type.GetScalarSizeInBits()) != left.Type.GetScalarSizeInBits() - 1 ||
                    right is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true, Operand: LlvmICmpInstruction compare }) return null;
                LlvmValue negative = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, shift.Left, LlvmConstant.GetNullValue(shift.Left.Type));
                LlvmValue boolean = Builder.CreateBinOp(instruction.Opcode, negative, compare);
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.ZExt, boolean, left.Type), "cast_bitwise_sign_icmp");
            }
            if (FoldSignCompare(instruction.Left, instruction.Right) is { } first) return first;
            if (FoldSignCompare(instruction.Right, instruction.Left) is { } second) return second;
            if (instruction.Left is not LlvmCastInstruction cast0 || !cast0.Operand.Type.IsIntegerTy()) return null;
            if (FoldLogicCastConstant(instruction, cast0) is { } constant) return constant;
            if (instruction.Right is not LlvmCastInstruction cast1 || cast0.Opcode != cast1.Opcode || (!cast0.HasOneUse && !cast1.HasOneUse)) return null;
            LlvmValue x = cast0.Operand, y = cast1.Operand;
            if (cast0.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt)
            {
                uint xWidth = x.Type.GetScalarSizeInBits(), yWidth = y.Type.GetScalarSizeInBits();
                if (xWidth != yWidth)
                {
                    if (!cast0.HasOneUse || !cast1.HasOneUse) return null;
                    if (xWidth < yWidth) x = Builder.CreateCast(cast0.Opcode, x, y.Type);
                    else y = Builder.CreateCast(cast0.Opcode, y, x.Type);
                }
                LlvmValue narrow = Builder.CreateBinOp(instruction.Opcode, x, y, instruction.Name);
                if (instruction.Opcode == LlvmOpcode.Or && narrow is LlvmBinaryOperator newOr) newOr.IsDisjoint = instruction.IsDisjoint;
                return LogicResult(new LlvmCastInstruction(cast0.Opcode, narrow, instruction.Type), "cast_matching_extensions");
            }
            if (x.Type != y.Type || !ShouldOptimizeCast(cast0) || !ShouldOptimizeCast(cast1)) return null;
            LlvmValue operation = Builder.CreateBinOp(instruction.Opcode, x, y, instruction.Name);
            var result = new LlvmCastInstruction(cast0.Opcode, operation, instruction.Type);
            if (result.Opcode == LlvmOpcode.Trunc)
            {
                result.HasNoUnsignedWrap = instruction.Opcode == LlvmOpcode.And ? cast0.HasNoUnsignedWrap || cast1.HasNoUnsignedWrap : cast0.HasNoUnsignedWrap && cast1.HasNoUnsignedWrap;
                result.HasNoSignedWrap = cast0.HasNoSignedWrap && cast1.HasNoSignedWrap;
            }
            return LogicResult(result, "cast_matching_logic");
        }
        private static bool CanNarrowShiftAmt(LlvmConstant value, uint width) => value is LlvmConstantInt constant && constant.Value.GetLimitedValue(width) < width;
        private LlvmInstruction? NarrowMaskedBinOp(LlvmBinaryOperator and)
        {
            LlvmValue operand = and.Left, common = and.Right;
            if (operand is not LlvmBinaryOperator { HasOneUse: true } binary ||
                common is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extension || common.HasNUsesOrMore(3)) return null;
            LlvmConstant? constant = null;
            if (binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Mul or LlvmOpcode.Shl or LlvmOpcode.LShr && ReferenceEquals(binary.Left, common)) constant = binary.Right as LlvmConstant;
            if (binary.Opcode == LlvmOpcode.Sub && ReferenceEquals(binary.Right, common)) constant = binary.Left as LlvmConstant;
            if (constant is null || !ShouldChangeType(and.Type, extension.Operand.Type)) return null;
            if (binary.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr && !CanNarrowShiftAmt(constant, extension.Operand.Type.GetScalarSizeInBits())) return null;
            LlvmValue narrowed = LlvmConstantExpr.GetTrunc(constant, extension.Operand.Type);
            LlvmValue newBinary = binary.Opcode == LlvmOpcode.Sub ? Builder.CreateBinOp(binary.Opcode, narrowed, extension.Operand) : Builder.CreateBinOp(binary.Opcode, extension.Operand, narrowed);
            return LogicResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateAnd(newBinary, extension.Operand), and.Type), "narrow_masked_binop");
        }
    }
}
