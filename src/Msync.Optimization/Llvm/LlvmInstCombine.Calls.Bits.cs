// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// foldCttzCtlz (470–649), foldCtpop (651–737), fshl/fshr visitor (2705–2820).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldCttzCtlz(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.count_zeros.visited");
            bool trailing = kind == LlvmIntrinsicKind.CTTZ;
            LlvmValue value = call.GetArgument(0), zeroPoison = call.GetArgument(1);
            if (CallsIntrinsic(value, LlvmIntrinsicKind.BitReverse, out var reversed))
                return CallsNewIntrinsic(call, trailing ? LlvmIntrinsicKind.CTLZ : LlvmIntrinsicKind.CTTZ, [reversed.GetArgument(0), zeroPoison]);
            if (call.Type.IsIntegerTy(1))
                return CallsIsZero(zeroPoison) ? new LlvmBinaryOperator(LlvmOpcode.Xor, value, CallsInt(call.Type, 1))
                    : ReplaceInstUsesWith(call, CallsInt(call.Type, 0));
            if (call.HasOneUse && CallsIsZero(zeroPoison) && call.Users.First() is LlvmBinaryOperator user
                && IsShiftOp(user.Opcode) && ReferenceEquals(user.Right, call))
                return CallsNewIntrinsic(call, kind, [value, LlvmConstantInt.GetTrue(call.Context)]);
            if (trailing)
            {
                if (CallsNeg(value, out var negated)) return CallsNewIntrinsic(call, kind, [negated, zeroPoison]);
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } andOp
                    && (CallsNeg(andOp.Left, out var left) && ReferenceEquals(left, andOp.Right)
                        || CallsNeg(andOp.Right, out left) && ReferenceEquals(left, andOp.Left)))
                    return CallsNewIntrinsic(call, kind, [left, zeroPoison]);
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, Right: LlvmConstantInt odd } mul && (odd.Value.GetWord(0) & 1) != 0)
                    return CallsNewIntrinsic(call, kind, [mul.Left, zeroPoison]);
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } sext)
                    return ReplaceInstUsesWith(call, Builder.CreateIntrinsic(kind, [Builder.CreateZExt(sext.Operand, call.Type), zeroPoison]));
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } zext && CallsIsOne(zeroPoison))
                    return ReplaceInstUsesWith(call, Builder.CreateZExt(Builder.CreateIntrinsic(kind, [zext.Operand, LlvmConstantInt.GetTrue(call.Context)]), call.Type));
                var select = LlvmSelectPattern.Match(value, Metrics);
                if (select.Flavor is LlvmSelectPatternFlavor.Abs or LlvmSelectPatternFlavor.NAbs)
                    return CallsNewIntrinsic(call, kind, [select.LHS!, zeroPoison]);
                if (CallsIntrinsic(value, LlvmIntrinsicKind.Abs, out var abs))
                    return CallsNewIntrinsic(call, kind, [abs.GetArgument(0), zeroPoison]);
                if (CallsIsOne(zeroPoison) && value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstant constant } shift)
                    return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateIntrinsic(kind, [constant, zeroPoison]), shift.Right);
                if (CallsIsOne(zeroPoison) && value is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, IsExact: true, Left: LlvmConstant rightConstant } right)
                    return new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateIntrinsic(kind, [rightConstant, zeroPoison]), right.Right);
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsOne: true },
                        Left: LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmConstantInt { IsMinusOne: true } } onesShift })
                    return new LlvmBinaryOperator(LlvmOpcode.Sub, CallsInt(call.Type, call.Type.GetScalarSizeInBits()), onesShift.Right);
            }
            else
            {
                if (CallsIsOne(zeroPoison) && value is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmConstant constant } shift)
                    return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateIntrinsic(kind, [constant, zeroPoison]), shift.Right);
                if (CallsIsOne(zeroPoison) && value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true, Left: LlvmConstant leftConstant } left)
                    return new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateIntrinsic(kind, [leftConstant, zeroPoison]), left.Right);
                if (value.HasOneUse && CallsNotAndDecrement(value, out var operand))
                {
                    LlvmValue cttz = Builder.CreateIntrinsic(LlvmIntrinsicKind.CTTZ, [operand, LlvmConstantInt.GetFalse(call.Context)]);
                    return ReplaceInstUsesWith(call, Builder.CreateSub(CallsInt(call.Type, call.Type.GetScalarSizeInBits()), cttz));
                }
            }
            if (TryGetLog2(value, CallsIsOne(zeroPoison)) is { } logarithm)
            {
                if (trailing) return ReplaceInstUsesWith(call, logarithm);
                return new LlvmBinaryOperator(LlvmOpcode.Sub, CallsInt(call.Type, call.Type.GetScalarSizeInBits() - 1), logarithm)
                    { HasNoSignedWrap = true, HasNoUnsignedWrap = true };
            }
            LlvmKnownBits known = CallsKnownBits(value, call);
            uint possible = trailing ? known.CountMaxTrailingZeros() : known.CountMaxLeadingZeros();
            uint definite = trailing ? known.CountMinTrailingZeros() : known.CountMinLeadingZeros();
            if (possible == definite) return ReplaceInstUsesWith(call, CallsInt(value.Type, definite));
            if ((!known.One.IsZero || CallsKnownNonZero(value, call)) && !CallsIsOne(zeroPoison))
                return CallsNewIntrinsic(call, kind, [value, LlvmConstantInt.GetTrue(call.Context)]);
            return CallsSetCountZeroRange(call, definite, possible);
        }
        private LlvmInstruction? FoldCtpop(LlvmCallInstruction call)
        {
            Count("calls.ctpop.visited");
            LlvmValue value = call.GetArgument(0);
            if (CallsIntrinsic(value, LlvmIntrinsicKind.BitReverse, out var bitrev) || CallsIntrinsic(value, LlvmIntrinsicKind.BSwap, out bitrev))
                return CallsNewIntrinsic(call, LlvmIntrinsicKind.CTPop, [bitrev.GetArgument(0)]);
            if ((CallsIntrinsic(value, LlvmIntrinsicKind.FShl, out var rotate) || CallsIntrinsic(value, LlvmIntrinsicKind.FShr, out rotate))
                && ReferenceEquals(rotate.GetArgument(0), rotate.GetArgument(1)))
                return CallsNewIntrinsic(call, LlvmIntrinsicKind.CTPop, [rotate.GetArgument(0)]);
            if (value.HasOneUse && value is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } orOp
                && (CallsNeg(orOp.Left, out var operand) && ReferenceEquals(operand, orOp.Right)
                    || CallsNeg(orOp.Right, out operand) && ReferenceEquals(operand, orOp.Left)))
            {
                LlvmValue zeros = Builder.CreateIntrinsic(LlvmIntrinsicKind.CTTZ, [operand, LlvmConstantInt.GetFalse(call.Context)]);
                return ReplaceInstUsesWith(call, Builder.CreateSub(CallsInt(call.Type, call.Type.GetScalarSizeInBits()), zeros));
            }
            if (CallsNotAndDecrement(value, out var decremented))
                return CallsNewIntrinsic(call, LlvmIntrinsicKind.CTTZ, [decremented, LlvmConstantInt.GetFalse(call.Context)]);
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } zext)
                return new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [zext.Operand]), call.Type);
            LlvmKnownBits known = CallsKnownBits(value, call);
            if ((~known.Zero).IsPowerOf2)
                return new LlvmBinaryOperator(LlvmOpcode.LShr, value, CallsInt(call.Type, (ulong)(~known.Zero).CountTrailingZeros()));
            if (CallsPowerOfTwo(value, call, true))
                return new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, value, LlvmConstant.GetNullValue(call.Type)), call.Type);
            return CallsSetCtpopRange(call, known);
        }
        private LlvmInstruction? FoldFunnelShift(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.funnel.visited");
            LlvmValue first = call.GetArgument(0), second = call.GetArgument(1), amount = call.GetArgument(2);
            uint width = call.Type.GetScalarSizeInBits();
            if (amount is LlvmConstantInt constant)
            {
                LlvmAPInt modulo = constant.Value.URem(new(width, width));
                if (modulo != constant.Value) return CallsNewIntrinsic(call, kind, [first, second, CallsInt(call.Type, modulo)]);
                if (kind == LlvmIntrinsicKind.FShr)
                {
                    if (!CallsKnownNonZero(amount, call)) return null;
                    return CallsNewIntrinsic(call, LlvmIntrinsicKind.FShl, [first, second, CallsInt(call.Type, new LlvmAPInt(width, width) - constant.Value)]);
                }
                if (CallsIsZero(second) || second is LlvmUndefValue) return new LlvmBinaryOperator(LlvmOpcode.Shl, first, amount);
                if ((CallsIsZero(first) || first is LlvmUndefValue) && CallsKnownNonZero(amount, call))
                    return new LlvmBinaryOperator(LlvmOpcode.LShr, second, CallsInt(call.Type, new LlvmAPInt(width, width) - constant.Value));
                if (ReferenceEquals(first, second) && width == 16 && constant.EqualsInt(8))
                    return CallsNewIntrinsic(call, LlvmIntrinsicKind.BSwap, [first]);
                if (MatchBSwapOrBitReverse(call, true, true) is { } bitop) return bitop;
                if (ReferenceEquals(first, second) && CallsIntrinsic(first, LlvmIntrinsicKind.FShl, out var inner)
                    && ReferenceEquals(inner.GetArgument(0), inner.GetArgument(1)) && inner.GetArgument(2) is LlvmConstantInt innerAmount)
                {
                    LlvmAPInt combined = (constant.Value + innerAmount.Value).URem(new(width, width));
                    if (combined.IsZero) return ReplaceInstUsesWith(call, inner.GetArgument(0));
                    return CallsNewIntrinsic(call, LlvmIntrinsicKind.FShl, [inner.GetArgument(0), inner.GetArgument(0), CallsInt(call.Type, combined)]);
                }
            }
            bool powerWidth = (width & (width - 1)) == 0;
            if (ReferenceEquals(first, second) && powerWidth && CallsNeg(amount, out var shift))
                return CallsNewIntrinsic(call, kind == LlvmIntrinsicKind.FShl ? LlvmIntrinsicKind.FShr : LlvmIntrinsicKind.FShl, [first, second, shift]);
            if (kind == LlvmIntrinsicKind.FShl && powerWidth && CallsIsZero(second))
                return new LlvmBinaryOperator(LlvmOpcode.Shl, first, Builder.CreateAnd(amount, CallsInt(call.Type, width - 1)));
            if (SimplifyDemandedInstructionBits(call)) return call;
            if (!powerWidth) return null;
            uint demanded = 0; for (uint value = width - 1; value != 0; value >>= 1) ++demanded;
            LlvmKnownBits amountKnown = new(width);
            return SimplifyDemandedBits(call, 2, LlvmAPInt.GetLowBitsSet(width, demanded), ref amountKnown, Query(call)) ? call : null;
        }
    }
}
