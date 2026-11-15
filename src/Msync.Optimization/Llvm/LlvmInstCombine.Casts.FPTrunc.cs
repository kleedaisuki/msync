// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 2305–2510 (scalar paths).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitFPTrunc(LlvmCastInstruction instruction)
        {
            Count("casts.fptrunc.queries");
            if (CommonCastTransforms(instruction) is { } common) return common;
            if (instruction.Operand is LlvmBinaryOperator { HasOneUse: true } binary && NarrowFPTruncBinOp(instruction, binary) is { } narrowed) return narrowed;
            if (instruction.Operand is LlvmInstruction { HasOneUse: true } operand)
            {
                var flags = instruction.FastMathFlags;
                if (operand is ILlvmFastMathInstruction math) flags &= math.FastMathFlags;
                if (FCmpNegatedOperand(operand) is { } negated)
                {
                    var narrow = CreateFPCastWithFlags(negated, instruction.Type, flags);
                    Count("casts.fptrunc.negate");
                    return ReplaceInstUsesWith(instruction, Builder.CreateFNeg(narrow, fastMathFlags: flags));
                }
                if (operand is LlvmSelectInstruction select)
                {
                    if (select.TrueValue is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } first && first.Operand.Type == instruction.Type)
                    {
                        var narrow = CreateFPCastWithFlags(select.FalseValue, instruction.Type, flags);
                        Count("casts.fptrunc.select");
                        return ReplaceInstUsesWith(instruction, Builder.CreateSelect(select.Condition, first.Operand, narrow, "narrow.sel", flags));
                    }
                    if (select.FalseValue is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } second && second.Operand.Type == instruction.Type)
                    {
                        var narrow = CreateFPCastWithFlags(select.TrueValue, instruction.Type, flags);
                        Count("casts.fptrunc.select");
                        return ReplaceInstUsesWith(instruction, Builder.CreateSelect(select.Condition, narrow, second.Operand, "narrow.sel", flags));
                    }
                }
            }
            if (instruction.Operand is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor && IsShrinkableUnaryFP(descriptor.Kind))
            {
                var source = call.GetArgument(0);
                if (source.HasOneUse && (descriptor.Kind == LlvmIntrinsicKind.FAbs ||
                    source is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt } extension && extension.Operand.Type == instruction.Type))
                {
                    var narrow = CreateFPCastWithFlags(source, instruction.Type);
                    var declaration = LlvmIntrinsic.GetDeclaration(instruction.Parent!.Module!, descriptor.Kind, instruction.Type, Metrics);
                    Count("casts.fptrunc.unary_intrinsic");
                    var replacement = new LlvmCallInstruction(declaration.FunctionType, declaration, [narrow], call.GetOperandBundlesAsDefs())
                    { FastMathFlags = instruction.FastMathFlags };
                    replacement.SetName(call.Name);
                    return replacement;
                }
            }
            if (instruction.Operand is LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP or LlvmOpcode.UIToFP } cast && IsKnownExactCastIntToFP(cast))
            { Count("casts.fptrunc.integer_direct"); return new LlvmCastInstruction(cast.Opcode, cast.Operand, instruction.Type); }
            return null;
        }
        private static bool IsShrinkableUnaryFP(LlvmIntrinsicKind kind) =>
            kind == LlvmIntrinsicKind.Ceil || kind == LlvmIntrinsicKind.FAbs || kind == LlvmIntrinsicKind.Floor ||
            kind == LlvmIntrinsicKind.NearbyInt || kind == LlvmIntrinsicKind.Rint || kind == LlvmIntrinsicKind.Round ||
            kind == LlvmIntrinsicKind.RoundEven || kind == LlvmIntrinsicKind.Trunc;
        private LlvmInstruction? NarrowFPTruncBinOp(LlvmCastInstruction instruction, LlvmBinaryOperator binary)
        {
            var type = instruction.Type;
            var leftType = GetMinimumFPType(binary.Left, type);
            var rightType = GetMinimumFPType(binary.Right, type);
            int operationWidth = binary.Type.GetFPMantissaWidth(), leftWidth = leftType.GetFPMantissaWidth(),
                rightWidth = rightType.GetFPMantissaWidth(), sourceWidth = Math.Max(leftWidth, rightWidth), destinationWidth = type.GetFPMantissaWidth();
            bool fit = LlvmAPFloat.IsLosslesslyConvertibleTo(leftType.GetScalarType().GetFltSemantics(), type.GetScalarType().GetFltSemantics(), true) &&
                LlvmAPFloat.IsLosslesslyConvertibleTo(rightType.GetScalarType().GetFltSemantics(), type.GetScalarType().GetFltSemantics(), true);
            var flags = binary.FastMathFlags;
            if ((instruction.FastMathFlags & LlvmFastMathFlags.NoInfs) == 0) flags &= ~LlvmFastMathFlags.NoInfs;
            bool narrow = binary.Opcode switch
            {
                LlvmOpcode.FAdd or LlvmOpcode.FSub => operationWidth >= 2 * destinationWidth + 1 && fit,
                LlvmOpcode.FMul => operationWidth >= leftWidth + rightWidth && fit,
                LlvmOpcode.FDiv => operationWidth >= 2 * destinationWidth && fit,
                _ => false
            };
            if (narrow)
            {
                Count("casts.fptrunc.binary_narrow");
                return new LlvmBinaryOperator(binary.Opcode, CreateFPCastWithFlags(binary.Left, type), CreateFPCastWithFlags(binary.Right, type))
                { FastMathFlags = flags };
            }
            if (binary.Opcode != LlvmOpcode.FRem || sourceWidth == operationWidth) return null;
            var minimum = leftWidth == sourceWidth ? leftType : rightType;
            var left = CreateFPCastWithFlags(binary.Left, minimum);
            var right = CreateFPCastWithFlags(binary.Right, minimum);
            var exact = Builder.CreateBinary(LlvmOpcode.FRem, left, right, fastMathFlags: binary.FastMathFlags);
            Count("casts.fptrunc.remainder_narrow");
            var opcode = exact.Type.GetScalarSizeInBits() == type.GetScalarSizeInBits() ? LlvmOpcode.BitCast :
                exact.Type.GetScalarSizeInBits() > type.GetScalarSizeInBits() ? LlvmOpcode.FPTrunc : LlvmOpcode.FPExt;
            return new LlvmCastInstruction(opcode, exact, type);
        }
    }
}
