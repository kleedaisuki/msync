// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// foldFBinOpOfIntCasts and foldFBinOpOfIntCastsFromSign.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldFBinOpOfIntCasts(LlvmBinaryOperator operation)
        {
            Count("instcombine.fp_integer_casts.visits");
            // The source excludes vectors because integer arithmetic may cost more.
            if (operation.Type is LlvmVectorType) return null;
            if (operation.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } lhs) return null;
            LlvmConstant? fpConstant = operation.Right as LlvmConstant;
            LlvmValue? rhs = (operation.Right as LlvmCastInstruction) is { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } cast ? cast.Operand : null;
            if (fpConstant is null && rhs is null) return null;
            LlvmKnownBits?[] known = [null, null];
            return FoldFBinOpOfIntCastsFromSign(operation, false, lhs.Operand, rhs, fpConstant, known)
                ?? FoldFBinOpOfIntCastsFromSign(operation, true, lhs.Operand, rhs, fpConstant, known);
        }

        private LlvmInstruction? FoldFBinOpOfIntCastsFromSign(LlvmBinaryOperator operation,
            bool fromSigned, LlvmValue lhs, LlvmValue? rhs, LlvmConstant? fpConstant, LlvmKnownBits?[] known)
        {
            Count(fromSigned ? "instcombine.fp_integer_casts.signed_attempts" : "instcombine.fp_integer_casts.unsigned_attempts");
            LlvmType intType = lhs.Type, fpType = operation.Type;
            uint width = intType.GetScalarSizeInBits();
            uint precision = LlvmAPFloat.SemanticsPrecision(fpType.GetScalarType().GetFltSemantics());
            LlvmValue?[] operands = [lhs, rhs];
            uint[] usedBits = [width, width];
            LlvmKnownBits Known(int index)
            {
                if (known[index] is { } cached) return cached;
                var bits = ComputeKnownBits(operands[index]!);
                known[index] = bits;
                return bits;
            }
            bool IsValidPromotion(int index)
            {
                if (fromSigned != (operation.GetOperand(index) is LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP }) && !Known(index).IsNonNegative)
                    return false;
                if (precision < width)
                    usedBits[index] = fromSigned ? width - ComputeNumSignBits(operands[index]!) : width - Known(index).CountMinLeadingZeros();
                if (precision < usedBits[index]) return false;
                return !fromSigned || operation.Opcode != LlvmOpcode.FMul || (known[index] is { IsNonZero: true }) || IsKnownNonZero(operands[index]!);
            }
            if (fpConstant is not null)
            {
                if (fromSigned && operation.Opcode == LlvmOpcode.FMul && fpConstant is not LlvmConstantFP { Value.IsZero: false }) return null;
                LlvmOpcode toInt = fromSigned ? LlvmOpcode.FPToSI : LlvmOpcode.FPToUI;
                LlvmOpcode toFP = fromSigned ? LlvmOpcode.SIToFP : LlvmOpcode.UIToFP;
                var integer = LlvmConstantFolding.ConstantFoldCastOperand(toInt, fpConstant, intType, DL, Metrics);
                if (integer is null || !ReferenceEquals(LlvmConstantFolding.ConstantFoldCastOperand(toFP, integer, fpType, DL, Metrics), fpConstant)) return null;
                operands[1] = integer;
            }
            if (!ReferenceEquals(intType, operands[1]!.Type)) return null;
            if (fpConstant is null && !IsValidPromotion(1)) return null;
            if (!IsValidPromotion(0)) return null;
            LlvmOpcode opcode = operation.Opcode switch
            {
                LlvmOpcode.FAdd => LlvmOpcode.Add,
                LlvmOpcode.FSub => LlvmOpcode.Sub,
                LlvmOpcode.FMul => LlvmOpcode.Mul,
                _ => throw new InvalidOperationException("The source integer-cast fold expects fadd, fsub or fmul.")
            };
            uint overflowBits = (fromSigned ? 2U : 1U) + Math.Max(usedBits[0], usedBits[1]) * (opcode == LlvmOpcode.Mul ? 2U : 1U);
            bool outputSigned = fromSigned;
            bool needsOverflowCheck = true;
            if (overflowBits < width)
            {
                needsOverflowCheck = false;
                if (opcode == LlvmOpcode.Sub) outputSigned = true;
            }
            if (needsOverflowCheck && !WillNotOverflow(opcode, operands[0]!, operands[1]!, operation, outputSigned)) return null;
            var result = Builder.CreateBinary(opcode, operands[0]!, operands[1]!);
            if (result is LlvmBinaryOperator binary)
            {
                binary.HasNoSignedWrap = outputSigned;
                binary.HasNoUnsignedWrap = !outputSigned;
            }
            Count(outputSigned ? "instcombine.fp_integer_casts.signed_rewrites" : "instcombine.fp_integer_casts.unsigned_rewrites");
            return new LlvmCastInstruction(outputSigned ? LlvmOpcode.SIToFP : LlvmOpcode.UIToFP, result, fpType);
        }
    }
}
