// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp narrowFunnelShift/narrowBinOp.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? NarrowFunnelShift(LlvmCastInstruction truncation)
        {
            uint narrow = truncation.Type.GetScalarSizeInBits(), wide = truncation.Operand.Type.GetScalarSizeInBits();
            if ((narrow & (narrow - 1)) != 0 || truncation.Operand is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true, Left: LlvmBinaryOperator left, Right: LlvmBinaryOperator right } ||
                !left.HasOneUse || !right.HasOneUse || left.Opcode is not (LlvmOpcode.Shl or LlvmOpcode.LShr) || right.Opcode is not (LlvmOpcode.Shl or LlvmOpcode.LShr) || left.Opcode == right.Opcode) return null;
            if (left.Opcode == LlvmOpcode.LShr) (left, right) = (right, left);
            LlvmValue? MatchAmount(LlvmValue first, LlvmValue second)
            {
                uint amountBits = (uint)BitOperations.Log2(narrow);
                LlvmAPInt high = ~LlvmAPInt.GetLowBitsSet(wide, amountBits);
                if ((ReferenceEquals(left.Left, right.Left) || MaskedValueIsZero(first, high)) &&
                    second is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasOneUse: true, Left: LlvmConstantInt constant } sub &&
                    constant.Value.GetLimitedValue(narrow + 1UL) == narrow && ReferenceEquals(sub.Right, first)) return first;
                if (!ReferenceEquals(left.Left, right.Left)) return null;
                bool MatchMasked(LlvmValue firstValue, LlvmValue secondValue, out LlvmValue x)
                {
                    if (firstValue is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask0 } and0 && mask0.Value.GetLimitedValue(narrow) == narrow - 1 &&
                        secondValue is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask1 } and1 && mask1.Value.GetLimitedValue(narrow) == narrow - 1 &&
                        LogicNeg(and1.Left, out var negated) && ReferenceEquals(and0.Left, negated)) { x = and0.Left; return true; }
                    x = firstValue; return false;
                }
                if (MatchMasked(first, second, out var value)) return value;
                if (first is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext0 && second is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext1 && MatchMasked(ext0.Operand, ext1.Operand, out value)) return value;
                return null;
            }
            LlvmValue? amount = MatchAmount(left.Right, right.Right);
            bool isLeft = amount is not null;
            amount ??= MatchAmount(right.Right, left.Right);
            if (amount is null || !MaskedValueIsZero(right.Left, LlvmAPInt.GetHighBitsSet(wide, wide - narrow), truncation)) return null;
            LlvmValue adjusted = Builder.CreateZExtOrTrunc(amount, truncation.Type);
            LlvmValue x = Builder.CreateTrunc(left.Left, truncation.Type);
            LlvmValue y = ReferenceEquals(left.Left, right.Left) ? x : Builder.CreateTrunc(right.Left, truncation.Type);
            LlvmIntrinsicKind kind = isLeft ? LlvmIntrinsicKind.FShl : LlvmIntrinsicKind.FShr;
            LlvmFunction function = LlvmIntrinsic.GetDeclaration(F.Parent!, kind, (LlvmIntegerType)truncation.Type, Metrics);
            return CastIntegerResult(new LlvmCallInstruction(function.FunctionType, function, [x, y, adjusted]), "narrow_funnel_shift");
        }
        private LlvmInstruction? NarrowBinOp(LlvmCastInstruction truncation)
        {
            LlvmType source = truncation.Operand.Type, destination = truncation.Type;
            uint wide = source.GetScalarSizeInBits(), narrow = destination.GetScalarSizeInBits();
            if (!ShouldChangeType(source, destination) || truncation.Operand is not LlvmBinaryOperator { HasOneUse: true } binary) return null;
            if (binary.Opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul)
            {
                if (binary.Left is LlvmConstant left)
                    return CastIntegerResult(new LlvmBinaryOperator(binary.Opcode, LlvmConstantExpr.GetTrunc(left, destination), Builder.CreateTrunc(binary.Right, destination)), "narrow_binop_left_constant");
                if (binary.Right is LlvmConstant right)
                    return CastIntegerResult(new LlvmBinaryOperator(binary.Opcode, Builder.CreateTrunc(binary.Left, destination), LlvmConstantExpr.GetTrunc(right, destination)), "narrow_binop_right_constant");
                if (binary.Left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } ext0 && ext0.Operand.Type == destination)
                    return CastIntegerResult(new LlvmBinaryOperator(binary.Opcode, ext0.Operand, Builder.CreateTrunc(binary.Right, destination)), "narrow_binop_left_extension");
                if (binary.Right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } ext1 && ext1.Operand.Type == destination)
                    return CastIntegerResult(new LlvmBinaryOperator(binary.Opcode, Builder.CreateTrunc(binary.Left, destination), ext1.Operand), "narrow_binop_right_extension");
            }
            if (binary.Opcode is LlvmOpcode.LShr or LlvmOpcode.AShr && binary.Left is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } inner &&
                binary.Right is LlvmConstantInt amount && amount.Value.GetLimitedValue((ulong)wide - narrow + 1) <= wide - narrow)
            {
                LlvmConstant? adjusted = LlvmConstantFolding.ConstantFoldCastOperand(amount.Type.GetScalarSizeInBits() < inner.Operand.Type.GetScalarSizeInBits() ? LlvmOpcode.SExt : LlvmOpcode.Trunc, amount, inner.Operand.Type, DL, Metrics);
                if (adjusted is not null)
                {
                    LlvmValue shifted = Builder.CreateBinary(binary.Opcode, inner.Operand, adjusted, binary.Name, exact: binary.IsExact);
                    return CastIntegerResult(IntegerCastInstruction(shifted, destination, false), "narrow_binop_truncated_shift");
                }
            }
            return NarrowFunnelShift(truncation);
        }
    }
}
