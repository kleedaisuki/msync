// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 1745-2088.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpAndShift(LlvmICmpInstruction compare, LlvmBinaryOperator and, LlvmAPInt expected, LlvmAPInt mask)
        {
            Count("icmp.constant.and-shift.queries");
            if (and.Left is not LlvmBinaryOperator shift || !IccIsShift(shift.Opcode)) return null;
            bool leftShift = shift.Opcode == LlvmOpcode.Shl;
            if (shift.Right is LlvmConstantInt amount)
            {
                LlvmAPInt rhs, newMask;
                bool shiftedOut;
                if (leftShift)
                {
                    if (ICmpIsSigned(compare.Predicate) && (mask.IsNegative || expected.IsNegative)) return null;
                    rhs = expected.LShr(amount.Value); newMask = mask.LShr(amount.Value);
                    shiftedOut = rhs.Shl(amount.Value) != expected;
                }
                else
                {
                    rhs = expected.Shl(amount.Value); newMask = mask.Shl(amount.Value);
                    if (shift.Opcode == LlvmOpcode.LShr)
                    {
                        shiftedOut = rhs.LShr(amount.Value) != expected;
                        if (ICmpIsSigned(compare.Predicate) && (newMask.IsNegative || rhs.IsNegative)) return null;
                    }
                    else
                    {
                        shiftedOut = rhs.AShr(amount.Value) != expected;
                        if (newMask.AShr(amount.Value) != mask) return null;
                    }
                }
                if (!shiftedOut)
                {
                    Count("icmp.constant.and-shift.constant-mask");
                    return IccCompare(compare.Predicate, Builder.CreateAnd(shift.Left, IccConstant(and, newMask)), IccConstant(and, rhs));
                }
                if (ICmpIsEquality(compare.Predicate))
                {
                    Count("icmp.constant.and-shift.impossible-equality");
                    return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Context, compare.Predicate == LlvmCmpPredicate.ICmpNe));
                }
            }
            if (shift.HasOneUse && expected.IsZero && ICmpIsEquality(compare.Predicate) && shift.Opcode != LlvmOpcode.AShr &&
                (!leftShift && mask.IsOne || shift.Left is not LlvmConstant))
            {
                Count("icmp.constant.and-shift.variable-mask");
                LlvmValue newShift = Builder.CreateBinary(leftShift ? LlvmOpcode.LShr : LlvmOpcode.Shl, and.Right, shift.Right);
                return IccCompare(compare.Predicate, Builder.CreateAnd(shift.Left, newShift), compare.Right);
            }
            return null;
        }

        private LlvmInstruction? FoldICmpAndConstConst(LlvmICmpInstruction compare, LlvmBinaryOperator and, LlvmAPInt expected)
        {
            Count("icmp.constant.and-mask.queries");
            bool ne = compare.Predicate == LlvmCmpPredicate.ICmpNe;
            if (ne && expected.IsZero && and.Right is LlvmConstantInt { Value.IsOne: true })
                return new LlvmCastInstruction(LlvmOpcode.Trunc, and.Left, compare.Type);
            if (and.Right is not LlvmConstantInt c) return null;
            LlvmAPInt mask = c.Value, one = IccInt(mask, 1);
            LlvmValue input = and.Left;
            if (compare.Predicate == LlvmCmpPredicate.ICmpSgt && expected.Ule(~mask) && (-mask).IsPowerOf2)
                return New(LlvmCmpPredicate.ICmpSgt, input, ~mask, "signed-high-mask-gt");
            if (compare.Predicate == LlvmCmpPredicate.ICmpSlt && !expected.IsMinSignedValue && (expected - one).Ule(~mask) && (-mask).IsPowerOf2 && !mask.IsMinSignedValue)
                return New(LlvmCmpPredicate.ICmpSlt, input, -mask, "signed-high-mask-lt");
            if (!and.HasOneUse) return null;
            if (ICmpIsEquality(compare.Predicate) && expected.IsZero)
            {
                if (mask.IsMinSignedValue) return New(ne ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge, input, LlvmAPInt.GetZero(mask.BitWidth), "sign-mask");
                var known = ComputeKnownBits(input, and);
                LlvmAPInt filled = mask | LlvmAPInt.GetHighBitsSet(mask.BitWidth, known.Zero.CountLeadingOnes());
                if ((-filled).IsPowerOf2) return New(ne ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpUlt, input, -filled, "zero-high-mask");
            }
            if (input is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true } trunc &&
                (ICmpIsEquality(compare.Predicate) || !expected.IsNegative && !mask.IsNegative))
            {
                uint width = trunc.Operand.Type.GetIntegerBitWidth();
                LlvmValue newAnd = Builder.CreateAnd(trunc.Operand, IccConstant(trunc.Operand, mask.ZExt(width)), and.Name);
                return New(compare.Predicate, newAnd, expected.ZExt(width), "widen-trunc");
            }
            if (FoldICmpAndShift(compare, and, expected, mask) is { } shifted) return shifted;
            if (!ICmpIsSigned(compare.Predicate) && expected.IsZero && input.HasOneUse && mask.IsOne &&
                input is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.LShr } shr } or && ReferenceEquals(shr.Left, or.Right))
            {
                int removed = (and.HasOneUse ? 1 : 0) + (or.HasOneUse ? 1 : 0) + (shr.HasOneUse ? 1 : 0);
                if (removed >= (shr.Right is LlvmConstant ? 1 : 3))
                {
                    Count("icmp.constant.and-mask.shift-or-bit");
                    LlvmValue bit = Builder.CreateBinary(LlvmOpcode.Shl, c, shr.Right, shr.Name, nuw: true);
                    LlvmValue merged = Builder.CreateOr(bit, c, or.Name);
                    return IccCompare(compare.Predicate, Builder.CreateAnd(or.Right, merged, and.Name), compare.Right);
                }
            }
            if (!F.NoImplicitFloat && ICmpIsEquality(compare.Predicate) && input is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true } cast &&
                cast.Operand.Type.IsIEEELikeFPTy() && (expected.IsZero || expected == mask))
            {
                LlvmAPInt exponent = LlvmAPFloat.GetInf(cast.Operand.Type.GetFltSemantics()).BitcastToAPInt();
                if (mask == exponent)
                {
                    LlvmFPClassTest classes = expected.IsZero ? LlvmFPClassTest.Zero | LlvmFPClassTest.Subnormal : LlvmFPClassTest.NaN | LlvmFPClassTest.Infinity;
                    if (ne) classes = ~classes & LlvmFPClassTest.All;
                    Count("icmp.constant.and-mask.fp-exponent");
                    return ReplaceInstUsesWith(compare, Builder.CreateIsFPClass(cast.Operand, classes));
                }
            }
            return null;
            LlvmInstruction New(LlvmCmpPredicate predicate, LlvmValue value, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.and-mask." + reason); return IccCompare(predicate, value, IccConstant(value, rhs)); }
        }

        private static LlvmValue? IccMatchAndNeg(LlvmBinaryOperator and)
        {
            return Match(and.Left, and.Right) ?? Match(and.Right, and.Left);
            static LlvmValue? Match(LlvmValue a, LlvmValue b) => a is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { Value.IsZero: true } } neg && ReferenceEquals(neg.Right, b) ? b : null;
        }

        private LlvmInstruction? FoldICmpAndConstant(LlvmICmpInstruction compare, LlvmBinaryOperator and, LlvmAPInt constant)
        {
            Count("icmp.constant.and.queries");
            if (FoldICmpAndConstConst(compare, and, constant) is { } folded) return folded;
            LlvmValue x = and.Left, y = and.Right;
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue? negated = IccMatchAndNeg(and);
            if (LlvmValueTracking.IsSignBitCheck(predicate, constant, out bool negative))
            {
                var equality = negative ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe;
                if (x is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { Value.IsAllOnes: true } } add && ReferenceEquals(ICmpMatchNot(y), add.Left))
                    return New(equality, add.Left, LlvmAPInt.GetZero(constant.BitWidth), "predecessor-sign");
                if (negated is not null) return New(equality, negated, LlvmAPInt.GetSignedMinValue(constant.BitWidth), "neg-sign");
            }
            if (y is LlvmConstantInt andMask && x is LlvmLoadInstruction load && load.PointerOperand is LlvmGetElementPtrInstruction gep &&
                FoldCmpLoadFromIndexedGlobal(load, gep, predicate, (LlvmConstant)compare.Right, compare, andMask) is { } indexed) return indexed;
            if (!ICmpIsEquality(predicate)) return null;
            if (constant.BitWidth > 1 && (constant.IsZero || constant.IsOne) && negated is not null)
            {
                if (constant.IsZero) return New(predicate, negated, constant, "neg-zero");
                if (predicate == LlvmCmpPredicate.ICmpEq) return new LlvmCastInstruction(LlvmOpcode.Trunc, negated, compare.Type);
                if (and.HasOneUse)
                {
                    Count("icmp.constant.and.neg-low-bit");
                    return new LlvmBinaryOperator(LlvmOpcode.Xor, Builder.CreateTrunc(negated, compare.Type), LlvmConstantInt.GetBool(compare.Context, true));
                }
            }
            if (ReferenceEquals(compare.Right, y) && (-constant).IsPowerOf2)
                return New(predicate == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUgt : LlvmCmpPredicate.ICmpUle, x, constant - IccInt(constant, 1), "equal-high-mask");
            if (and.HasOneUse && (constant.IsZero || constant.IsOne))
            {
                LlvmCastInstruction? ext = x is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } a ? a : y as LlvmCastInstruction;
                LlvmValue other = ReferenceEquals(ext, x) ? y : x;
                if (ext is { Opcode: LlvmOpcode.ZExt, HasOneUse: true } && ext.Operand.Type.IsIntegerTy(1))
                {
                    Count("icmp.constant.and.boolean-zext");
                    LlvmValue boolean = Builder.CreateAnd(Builder.CreateTrunc(other, ext.Operand.Type), ext.Operand);
                    return ReplaceInstUsesWith(compare, constant.IsZero ^ predicate == LlvmCmpPredicate.ICmpNe ? Builder.CreateNot(boolean) : boolean);
                }
            }
            if (constant.IsZero && and.HasOneUse)
            {
                LlvmBinaryOperator? shift = x as LlvmBinaryOperator;
                if (shift is not { Opcode: LlvmOpcode.Shl, HasOneUse: true, Left: LlvmConstantInt { Value.IsAllOnes: true } }) { shift = y as LlvmBinaryOperator; (x, y) = (y, x); }
                if (shift is { Opcode: LlvmOpcode.Shl, HasOneUse: true, Left: LlvmConstantInt { Value.IsAllOnes: true } })
                    return IccCompare(predicate, Builder.CreateBinary(LlvmOpcode.LShr, y, shift.Right), IccConstant(and, constant));
            }
            if (and.HasOneUse && and.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt addend } addition &&
                and.Right is LlvmConstantInt lowMask && !lowMask.Value.IsZero && (lowMask.Value & (lowMask.Value + IccInt(lowMask.Value, 1))).IsZero && constant.Ule(lowMask.Value))
            {
                Count("icmp.constant.and.offset-mask");
                LlvmValue masked = Builder.CreateAnd(addition.Left, lowMask);
                return IccCompare(predicate, masked, IccConstant(masked, (constant - addend.Value) & lowMask.Value));
            }
            return null;
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmValue value, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.and." + reason); return IccCompare(pred, value, IccConstant(value, rhs)); }
        }
    }
}
