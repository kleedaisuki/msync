// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp
// transformZExtICmp / transformSExtICmp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction CastIntegerResult(LlvmInstruction result, string pattern)
        { if (Metrics is not null) Count("instcombine.casts.integer." + pattern); return result; }
        private LlvmInstruction? CastIntegerReplace(LlvmInstruction old, LlvmValue value, string pattern)
        { if (Metrics is not null) Count("instcombine.casts.integer." + pattern); return ReplaceInstUsesWith(old, value); }
        private static LlvmCastInstruction IntegerCastInstruction(LlvmValue value, LlvmType destination, bool signed) =>
            new(value.Type == destination ? LlvmOpcode.BitCast : value.Type.GetScalarSizeInBits() > destination.GetScalarSizeInBits() ? LlvmOpcode.Trunc : signed ? LlvmOpcode.SExt : LlvmOpcode.ZExt, value, destination);
        private LlvmInstruction? TransformZExtICmp(LlvmICmpInstruction compare, LlvmCastInstruction extension)
        {
            bool equality = compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
            if (compare.Right is LlvmConstantInt constant)
            {
                if (compare.Predicate == LlvmCmpPredicate.ICmpSlt && constant.IsZero)
                {
                    LlvmValue value = Builder.CreateBinOp(LlvmOpcode.LShr, compare.Left, LogicC(compare.Left.Type, compare.Left.Type.GetScalarSizeInBits() - 1), compare.Left.Name + ".lobit");
                    if (value.Type != extension.Type) value = Builder.CreateIntCast(value, extension.Type, false);
                    return CastIntegerReplace(extension, value, "zext_icmp_sign");
                }
                if (constant.IsZero && equality)
                {
                    LlvmAPInt possible = ~ComputeKnownBits(compare.Left, extension).Zero;
                    uint amount = possible.GetActiveBits() == 0 ? uint.MaxValue : possible.GetActiveBits() - 1;
                    if (possible.IsPowerOf2 && extension.Type.GetScalarSizeInBits() != amount + 1 &&
                        (compare.Left.Type == extension.Type || compare.Predicate == LlvmCmpPredicate.ICmpNe || amount == 0))
                    {
                        LlvmValue value = compare.Left;
                        if (amount != 0) value = Builder.CreateBinOp(LlvmOpcode.LShr, value, LogicC(value.Type, amount), value.Name + ".lobit");
                        if (compare.Predicate == LlvmCmpPredicate.ICmpEq) value = Builder.CreateXor(value, LogicC(value.Type, 1));
                        if (value.Type != extension.Type) value = Builder.CreateIntCast(value, extension.Type, false);
                        return CastIntegerReplace(extension, value, "zext_icmp_single_bit");
                    }
                }
            }
            if (equality && compare.HasOneUse && compare.Right is LlvmConstantInt { IsZero: true } && compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and)
            {
                LlvmValue x = and.Left, shiftValue = and.Right;
                if (shiftValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } }) (x, shiftValue) = (shiftValue, x);
                if (shiftValue is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsOne: true } } shift &&
                    (extension.Type == and.Type || compare.Predicate != LlvmCmpPredicate.ICmpEq || shift.HasOneUse))
                {
                    if (compare.Predicate == LlvmCmpPredicate.ICmpEq) x = Builder.CreateNot(x);
                    LlvmValue shifted = Builder.CreateBinOp(LlvmOpcode.LShr, x, shift.Right);
                    LlvmValue bit = Builder.CreateAnd(shifted, LogicC(x.Type, 1));
                    return CastIntegerReplace(extension, Builder.CreateZExtOrTrunc(bit, extension.Type), "zext_icmp_variable_bit");
                }
            }
            return null;
        }
        private LlvmInstruction? TransformSExtICmp(LlvmICmpInstruction compare, LlvmCastInstruction extension)
        {
            LlvmValue operand = compare.Left;
            if (!compare.Right.Type.IsIntegerTy()) return null;
            if (compare.Predicate == LlvmCmpPredicate.ICmpSlt && compare.Right is LlvmConstantInt { IsZero: true })
            {
                LlvmValue value = Builder.CreateBinOp(LlvmOpcode.AShr, operand, LogicC(operand.Type, operand.Type.GetScalarSizeInBits() - 1), operand.Name + ".lobit");
                if (value.Type != extension.Type) value = Builder.CreateIntCast(value, extension.Type, true);
                return CastIntegerReplace(extension, value, "sext_icmp_sign");
            }
            if (compare.Right is not LlvmConstantInt constant || !compare.HasOneUse ||
                compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) || (!constant.IsZero && !constant.Value.IsPowerOf2)) return null;
            LlvmAPInt possible = ~ComputeKnownBits(operand, extension).Zero;
            if (!possible.IsPowerOf2) return null;
            if (!constant.IsZero && constant.Value != possible)
                return CastIntegerReplace(extension, compare.Predicate == LlvmCmpPredicate.ICmpNe ? LogicOnes(extension.Type) : LlvmConstant.GetNullValue(extension.Type), "sext_icmp_known_zero_bit");
            LlvmValue input = operand;
            if ((!constant.IsZero) == (compare.Predicate == LlvmCmpPredicate.ICmpNe))
            {
                uint amount = possible.CountTrailingZeros();
                if (amount != 0) input = Builder.CreateBinOp(LlvmOpcode.LShr, input, LogicC(input.Type, amount));
                input = Builder.CreateAdd(input, LogicOnes(input.Type), "sext");
            }
            else
            {
                uint amount = possible.CountLeadingZeros();
                if (amount != 0) input = Builder.CreateBinOp(LlvmOpcode.Shl, input, LogicC(input.Type, amount));
                input = Builder.CreateBinOp(LlvmOpcode.AShr, input, LogicC(input.Type, possible.BitWidth - 1), "sext");
            }
            if (extension.Type == input.Type) return CastIntegerReplace(extension, input, "sext_icmp_single_bit");
            return CastIntegerResult(IntegerCastInstruction(input, extension.Type, true), "sext_icmp_single_bit_cast");
        }
    }
}
