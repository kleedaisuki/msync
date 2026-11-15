// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp foldICmpCommutative.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpCommutative(LlvmCmpPredicate predicate, LlvmValue left,
            LlvmValue right, LlvmICmpInstruction context)
        {
            if (LlvmGepOperator.TryGet(left, out var gep) && FoldGEPICmp(gep, right, predicate, context, context.SameSign) is { } pointer) return pointer;
            if (left is LlvmSelectInstruction select && FoldSelectICmp(predicate, select, right, context) is { } selected) return selected;
            if (left is LlvmCallInstruction minMax && minMax.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsMinMax(descriptor.Kind))
            {
                if (FoldICmpWithMinMax(context, minMax, right, predicate) is { } bounded) return bounded;
                if (FoldICmpWithClamp(context, right, minMax) is { } clamped) return clamped;
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } addition && addition.Left == right && ICmpAPInt(addition.Right) is { } add)
                return FoldICmpAddOpConst(right, add.Value, predicate);
            if (CallsIntrinsic(left, LlvmIntrinsicKind.Abs, out var absolute) && absolute.GetArgument(0) == right &&
                absolute.GetArgument(1) is LlvmConstantInt poison)
            {
                var zero = ICmpZero(right); var ones = ICmpOnes(right);
                var minimum = LlvmAPInt.GetSignedMinValue(right.Type.GetScalarSizeInBits());
                if (predicate is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge) return ReplaceInstUsesWith(context, LlvmConstant.GetAllOnesValue(context.Type));
                if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSlt) return ReplaceInstUsesWith(context, LlvmConstant.GetNullValue(context.Type));
                if (predicate is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpEq)
                    return ReplaceInstUsesWith(context, poison.IsMinusOne ? Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, right, ones)
                        : Builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, right, LlvmConstantInt.Get(right.Type, minimum + new LlvmAPInt(minimum.BitWidth, 1))));
                return ReplaceInstUsesWith(context, poison.IsMinusOne ? Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, right, zero)
                    : Builder.CreateICmp(LlvmCmpPredicate.ICmpUgt, right, LlvmConstantInt.Get(right.Type, minimum)));
            }
            if (CallsIntrinsic(left, LlvmIntrinsicKind.USubSat, out var saturation) && saturation.GetArgument(0) == right &&
                ICmpConstantMatches(saturation.GetArgument(1), static bits => !bits.IsZero) && (ICmpIsEquality(predicate) || predicate == LlvmCmpPredicate.ICmpUlt))
                return new LlvmICmpInstruction(ICmpIsEquality(predicate) ? predicate : LlvmCmpPredicate.ICmpNe, right, ICmpZero(right));
            if (FoldICmpWithLowBitMaskedVal(predicate, left, right, Query(context)) is { } low) return ReplaceInstUsesWith(context, low);
            if (left is LlvmBinaryOperator binary && binary.Left == right)
            {
                bool fold = binary.Opcode switch
                {
                    LlvmOpcode.UDiv => ICmpConstantMatches(binary.Right, static bits => bits.GetLimitedValue() > 1),
                    LlvmOpcode.SDiv => !ICmpIsUnsigned(predicate) && ICmpConstantMatches(binary.Right, static bits => bits.GetLimitedValue() > 1),
                    LlvmOpcode.LShr => ICmpConstantMatches(binary.Right, static bits => !bits.IsZero),
                    LlvmOpcode.AShr => predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge && ICmpConstantMatches(binary.Right, static bits => !bits.IsZero),
                    _ => false
                };
                if (fold) return new LlvmICmpInstruction(LlvmCmpInstruction.GetSwappedPredicate(predicate), right, ICmpZero(right));
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt shift } shl)
            {
                ulong amount = shift.Value.GetLimitedValue();
                if (amount >= shl.Left.Type.GetScalarSizeInBits()) return null;
                if (shl.HasNoUnsignedWrap && !ICmpIsSigned(context.Predicate) && CanEvaluateShifted(right, (uint)amount, false, ShiftSemantics.Unsigned, context))
                    return new LlvmICmpInstruction(predicate, shl.Left, GetShiftedValue(right, (uint)amount, false, ShiftSemantics.Unsigned)) { SameSign = context.SameSign };
                if (shl.HasNoSignedWrap && !ICmpIsUnsigned(context.Predicate) && CanEvaluateShifted(right, (uint)amount, false, ShiftSemantics.Signed, context))
                    return new LlvmICmpInstruction(predicate, shl.Left, GetShiftedValue(right, (uint)amount, false, ShiftSemantics.Signed)) { SameSign = context.SameSign };
            }
            return null;
        }
    }
}
