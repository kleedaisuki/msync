// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// checkForNegativeOperand/foldNoWrapAdd.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? CheckForNegativeOperand(LlvmBinaryOperator instruction)
        {
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (!left.HasOneUse && !right.HasOneUse) return null;
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsOne: true } })
                (left, right) = (right, left);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsOne: true } } add)
            {
                LlvmValue x = add.Left;
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt })
                    (x, right) = (right, x);
                if (x is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt c1 } xor &&
                    xor.Left is LlvmBinaryOperator { Right: LlvmConstantInt c2 } masked)
                {
                    if (masked.Opcode == LlvmOpcode.Or && c2.Value == ~c1.Value)
                        return Builder.CreateSub(right, Builder.CreateAnd(masked.Left, c1));
                    if (masked.Opcode == LlvmOpcode.And && c1.Value == c2.Value)
                        return Builder.CreateSub(right, Builder.CreateOr(masked.Left, LlvmConstantInt.Get(c1.Context, ~c1.Value)));
                }
            }
            left = instruction.Left; right = instruction.Right;
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt })
                (left, right) = (right, left);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt outer } xorMask &&
                outer.Value.CountTrailingZeros() == 0 &&
                xorMask.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt inner } and &&
                outer.Value == inner.Value + new LlvmAPInt(inner.Value.BitWidth, 1))
                return Builder.CreateSub(right, Builder.CreateOr(and.Left, LlvmConstantInt.Get(inner.Context, ~inner.Value)));
            return null;
        }
        private static bool MatchAddSubAddLike(LlvmValue value, bool signed, out LlvmValue operand, out LlvmConstant constant)
        {
            operand = null!; constant = null!;
            if (value is not LlvmBinaryOperator { Right: LlvmConstant rhs } binary) return false;
            if (binary.Opcode != LlvmOpcode.Or || !binary.IsDisjoint)
                if (binary.Opcode != LlvmOpcode.Add || !(signed ? binary.HasNoSignedWrap : binary.HasNoUnsignedWrap)) return false;
            operand = binary.Left; constant = rhs;
            return true;
        }
        private LlvmInstruction? FoldNoWrapAdd(LlvmBinaryOperator add)
        {
            if (add.Right is not LlvmConstant outerConstant || add.Left is not LlvmCastInstruction cast) return null;
            LlvmType type = add.Type;
            if (cast.Opcode == LlvmOpcode.ZExt && outerConstant is LlvmConstantInt c1 &&
                MatchAddSubAddLike(cast.Operand, false, out var narrowValue, out var narrowConstant) &&
                narrowConstant is LlvmConstantInt c2 && c1.Value.IsNegative &&
                c1.Value.Sge(-c2.Value.SExt(c1.Value.BitWidth)))
            {
                var value = c2.Value + c1.Value.Trunc(c2.Value.BitWidth);
                if (value.IsZero) return new LlvmCastInstruction(LlvmOpcode.ZExt, narrowValue, type);
                if (cast.HasOneUse)
                    return new LlvmCastInstruction(LlvmOpcode.ZExt,
                        Builder.CreateAdd(narrowValue, LlvmConstantInt.Get(narrowValue.Context, value), nuw: true), type);
            }
            if (!cast.HasOneUse) return null;
            if ((cast.Opcode == LlvmOpcode.SExt || cast.Opcode == LlvmOpcode.ZExt && cast.IsNonNegative) &&
                MatchAddSubAddLike(cast.Operand, true, out var signedValue, out var signedConstant))
            {
                LlvmValue combined = Builder.CreateAdd(Builder.CreateSExt(signedConstant, type), outerConstant);
                return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateSExt(signedValue, type), combined);
            }
            if (cast.Opcode == LlvmOpcode.ZExt &&
                MatchAddSubAddLike(cast.Operand, false, out var unsignedValue, out var unsignedConstant))
            {
                LlvmValue combined = Builder.CreateAdd(Builder.CreateZExt(unsignedConstant, type), outerConstant);
                return new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateZExt(unsignedValue, type), combined);
            }
            return null;
        }
    }
}
