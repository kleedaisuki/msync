// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp visitZExt/visitSExt.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitZExt(LlvmCastInstruction extension)
        {
            Count("instcombine.casts.integer.zext.visits");
            if (extension.HasOneUse && extension.Users.Last() is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } && extension.Operand is not LlvmConstant) return null;
            if (CommonCastTransforms(extension) is { } common) return common;
            LlvmValue source = extension.Operand;
            LlvmType sourceType = source.Type, destination = extension.Type;
            uint sourceWidth = sourceType.GetScalarSizeInBits(), width = destination.GetScalarSizeInBits();
            if (sourceWidth == 1 && extension.IsNonNegative) return CastIntegerReplace(extension, LlvmConstant.GetNullValue(destination), "zext_nneg_bool");
            bool signed = extension.IsNonNegative && TypeEvaluationHelper.CanEvaluateSExtd(source, destination, CLOpts.MaxTypeEvalDepth, Metrics);
            uint clear = 0;
            if (ShouldChangeType(sourceType, destination) && (signed || TypeEvaluationHelper.CanEvaluateZExtd(source, destination, out clear, this, extension)))
            {
                LlvmValue evaluated = EvaluateInDifferentType(source, destination, signed);
                uint kept = sourceWidth - clear;
                bool highKnown = signed ? ComputeNumSignBits(evaluated, extension) > width - kept : MaskedValueIsZero(evaluated, LlvmAPInt.GetHighBitsSet(width, width - kept), extension);
                if (highKnown) return CastIntegerReplace(extension, evaluated, "zext_evaluate_no_mask");
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.And, evaluated, LogicC(destination, LlvmAPInt.GetLowBitsSet(width, kept))), "zext_evaluate_with_mask");
            }
            if (source is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncation)
            {
                LlvmValue value = truncation.Operand;
                uint originalWidth = value.Type.GetScalarSizeInBits();
                if (originalWidth < width)
                    return CastIntegerResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateAnd(value, LogicC(value.Type, LlvmAPInt.GetLowBitsSet(originalWidth, sourceWidth)), truncation.Name + ".mask"), destination), "zext_trunc_widen_mask");
                LlvmValue narrowed = originalWidth == width ? value : Builder.CreateTrunc(value, destination);
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.And, narrowed, LogicC(narrowed.Type, LlvmAPInt.GetLowBitsSet(narrowed.Type.GetScalarSizeInBits(), sourceWidth))), "zext_trunc_mask");
            }
            if (source is LlvmICmpInstruction compare) return TransformZExtICmp(compare, extension);
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } maskedTrunc } and, Right: LlvmConstant constant } &&
                ReferenceEquals(and.Right, constant) && maskedTrunc.Operand.Type == destination)
            {
                LlvmValue widened = Builder.CreateZExt(constant, destination);
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateAnd(maskedTrunc.Operand, widened), widened), "zext_masked_trunc_xor");
            }
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true }, Right: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } negated } && negated.Operand.Type == destination)
            {
                LlvmValue neg = Builder.CreateSub(LogicZero(destination), negated.Operand);
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.And, neg, LogicC(destination, LlvmAPInt.GetLowBitsSet(width, sourceWidth))), "zext_negated_trunc");
            }
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } narrow, Right: LlvmConstant mask } && narrow.Operand.Type == destination)
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.And, narrow.Operand, Builder.CreateZExt(mask, destination)), "zext_masked_trunc");
            if (source is LlvmBinaryOperator logic && logic.HasOneUse && logic.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)
            {
                LlvmValue first = logic.Left, second = logic.Right;
                bool Matches(LlvmValue value) => value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true } cast && cast.Operand.Type == destination;
                if (!Matches(first)) (first, second) = (second, first);
                if (Matches(first)) return CastIntegerResult(new LlvmBinaryOperator(logic.Opcode, ((LlvmCastInstruction)first).Operand, Builder.CreateZExt(second, destination)), "zext_bitwise_nuw_trunc");
            }
            if (ShiftIntrinsic(source, LlvmIntrinsicKind.VScale, out _) && F.VScaleRange is { Maximum: > 0 } range && BitOperations.Log2(range.Maximum) < sourceWidth)
                return CastIntegerReplace(extension, Builder.CreateIntrinsic(LlvmIntrinsicKind.VScale, [], (LlvmIntegerType)destination), "zext_vscale_range");
            if (!extension.IsNonNegative)
            {
                if (extension.HasOneUse && sourceWidth > BitOperations.Log2(width - 1) + 1 &&
                    extension.Users.Last() is LlvmBinaryOperator user && IsShiftOp(user.Opcode) && ReferenceEquals(user.Right, extension))
                { extension.IsNonNegative = true; return CastIntegerResult(extension, "zext_nneg_shift_amount"); }
                if (ComputeKnownBits(source, extension).IsNonNegative)
                { extension.IsNonNegative = true; return CastIntegerResult(extension, "zext_nneg_knownbits"); }
            }
            return null;
        }

        private LlvmInstruction? VisitSExt(LlvmCastInstruction extension)
        {
            Count("instcombine.casts.integer.sext.visits");
            if (extension.HasOneUse && extension.Users.Last() is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc }) return null;
            if (CommonCastTransforms(extension) is { } common) return common;
            LlvmValue source = extension.Operand;
            LlvmType sourceType = source.Type, destination = extension.Type;
            uint sourceWidth = sourceType.GetScalarSizeInBits(), width = destination.GetScalarSizeInBits();
            if (ComputeKnownBits(source, extension).IsNonNegative)
                return CastIntegerResult(new LlvmCastInstruction(LlvmOpcode.ZExt, source, destination) { IsNonNegative = true }, "sext_to_nneg_zext");
            if (source is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncation)
            {
                LlvmValue value = truncation.Operand;
                uint removed = value.Type.GetScalarSizeInBits() - sourceWidth;
                if (truncation.HasNoSignedWrap || ComputeNumSignBits(value, extension) > removed)
                {
                    var result = IntegerCastInstruction(value, destination, true);
                    if (result.Opcode == LlvmOpcode.Trunc && truncation.HasNoSignedWrap) result.HasNoSignedWrap = true;
                    return CastIntegerResult(result, "sext_trunc_signbits");
                }
                if (truncation.HasOneUse && value is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt constant } shift && constant.Value.GetLimitedValue(removed + 1UL) <= removed &&
                    (constant.Value.GetZExtValue() == removed || ComputeNumSignBits(shift.Left, extension) + constant.Value.GetZExtValue() > removed))
                    return CastIntegerResult(IntegerCastInstruction(Builder.CreateBinOp(LlvmOpcode.AShr, shift.Left, constant), destination, true), "sext_trunc_logical_to_signed");
                if (truncation.HasOneUse && value.Type == destination)
                {
                    LlvmConstant amount = LogicC(destination, width - sourceWidth);
                    return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.AShr, Builder.CreateBinOp(LlvmOpcode.Shl, value, amount), amount), "sext_trunc_register");
                }
            }
            bool extendExpression = source is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } original || original.Operand.Type.GetScalarSizeInBits() <= width;
            if (extendExpression && ShouldChangeType(sourceType, destination) && TypeEvaluationHelper.CanEvaluateSExtd(source, destination, CLOpts.MaxTypeEvalDepth, Metrics))
            {
                LlvmValue evaluated = EvaluateInDifferentType(source, destination, true);
                if (ComputeNumSignBits(evaluated, extension) > width - sourceWidth) return CastIntegerReplace(extension, evaluated, "sext_evaluate_no_shift");
                LlvmConstant amount = LogicC(destination, width - sourceWidth);
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.AShr, Builder.CreateBinOp(LlvmOpcode.Shl, evaluated, amount, "sext"), amount), "sext_evaluate_register");
            }
            if (source is LlvmICmpInstruction compare) return TransformSExtICmp(compare, extension);
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } narrow, Right: LlvmConstant before }, Right: LlvmConstant after } &&
                narrow.Operand.Type == destination && after is LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue && ReferenceEquals(before, after))
            {
                LlvmConstant? widened = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.SExt, after, destination, DL, Metrics);
                LlvmConstant kept = LlvmConstantExpr.GetSub(LogicC(destination, sourceWidth), widened!);
                LlvmConstant amount = LlvmConstantExpr.GetSub(LogicC(destination, width), kept);
                if (before is LlvmUndefValue || after is LlvmUndefValue) amount = LlvmUndefValue.Get(destination);
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.AShr, Builder.CreateBinOp(LlvmOpcode.Shl, narrow.Operand, amount!, extension.Name), amount!), "sext_truncated_register");
            }
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, HasOneUse: true, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } low, Right: LlvmConstantInt bit } && bit.Value.GetLimitedValue(sourceWidth) == sourceWidth - 1)
            {
                LlvmValue value = low.Operand;
                uint originalWidth = value.Type.GetScalarSizeInBits();
                if (value.Type == destination || low.HasOneUse)
                {
                    LlvmValue shifted = Builder.CreateBinOp(LlvmOpcode.Shl, value, LogicC(value.Type, originalWidth - sourceWidth));
                    LlvmConstant amount = LogicC(value.Type, originalWidth - 1);
                    if (value.Type == destination) return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.AShr, shifted, amount), "sext_trunc_sign_splat");
                    return CastIntegerResult(IntegerCastInstruction(Builder.CreateBinOp(LlvmOpcode.AShr, shifted, amount), destination, true), "sext_trunc_sign_splat_cast");
                }
            }
            if (ShiftIntrinsic(source, LlvmIntrinsicKind.VScale, out _) && F.VScaleRange is { Maximum: > 0 } range && BitOperations.Log2(range.Maximum) < sourceWidth - 1)
                return CastIntegerReplace(extension, Builder.CreateIntrinsic(LlvmIntrinsicKind.VScale, [], (LlvmIntegerType)destination), "sext_vscale_range");
            if (source is LlvmCallBase intrinsic && intrinsic.HasOneUse && intrinsic.CalledFunction?.IntrinsicDescriptor is { HasComparisonRange: true } descriptor)
                return CastIntegerReplace(extension, Builder.CreateIntrinsic(descriptor.Kind, [intrinsic.GetArgument(0), intrinsic.GetArgument(1)], (LlvmIntegerType)destination), "sext_compare_intrinsic");
            if (source is LlvmBinaryOperator logic && logic.HasOneUse && logic.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)
            {
                LlvmValue first = logic.Left, second = logic.Right;
                bool Matches(LlvmValue value) => value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoSignedWrap: true } cast && cast.Operand.Type == destination;
                if (!Matches(first)) (first, second) = (second, first);
                if (Matches(first)) return CastIntegerResult(new LlvmBinaryOperator(logic.Opcode, ((LlvmCastInstruction)first).Operand, Builder.CreateSExt(second, destination)), "sext_bitwise_nsw_trunc");
            }
            return null;
        }
    }
}
