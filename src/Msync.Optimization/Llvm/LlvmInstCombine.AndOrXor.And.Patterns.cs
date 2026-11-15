// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: visitAnd scalar local patterns.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldAndShiftedPower(LlvmBinaryOperator i, LlvmAPInt mask)
        {
            if (!mask.IsPowerOf2 || i.Left is not LlvmBinaryOperator { HasOneUse: true } outer) return null;
            uint width = i.Type.GetScalarSizeInBits();
            uint maskBit = mask.CountTrailingZeros();
            if (outer.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr && outer.Left is LlvmConstantInt power && power.Value.IsPowerOf2)
            {
                int bit = outer.Opcode == LlvmOpcode.Shl ? (int)maskBit - (int)power.Value.CountTrailingZeros() : (int)power.Value.CountTrailingZeros() - (int)maskBit;
                Debug.Assert(bit >= 0, "Demanded bits must handle an impossible power-of-two mask");
                return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, outer.Right, LogicC(i.Type, unchecked((ulong)bit))), i.Right, LogicZero(i.Type), "and.shifted_pow2_bit_test");
            }
            if (outer.Right is not LlvmConstantInt shift || outer.Left is not LlvmBinaryOperator { Left: LlvmConstantInt c1 } inner || !c1.Value.IsPowerOf2) return null;
            var log1 = new LlvmAPInt(width, c1.Value.CountTrailingZeros());
            var log3 = new LlvmAPInt(width, maskBit);
            if (outer.Opcode == LlvmOpcode.LShr && inner.Opcode == LlvmOpcode.Shl)
            {
                var total = shift.Value + log3;
                if (total.Ult(new(width, width)))
                    return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, inner.Right, LogicC(i.Type, total - log1)), i.Right, LogicZero(i.Type), "and.double_shift_pow2_bit_test");
            }
            if (outer.Opcode == LlvmOpcode.Shl && inner.Opcode == LlvmOpcode.LShr && log3.Uge(shift.Value))
                return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, inner.Right, LogicC(i.Type, shift.Value + log1 - log3)), i.Right, LogicZero(i.Type), "and.reverse_double_shift_pow2_bit_test");
            return null;
        }
        private LlvmInstruction? FoldAndUnsignedClamp(LlvmBinaryOperator i, LlvmAPInt mask)
        {
            if (!LogicMask(mask) || i.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } or) return null;
            uint width = i.Type.GetScalarSizeInBits();
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var shifted = orientation == 0 ? or.Left : or.Right;
                var x = orientation == 0 ? or.Right : or.Left;
                if (shifted is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr or LlvmOpcode.AShr,
                    Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt c } sub,
                    Right: LlvmConstantInt amount } shift || c.Value != mask || !ReferenceEquals(sub.Right, x) || !amount.Value.Ult(new(width, width))) continue;
                uint s = mask.PopCount(), shiftAmount = (uint)amount.GetZExtValue();
                if (s > shiftAmount || (shift.Opcode != LlvmOpcode.AShr && shiftAmount + s > width) || !LogicMaskedZero(x, LlvmAPInt.GetBitsSetFrom(width, shiftAmount), i)) continue;
                return LogicReplace(i, Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.UMin, x, i.Right), "and.unsigned_clamp");
            }
            return null;
        }
        private LlvmInstruction? FoldAndFloatingAbs(LlvmBinaryOperator i)
        {
            if (!F.NoImplicitFloat && i.Left is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast && cast.Operand.Type.IsFloatingPointTy()
                && i.Right is LlvmConstantInt mask && mask.Value == LlvmAPInt.GetSignedMaxValue(i.Type.GetScalarSizeInBits()))
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.BitCast, Builder.CreateFAbs(cast.Operand), i.Type), "and.float_abs");
            return null;
        }
        private LlvmInstruction? FoldAndXorOrIdentities(LlvmBinaryOperator i)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var value = orientation == 0 ? i.Left : i.Right;
                var other = orientation == 0 ? i.Right : i.Left;
                if (LogicNot(other, out var inverted) && LogicOther(inverted, LlvmOpcode.Xor, value, out var b))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, value, b), "and.not_xor_absorption");
            }
            if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } left && i.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor,
                Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } inner } right && ReferenceEquals(right.Right, left.Left) && ReferenceEquals(inner.Left, left.Right))
            {
                var notC = i.Right.HasOneUse ? Builder.CreateNot(inner.Right) : GetFreelyInverted(inner.Right, inner.Right.HasOneUse, Builder);
                if (notC is not null) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, i.Left, notC), "and.xor_chain");
            }
            if (i.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } innerLeft } leftChain
                && i.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } rightPair && ReferenceEquals(rightPair.Left, leftChain.Right) && ReferenceEquals(rightPair.Right, innerLeft.Left))
            {
                var notC = i.Left.HasOneUse ? Builder.CreateNot(innerLeft.Right) : GetFreelyInverted(innerLeft.Right, innerLeft.Right.HasOneUse, Builder);
                if (notC is not null) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, i.Right, notC), "and.reverse_xor_chain");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var value = orientation == 0 ? i.Left : i.Right;
                var other = orientation == 0 ? i.Right : i.Left;
                for (int order = 0; order < 2; ++order)
                {
                    if (LogicNotPairOperands(other, LlvmOpcode.Xor, order != 0, out var b, out var a) && LogicPair(value, LlvmOpcode.Or, a, b))
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, a, b), "and.or_xor_inverted");
                    if (LogicNotPairOperands(value, LlvmOpcode.Or, order != 0, out b, out a) && LogicPair(other, LlvmOpcode.Xor, a, b))
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateNot(a), b), "and.inverted_or_xor");
                }
            }
            return null;
        }
        private LlvmInstruction? FoldAndBooleanMasks(LlvmBinaryOperator i)
        {
            for (int inverted = 0; inverted < 2; ++inverted)
                for (int orientation = 0; orientation < 2; ++orientation)
                {
                    var value = orientation == 0 ? i.Left : i.Right;
                    var b = orientation == 0 ? i.Right : i.Left;
                    if (inverted != 0 && !LogicNot(value, out value)) continue;
                    if (value is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext && sext.Operand.Type.IsIntegerTy(1))
                        return inverted == 0 ? LogicSelect(sext.Operand, b, LogicZero(i.Type), "and.sext_bool") : LogicSelect(sext.Operand, LogicZero(i.Type), b, "and.not_sext_bool");
                }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var value = orientation == 0 ? i.Left : i.Right;
                var b = orientation == 0 ? i.Right : i.Left;
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } zext && zext.Operand.Type.IsIntegerTy(1))
                    return LogicSelect(zext.Operand, Builder.CreateAnd(b, LogicC(i.Type, 1)), LogicZero(i.Type), "and.zext_bool");
            }
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var value = orientation == 0 ? i.Left : i.Right;
                var b = orientation == 0 ? i.Right : i.Left;
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true, Right: LlvmConstantInt { IsMinusOne: true } } add)
                {
                    var a = LogicCastOrSelf(add.Left, LlvmOpcode.ZExt);
                    if (a.Type.IsIntegerTy(1)) return LogicSelect(a, LogicZero(i.Type), b, "and.decrement_bool");
                    if (LogicKnown(a, i).CountMaxActiveBits() <= 1)
                        return LogicSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, a, LogicZero(a.Type)), b, LogicZero(i.Type), "and.decrement_known_bool");
                }
            }
            for (int inverted = 0; inverted < 2; ++inverted)
                for (int orientation = 0; orientation < 2; ++orientation)
                {
                    var value = orientation == 0 ? i.Left : i.Right;
                    var b = orientation == 0 ? i.Right : i.Left;
                    if (!value.HasOneUse) continue;
                    value = LogicCastOrSelf(value, LlvmOpcode.SExt);
                    if (inverted != 0 && !LogicNot(value, out value)) continue;
                    if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt amount } shift
                        || amount.Value != new LlvmAPInt(amount.BitWidth, shift.Left.Type.GetScalarSizeInBits() - 1)) continue;
                    var negative = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, shift.Left, LogicZero(shift.Left.Type), "isneg");
                    return inverted == 0 ? LogicSelect(negative, b, LogicZero(i.Type), "and.sign_shift") : LogicSelect(negative, LogicZero(i.Type), b, "and.not_sign_shift");
                }
            return null;
        }
    }
}
