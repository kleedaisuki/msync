// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 1633-1744.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpXorConstant(LlvmICmpInstruction compare, LlvmBinaryOperator xor, LlvmAPInt constant)
        {
            Count("icmp.constant.xor.queries");
            if (FoldICmpXorShiftConst(compare, xor, constant) is { } shifted) return shifted;
            if (xor.Right is not LlvmConstantInt right) return null;
            LlvmValue input = xor.Left;
            LlvmAPInt mask = right.Value;
            LlvmCmpPredicate predicate = compare.Predicate;
            if (LlvmValueTracking.IsSignBitCheck(predicate, constant, out bool negative))
            {
                Count("icmp.constant.xor.sign-bit");
                if (!mask.IsNegative) return ReplaceOperand(compare, 0, input);
                return IccCompare(negative ? LlvmCmpPredicate.ICmpSgt : LlvmCmpPredicate.ICmpSlt,
                    input, IccConstant(input, negative ? LlvmAPInt.GetAllOnes(mask.BitWidth) : LlvmAPInt.GetZero(mask.BitWidth)));
            }
            if (xor.HasOneUse && !ICmpIsEquality(predicate))
            {
                if (mask.IsMinSignedValue) return New(IccFlipSignedness(predicate), constant ^ mask, "sign-mask");
                if (mask.IsMaxSignedValue) return New(LlvmCmpInstruction.GetSwappedPredicate(IccFlipSignedness(predicate)), constant ^ mask, "inverse-sign-mask");
            }
            if (predicate == LlvmCmpPredicate.ICmpUgt && (constant + IccInt(constant, 1)).IsPowerOf2)
            {
                if (mask == ~constant) return New(LlvmCmpPredicate.ICmpUlt, mask, "unsigned-complement");
                if (mask == constant) return New(LlvmCmpPredicate.ICmpUgt, mask, "unsigned-mask");
            }
            if (predicate == LlvmCmpPredicate.ICmpUlt && ((mask == -constant && constant.IsPowerOf2) || (mask == constant && (-constant).IsPowerOf2)))
                return New(LlvmCmpPredicate.ICmpUgt, ~constant, "unsigned-negated-mask");
            return null;
            LlvmInstruction New(LlvmCmpPredicate pred, LlvmAPInt rhs, string reason)
            { if (Metrics is not null) Count("icmp.constant.xor." + reason); return IccCompare(pred, input, IccConstant(input, rhs)); }
        }

        private LlvmInstruction? FoldICmpXorShiftConst(LlvmICmpInstruction compare, LlvmBinaryOperator xor, LlvmAPInt constant)
        {
            LlvmAPInt power;
            if (compare.Predicate == LlvmCmpPredicate.ICmpUlt) power = constant;
            else if (compare.Predicate == LlvmCmpPredicate.ICmpUgt && !constant.IsMaxValue) power = constant + IccInt(constant, 1);
            else return null;
            if (!power.IsPowerOf2 || !xor.HasOneUse || power.IsMinSignedValue) return null;
            LlvmValue? input = Match(xor.Left, xor.Right) ?? Match(xor.Right, xor.Left);
            if (input is null) return null;
            Count("icmp.constant.xor.shift-range");
            var add = Builder.CreateAdd(input, IccConstant(input, power));
            LlvmAPInt bound = power.Shl(1);
            if (compare.Predicate == LlvmCmpPredicate.ICmpUgt) bound -= IccInt(bound, 1);
            return IccCompare(compare.Predicate, add, IccConstant(input, bound));
            static LlvmValue? Match(LlvmValue a, LlvmValue b) => b is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt amount } shift &&
                ReferenceEquals(a, shift.Left) && !amount.Value.IsZero ? a : null;
        }
    }
}
