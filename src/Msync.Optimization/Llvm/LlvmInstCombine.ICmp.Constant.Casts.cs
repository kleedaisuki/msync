// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 1484-1632.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmValue? IccMatchSignum(LlvmValue value)
        {
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } binary) return null;
            return Match(binary.Left, binary.Right) ?? Match(binary.Right, binary.Left);
            static LlvmValue? Match(LlvmValue first, LlvmValue second)
            {
                if (first is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt a } arithmetic ||
                    second is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt b,
                        Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt zero } neg } ||
                    !zero.Value.IsZero || !ReferenceEquals(arithmetic.Left, neg.Right)) return null;
                uint width = first.Type.GetIntegerBitWidth();
                return a.Value == new LlvmAPInt(width, width - 1) && b.Value == a.Value ? arithmetic.Left : null;
            }
        }

        private LlvmInstruction? FoldICmpTruncConstant(LlvmICmpInstruction compare, LlvmCastInstruction trunc, LlvmAPInt constant)
        {
            Count("icmp.constant.trunc.queries");
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue input = trunc.Operand;
            uint src = input.Type.GetIntegerBitWidth(), dst = trunc.Type.GetIntegerBitWidth();
            if (ShouldChangeType(trunc.Type, input.Type))
            {
                if (trunc.HasNoSignedWrap) return New(input, constant.SExt(src), "nsw");
                if (!ICmpIsSigned(predicate) && trunc.HasNoUnsignedWrap) return New(input, constant.ZExt(src), "nuw");
            }
            if (constant.IsOne && constant.BitWidth > 1 && predicate == LlvmCmpPredicate.ICmpSlt && IccMatchSignum(input) is { } signum)
                return New(signum, new LlvmAPInt(src, 1), "signum");
            if (ICmpIsEquality(predicate) && input is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt power } shift &&
                power.Value.IsPowerOf2 && dst > IccLog2(power.Value))
            {
                if (constant.IsZero)
                {
                    Count("icmp.constant.trunc.power-zero");
                    return IccCompare(predicate == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpUlt,
                        shift.Right, IccConstant(input, (ulong)(dst - IccLog2(power.Value))));
                }
                if (constant.IsPowerOf2) return New(shift.Right, new LlvmAPInt(src, unchecked(IccLog2(constant) - IccLog2(power.Value)), implicitTrunc: true), "power-equal");
            }
            if (ICmpIsEquality(predicate) && (trunc.HasOneUse || trunc.HasNoUnsignedWrap))
            {
                if (ShouldChangeType(dst, src))
                {
                    LlvmValue masked = trunc.HasNoUnsignedWrap ? input : Builder.CreateAnd(input, IccConstant(input, LlvmAPInt.GetLowBitsSet(src, dst)));
                    return New(masked, constant.ZExt(src), "widen-mask");
                }
                var known = ComputeKnownBits(input, compare);
                if ((known.Zero | known.One).CountLeadingOnes() >= src - dst)
                    return New(input, constant.ZExt(src) | (known.One & LlvmAPInt.GetHighBitsSet(src, src - dst)), "known-high-bits");
            }
            if (LlvmValueTracking.IsSignBitCheck(predicate, constant, out bool negative) &&
                input is LlvmBinaryOperator { Right: LlvmConstantInt amount } rightShift && rightShift.Opcode is LlvmOpcode.AShr or LlvmOpcode.LShr &&
                amount.Value.Ule(new LlvmAPInt(src, src)) && dst == src - amount.Value.GetZExtValue())
            {
                Count("icmp.constant.trunc.shift-sign");
                return IccCompare(negative ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSgt,
                    rightShift.Left, IccConstant(input, negative ? LlvmAPInt.GetZero(src) : LlvmAPInt.GetAllOnes(src)));
            }
            return null;
            LlvmInstruction New(LlvmValue value, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.trunc." + reason); return IccCompare(predicate, value, IccConstant(value, rhs)); }
        }

        private LlvmInstruction? FoldICmpTruncWithTruncOrExt(LlvmICmpInstruction compare, LlvmInstructionSimplify.BinaryQuery query)
        {
            Count("icmp.trunc-cast.queries");
            LlvmCmpPredicate predicate = compare.Predicate;
            LlvmValue x, y;
            bool signed = false;
            if (compare.Left is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } left && compare.Right is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } right)
            {
                bool nsw = left.HasNoSignedWrap && right.HasNoSignedWrap;
                bool nuw = left.HasNoUnsignedWrap && right.HasNoUnsignedWrap;
                if (ICmpIsSigned(predicate) ? !nsw : !nsw && !nuw) return null;
                x = left.Operand; y = right.Operand;
                if (x.Type != y.Type && (!left.HasOneUse || !right.HasOneUse)) return null;
                if (!IsDesirableIntType(x.Type.GetIntegerBitWidth()) && IsDesirableIntType(y.Type.GetIntegerBitWidth()))
                { (x, y) = (y, x); predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
                signed = !nuw;
            }
            else if (!Match(compare.Left, compare.Right, false, out x, out y, out signed) &&
                     !Match(compare.Right, compare.Left, true, out x, out y, out signed)) return null;
            if (IsDesirableIntType(compare.Left.Type.GetIntegerBitWidth()) && !IsDesirableIntType(x.Type.GetIntegerBitWidth())) return null;
            Count("icmp.trunc-cast.rewrites");
            return IccCompare(predicate, x, Builder.CreateIntCast(y, x.Type, signed));
            bool Match(LlvmValue a, LlvmValue b, bool swap, out LlvmValue input, out LlvmValue other, out bool sext)
            {
                input = other = null!; sext = false;
                if (a is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc || b is not LlvmCastInstruction extension || !extension.HasOneUse) return false;
                bool unsignedMatch = !ICmpIsSigned(predicate) && trunc.HasNoUnsignedWrap && extension.Opcode == LlvmOpcode.ZExt;
                bool signedMatch = trunc.HasNoSignedWrap && extension.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt;
                if (!unsignedMatch && !signedMatch) return false;
                input = trunc.Operand; other = extension.Operand; sext = !unsignedMatch && extension.Opcode == LlvmOpcode.SExt;
                if (swap) predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
                return true;
            }
        }
    }
}
