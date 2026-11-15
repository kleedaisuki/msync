// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp visitTrunc.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitTrunc(LlvmCastInstruction truncation)
        {
            Count("instcombine.casts.integer.trunc.visits");
            if (CommonCastTransforms(truncation) is { } common) return common;
            LlvmValue source = truncation.Operand;
            LlvmType destination = truncation.Type, sourceType = source.Type;
            uint width = destination.GetScalarSizeInBits(), wide = sourceType.GetScalarSizeInBits();
            if (ShouldChangeType(sourceType, destination) && TypeEvaluationHelper.CanEvaluateTruncated(source, destination, this, truncation))
                return CastIntegerReplace(truncation, EvaluateInDifferentType(source, destination, false), "trunc_evaluate_expression");
            if (width * 2 < wide)
            {
                LlvmType intermediate = LlvmIntegerType.Get(truncation.Context, width * 2);
                if (ShouldChangeType(sourceType, intermediate) && TypeEvaluationHelper.CanEvaluateTruncated(source, intermediate, this, truncation))
                    return CastIntegerResult(new LlvmCastInstruction(LlvmOpcode.Trunc, EvaluateInDifferentType(source, intermediate, false), destination), "trunc_evaluate_half_width");
            }
            if (width == 1 && (truncation.HasNoUnsignedWrap || truncation.HasNoSignedWrap) && source is LlvmBinaryOperator { IsExact: true } exact && IsRightShiftOp(exact.Opcode))
                return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, exact.Left, LogicZero(sourceType)), "trunc_exact_to_bool");
            if (SimplifyDemandedInstructionBits(truncation)) return truncation;
            if (width == 1)
            {
                if (source is LlvmBinaryOperator { HasOneUse: true, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt power } left, Right: LlvmConstant amount } shr &&
                    IsRightShiftOp(shr.Opcode) && power.Value.IsPowerOf2 && amount is LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue)
                {
                    LlvmConstant compareConstant = LlvmConstantExpr.GetSub(amount, LogicC(sourceType, power.Value.CountTrailingZeros()));
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, left.Right, compareConstant!), "trunc_shifted_power_bool");
                }
                if (source is LlvmBinaryOperator right && IsRightShiftOp(right.Opcode) && right.Right is LlvmConstantInt sign && sign.Value.GetLimitedValue(wide) == wide - 1)
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, right.Left, LogicZero(sourceType)), "trunc_signbit_bool");
                if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue } logical)
                {
                    LlvmValue mask = Builder.CreateBinOp(LlvmOpcode.Shl, LogicC(sourceType, 1), logical.Right);
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, Builder.CreateAnd(logical.Left, mask), LogicZero(sourceType)), "trunc_lshr_to_masked_bool");
                }
                if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } or)
                {
                    LlvmValue shifted = or.Left, x = or.Right;
                    bool Match(LlvmValue value, LlvmValue original) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt or LlvmUndefValue or LlvmPoisonValue } candidate && ReferenceEquals(candidate.Left, original);
                    if (!Match(shifted, x)) (shifted, x) = (x, shifted);
                    if (Match(shifted, x))
                    {
                        LlvmValue mask = Builder.CreateBinOp(LlvmOpcode.Shl, LogicC(sourceType, 1), ((LlvmBinaryOperator)shifted).Right);
                        LlvmValue tested = Builder.CreateAnd(x, Builder.CreateOr(mask, LogicC(sourceType, 1)));
                        return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, tested, LogicZero(sourceType)), "trunc_or_lshr_bool");
                    }
                }
                if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt odd } shl && !((odd.Value & new LlvmAPInt(wide, 1)).IsZero))
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, shl.Right, LogicZero(sourceType)), "trunc_odd_shl_bool");
                if ((truncation.HasNoUnsignedWrap || truncation.HasNoSignedWrap) && source is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, xor.Left, xor.Right), "trunc_nowrap_xor_bool");
                if (source.HasOneUse && ShiftIntrinsic(source, LlvmIntrinsicKind.USubSat, out var saturated) && saturated.GetArgument(0) is LlvmConstantInt { IsOne: true })
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, saturated.GetArgument(1), LogicZero(sourceType)), "trunc_usub_sat_bool");
            }
            if (source is LlvmCallBase minimum && minimum.HasOneUse && minimum.CalledFunction?.IntrinsicDescriptor is { } minDescriptor &&
                (minDescriptor.Kind == LlvmIntrinsicKind.UMin || minDescriptor.Kind == LlvmIntrinsicKind.SMin) &&
                minimum.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext0, Right: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext1 } &&
                ext0.Operand.Type == destination && ext1.Operand.Type == destination && minimum.GetArgument(1) is LlvmConstantInt satMaximum && satMaximum.Value == LlvmAPInt.GetLowBitsSet(wide, width))
                return CastIntegerReplace(truncation, Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.UAddSat, ext0.Operand, ext1.Operand), "trunc_unsigned_add_sat");
            if (source.HasOneUse && ShiftIntrinsic(source, LlvmIntrinsicKind.SMax, out var maximumCall) && maximumCall.GetArgument(1) is LlvmConstantInt { IsZero: true } &&
                maximumCall.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true, Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } sub0, Right: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } sub1 } && sub0.Operand.Type == destination && sub1.Operand.Type == destination)
                return CastIntegerReplace(truncation, Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.USubSat, sub0.Operand, sub1.Operand), "trunc_unsigned_sub_sat");
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmCastInstruction { Opcode: LlvmOpcode.SExt } signed, Right: LlvmConstantInt amountConstant } oldShift)
            {
                LlvmValue value = signed.Operand;
                uint originalWidth = value.Type.GetScalarSizeInBits();
                uint maximumShift = wide - Math.Max(width, originalWidth);
                if (amountConstant.Value.GetLimitedValue(maximumShift + 1UL) <= maximumShift)
                {
                    uint bits = (uint)Math.Min(amountConstant.Value.GetZExtValue(), originalWidth - 1UL);
                    var amount = LogicC(value.Type, bits);
                    if (value.Type == destination)
                        return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.AShr, value, amount) { IsExact = oldShift.IsExact }, "trunc_logical_sext_to_signed");
                    if (source.HasOneUse)
                        return CastIntegerResult(IntegerCastInstruction(Builder.CreateBinary(LlvmOpcode.AShr, value, amount, exact: oldShift.IsExact), destination, true), "trunc_logical_sext_to_signed_cast");
                }
            }
            if (NarrowBinOp(truncation) is { } narrowed) return narrowed;
            if (source.HasOneUse && ShouldChangeType(sourceType, destination) && source is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt shiftAmount } wideShift &&
                !(wideShift.Left is LlvmBinaryOperator opposite && IsRightShiftOp(opposite.Opcode) && opposite.Right is LlvmConstant) && shiftAmount.Value.GetLimitedValue(width) < width)
            {
                bool nuw = truncation.HasNoUnsignedWrap && wideShift.HasNoUnsignedWrap, nsw = truncation.HasNoSignedWrap && wideShift.HasNoSignedWrap;
                var truncated = Builder.Insert(new LlvmCastInstruction(LlvmOpcode.Trunc, wideShift.Left, destination) { HasNoUnsignedWrap = nuw, HasNoSignedWrap = nsw }, wideShift.Left.Name + ".tr");
                return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.Shl, truncated, LogicC(destination, shiftAmount.Value.Trunc(width))) { HasNoUnsignedWrap = nuw, HasNoSignedWrap = nsw }, "trunc_left_shift");
            }
            if ((wide & (wide - 1)) == 0 && (width & (width - 1)) == 0 && source is LlvmSelectInstruction { HasOneUse: true, Condition: LlvmICmpInstruction { HasOneUse: true, Right: LlvmConstantInt boundary } compare } select)
            {
                LlvmAPInt maximum = LlvmAPInt.GetLowBitsSet(wide, width);
                LlvmValue? extended = compare.Predicate == LlvmCmpPredicate.ICmpUlt && ReferenceEquals(select.TrueValue, compare.Left) && boundary.Value == maximum + new LlvmAPInt(wide, 1) ? select.FalseValue :
                    compare.Predicate == LlvmCmpPredicate.ICmpUgt && ReferenceEquals(select.FalseValue, compare.Left) && boundary.Value == maximum ? select.TrueValue : null;
                if (extended is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true, Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpSgt, HasOneUse: true, Right: LlvmConstantInt { IsZero: true } } positive } && ReferenceEquals(positive.Left, compare.Left))
                {
                    LlvmValue clamped = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMax, LogicZero(sourceType), compare.Left);
                    LlvmValue high = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMin, clamped, LogicC(sourceType, maximum));
                    return CastIntegerResult(new LlvmCastInstruction(LlvmOpcode.Trunc, high, destination), "trunc_unsigned_clamp");
                }
            }
            if (source.HasOneUse && ShiftIntrinsic(source, LlvmIntrinsicKind.CTLZ, out var count) && count.GetArgument(0) is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } input)
            {
                uint originalWidth = input.Operand.Type.GetScalarSizeInBits();
                if (originalWidth == width && originalWidth > BitOperations.Log2(wide))
                {
                    LlvmValue narrowedCount = Builder.CreateIntrinsic(LlvmIntrinsicKind.CTLZ, [input.Operand, count.GetArgument(1)]);
                    return CastIntegerResult(new LlvmBinaryOperator(LlvmOpcode.Add, narrowedCount, LogicC(input.Operand.Type, wide - originalWidth)), "trunc_narrow_ctlz");
                }
            }
            if (ShiftIntrinsic(source, LlvmIntrinsicKind.VScale, out _) && F.VScaleRange is { Maximum: > 0 } range && BitOperations.Log2(range.Maximum) < width)
                return CastIntegerReplace(truncation, Builder.CreateIntrinsic(LlvmIntrinsicKind.VScale, [], (LlvmIntegerType)destination), "trunc_vscale_range");
            if (width >= 2 && source is LlvmCallBase intrinsic && intrinsic.HasOneUse && intrinsic.CalledFunction?.IntrinsicDescriptor is { HasComparisonRange: true } descriptor)
                return CastIntegerReplace(truncation, Builder.CreateIntrinsic(descriptor.Kind, [intrinsic.GetArgument(0), intrinsic.GetArgument(1)], (LlvmIntegerType)destination), "trunc_compare_intrinsic");
            if (width == 1 && (truncation.HasNoUnsignedWrap || truncation.HasNoSignedWrap) && IsKnownNonZero(source, truncation))
                return CastIntegerReplace(truncation, LogicC(destination, 1), "trunc_nowrap_nonzero_bool");
            bool changed = false;
            if (!truncation.HasNoSignedWrap && ComputeMaxSignificantBits(source, truncation) <= width)
            { truncation.HasNoSignedWrap = true; changed = true; Count("instcombine.casts.integer.trunc.flags.nsw"); }
            if (!truncation.HasNoUnsignedWrap && MaskedValueIsZero(source, LlvmAPInt.GetBitsSetFrom(wide, width), truncation))
            { truncation.HasNoUnsignedWrap = true; changed = true; Count("instcombine.casts.integer.trunc.flags.nuw"); }
            if (width == 1 && source is LlvmBinaryOperator rightShift && IsRightShiftOp(rightShift.Opcode) && rightShift.Left is LlvmConstantInt valueConstant)
            {
                LlvmAPInt value = valueConstant.Value;
                if (value.IsPowerOf2)
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, rightShift.Right, LogicC(rightShift.Right.Type, value.CountTrailingZeros())), "trunc_shifted_power_test");
                if (LogicMask(value))
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUlt, rightShift.Right, LogicC(rightShift.Right.Type, value.CountTrailingOnes())), "trunc_shifted_low_mask_test");
                if ((-value).IsPowerOf2)
                    return CastIntegerResult(new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUge, rightShift.Right, LogicC(rightShift.Right.Type, value.CountTrailingZeros())), "trunc_shifted_negative_power_test");
            }
            if (width == 1 && source is LlvmLoadInstruction load && load.PointerOperand is LlvmGetElementPtrInstruction gep &&
                FoldCmpLoadFromIndexedGlobal(load, gep, LlvmCmpPredicate.ICmpNe, LogicZero(sourceType), truncation, LogicC(sourceType, 1)) is { } loadTest) return loadTest;
            return changed ? truncation : null;
        }
    }
}
