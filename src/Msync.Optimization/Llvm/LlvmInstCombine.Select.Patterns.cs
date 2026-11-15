// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Scalar PatternMatch spelling adapters used by the source Select.cpp family.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool SelectIsZero(LlvmValue value) => value is LlvmConstantInt { IsZero: true };
        private static bool SelectIsOne(LlvmValue value) => value is LlvmConstantInt constant && constant.Value.IsOne;
        private static bool SelectIsOnes(LlvmValue value) => value is LlvmConstantInt constant && constant.Value.IsAllOnes;
        private static bool SelectMatchNeg(LlvmValue value, out LlvmValue operand)
        {
            operand = value;
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } binary || !SelectIsZero(binary.Left)) return false;
            operand = binary.Right; return true;
        }
        private static bool SelectMatchNot(LlvmValue value, out LlvmValue operand)
        {
            operand = value;
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } binary || !SelectIsOnes(binary.Right)) return false;
            operand = binary.Left; return true;
        }
        private static bool SelectMatchLike(LlvmValue value, out LlvmValue condition, out LlvmValue whenTrue, out LlvmValue whenFalse)
        {
            condition = whenTrue = whenFalse = value;
            if (value is LlvmSelectInstruction select)
            { condition = select.Condition; whenTrue = select.TrueValue; whenFalse = select.FalseValue; return true; }
            if (value is LlvmCastInstruction cast && cast.Opcode is LlvmOpcode.ZExt or LlvmOpcode.SExt && cast.Operand.Type.IsIntegerTy(1))
            {
                condition = cast.Operand;
                whenTrue = cast.Opcode == LlvmOpcode.ZExt ? LlvmConstantInt.Get((LlvmIntegerType)value.Type, 1) :
                    LlvmConstantInt.Get((LlvmIntegerType)value.Type, LlvmAPInt.GetAllOnes(value.Type.GetIntegerBitWidth()));
                whenFalse = LlvmConstant.GetNullValue(value.Type); return true;
            }
            return false;
        }
        private static bool SelectMatchLogical(LlvmValue value, bool isAnd, out LlvmValue left, out LlvmValue right)
        {
            left = right = value;
            if (!value.Type.IsIntegerTy(1)) return false;
            if (value is LlvmBinaryOperator binary && binary.Opcode == (isAnd ? LlvmOpcode.And : LlvmOpcode.Or))
            { left = binary.Left; right = binary.Right; return true; }
            if (value is LlvmSelectInstruction select && ReferenceEquals(select.Condition.Type, select.Type) &&
                (isAnd ? SelectIsZero(select.FalseValue) : SelectIsOne(select.TrueValue)))
            { left = select.Condition; right = isAnd ? select.TrueValue : select.FalseValue; return true; }
            return false;
        }
    }
}
