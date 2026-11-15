// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// integer min/max visitor (2241–2530).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldIntegerMinMax(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.minmax.visited");
            LlvmValue first = call.GetArgument(0), second = call.GetArgument(1);
            bool signed = kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.SMax;
            if (kind == LlvmIntrinsicKind.UMin)
            {
                if (CallsIsOne(second))
                    return new LlvmCastInstruction(LlvmOpcode.ZExt,
                        Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, first, LlvmConstant.GetNullValue(first.Type)), call.Type);
                if (FoldMinimumOverZeroCount(first, second, LlvmIntrinsicKind.CTTZ) is { } cttz) return ReplaceInstUsesWith(call, cttz);
                if (FoldMinimumOverZeroCount(first, second, LlvmIntrinsicKind.CTLZ) is { } ctlz) return ReplaceInstUsesWith(call, ctlz);
            }
            if (!signed)
            {
                if (CallsNarrowExtendedMinMax(call, kind, LlvmOpcode.ZExt) is { } narrow) return narrow;
                if (kind == LlvmIntrinsicKind.UMax)
                {
                    if (FoldMaxMulShift(call, first, second) is { } fold) return fold;
                    if (FoldMaxMulShift(call, second, first) is { } commuted) return commuted;
                }
            }
            if (CallsNarrowExtendedMinMax(call, kind, LlvmOpcode.SExt) is { } narrowSigned) return narrowSigned;
            LlvmIntrinsicKind inverse = LlvmIntrinsic.GetInverseMinMax(kind);
            if ((kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMax)
                && CallsIntrinsic(first, inverse, out var minimum) && first.HasOneUse
                && minimum.GetArgument(1) is LlvmConstantInt minimumConstant && second is LlvmConstantInt maximumConstant
                && (signed ? minimumConstant.Value.Sgt(maximumConstant.Value) : minimumConstant.Value.Ugt(maximumConstant.Value)))
            {
                LlvmValue max = Builder.CreateIntrinsic(kind, [minimum.GetArgument(0), second]);
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(inverse, [max, minimumConstant]));
            }
            if (call.Type.IsIntegerTy(1))
                return new LlvmBinaryOperator(kind == LlvmIntrinsicKind.UMin || kind == LlvmIntrinsicKind.SMax ? LlvmOpcode.And : LlvmOpcode.Or, first, second);
            if (kind == LlvmIntrinsicKind.SMin && CallsIsOne(second) && CallsIntrinsic(first, LlvmIntrinsicKind.SMax, out var maxOne)
                && first.HasOneUse && maxOne.GetArgument(1) is LlvmConstantInt { IsMinusOne: true })
                return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(LlvmIntrinsicKind.SCmp, [maxOne.GetArgument(0), CallsInt(first.Type, 0)]));
            if (signed && CallsNeg(first, out var x) && CallsNeg(second, out var y)
                && first is LlvmBinaryOperator { HasNoSignedWrap: true } && second is LlvmBinaryOperator { HasNoSignedWrap: true }
                && (first.HasOneUse || second.HasOneUse))
                return new LlvmBinaryOperator(LlvmOpcode.Sub, CallsInt(call.Type, 0), Builder.CreateIntrinsic(inverse, [x, y])) { HasNoSignedWrap = true };
            if (CallsXorOf(first, second, out var mask) || CallsXorOf(second, first, out mask))
            {
                if (CallsPowerOfTwo(mask, call, true))
                {
                    bool useOr = kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMax;
                    bool useAndNot = kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.UMin;
                    if (signed)
                    {
                        bool? sign = CallsKnownSign(mask, call);
                        if (sign is null) { useOr = false; useAndNot = false; }
                        else if (sign.Value)
                        {
                            useOr = !useOr; useAndNot = !useAndNot;
                            mask = CallsInt(call.Type, LlvmAPInt.GetSignedMinValue(call.Type.GetScalarSizeInBits()));
                        }
                    }
                    if (useOr) return new LlvmBinaryOperator(LlvmOpcode.Or, first, mask);
                    if (useAndNot) return new LlvmBinaryOperator(LlvmOpcode.And, first, Builder.CreateNot(mask));
                }
            }
            if (MoveNotAfterMinMax(call, kind, first, second) is { } inverted) return inverted;
            if (MoveNotAfterMinMax(call, kind, second, first) is { } invertedOther) return invertedOther;
            if (MoveAddAfterMinMax(call, kind) is { } added) return added;
            if (first is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Right: LlvmConstantInt negativePower } and0
                && (-negativePower.Value).IsPowerOf2 && second is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and1
                && and1.Right is LlvmConstantInt andConstant && negativePower.Value == andConstant.Value)
                return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateIntrinsic(kind, [and0.Left, and1.Left]), negativePower);
            if (LlvmSelectPattern.IsKnownNegation(first, second))
            {
                if (first.HasOneUse && !second.HasOneUse) (first, second) = (second, first);
                bool poison = LlvmSelectPattern.IsKnownNegation(first, second, needNSW: true);
                LlvmValue abs = Builder.CreateIntrinsic(LlvmIntrinsicKind.Abs, [first, LlvmConstantInt.GetBool(call.Context, poison)]);
                if (kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.UMax) abs = Builder.CreateNeg(abs, "nabs", nsw: poison);
                return ReplaceInstUsesWith(call, abs);
            }
            if (FoldClampRangeOfTwo(call, kind) is { } clamp) return clamp;
            if (MatchSAddSubSat(call, kind) is { } saturation) return saturation;
            if (ReassociateMinMaxWithConstants(call, kind) is { } associated) return ReplaceInstUsesWith(call, associated);
            if (ReassociateMinMaxWithConstantInOperand(call, kind) is { } associatedOperand) return associatedOperand;
            if (FactorizeMinMaxTree(call, kind) is { } factored) return factored;
            if (second is LlvmConstantInt rightConstant)
            {
                var query = Query(call);
                LlvmConstantRange range = LlvmConstantRangeAnalysis.ComputeIncludingKnownBits(first, signed, context: call,
                    tree: query.Tree, metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo,
                    assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC);
                LlvmCmpPredicate predicate = (LlvmCmpPredicate)((byte)LlvmIntrinsic.GetMinMaxPredicate(kind) + 1);
                if (!range.IsFullSet)
                {
                    if (range.ICmp(predicate, new(rightConstant.Value))) return ReplaceInstUsesWith(call, first);
                    if (range.ICmp(LlvmCmpInstruction.GetSwappedPredicate(predicate), new(rightConstant.Value))) return ReplaceInstUsesWith(call, rightConstant);
                }
            }
            if (FoldIntrinsicUsingDistributiveLaws(call, kind) is { } distributed) return ReplaceInstUsesWith(call, distributed);
            return null;
        }
        private static bool CallsXorOf(LlvmValue candidate, LlvmValue other, out LlvmValue mask)
        {
            if (candidate is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
            {
                if (ReferenceEquals(xor.Left, other)) { mask = xor.Right; return true; }
                if (ReferenceEquals(xor.Right, other)) { mask = xor.Left; return true; }
            }
            mask = candidate; return false;
        }
        private LlvmInstruction? CallsNarrowExtendedMinMax(LlvmCallInstruction call, LlvmIntrinsicKind kind, LlvmOpcode opcode)
        {
            if (call.GetArgument(0) is not LlvmCastInstruction left || left.Opcode != opcode) return null;
            if (call.GetArgument(1) is LlvmCastInstruction right && right.Opcode == opcode
                && (left.HasOneUse || right.HasOneUse) && ReferenceEquals(left.Operand.Type, right.Operand.Type))
                return new LlvmCastInstruction(opcode, Builder.CreateIntrinsic(kind, [left.Operand, right.Operand]), call.Type);
            if (left.HasOneUse && call.GetArgument(1) is LlvmConstantInt constant
                && CallsLosslessTrunc(constant, left.Operand.Type, opcode == LlvmOpcode.SExt) is { } narrow)
                return new LlvmCastInstruction(opcode, Builder.CreateIntrinsic(kind, [left.Operand, narrow]), call.Type);
            return null;
        }
        private static LlvmConstantInt? CallsLosslessTrunc(LlvmConstantInt constant, LlvmType type, bool signed)
        {
            LlvmAPInt truncated = constant.Value.Trunc(type.GetScalarSizeInBits());
            return (signed ? truncated.SExt(constant.BitWidth) : truncated.ZExt(constant.BitWidth)) == constant.Value ? CallsInt(type, truncated) : null;
        }
        private LlvmInstruction? FoldMaxMulShift(LlvmCallInstruction call, LlvmValue first, LlvmValue second)
        {
            if (first is not LlvmBinaryOperator { HasNoUnsignedWrap: true, Right: LlvmConstantInt constant } operation
                || operation.Opcode != LlvmOpcode.Shl && (operation.Opcode != LlvmOpcode.Mul || constant.IsOne) || constant.IsZero) return null;
            if (second is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt { IsOne: true } } increment
                || !ReferenceEquals(increment.Left, operation.Left)) return null;
            return ReplaceInstUsesWith(call, Builder.CreateSelect(
                Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, operation.Left, CallsInt(call.Type, 0)), CallsInt(call.Type, 1), first));
        }
        private bool? CallsKnownSign(LlvmValue value, LlvmInstruction context, bool orZero = false)
        {
            LlvmKnownBits known = CallsKnownBits(value, context);
            if (known.IsNonNegative) return false;
            if (known.IsNegative) return true;
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } sub) return null;
            bool? sign = LlvmInstructionSimplify.IsImpliedByDomCondition(LlvmCmpPredicate.ICmpSlt, sub.Left, sub.Right, Query(context));
            return sign ?? (orZero ? LlvmInstructionSimplify.IsImpliedByDomCondition(LlvmCmpPredicate.ICmpSle, sub.Left, sub.Right, Query(context)) : null);
        }
    }
}
