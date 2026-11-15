// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 3160-3247;
// PatternMatch.h SelectLike_match scalar branch.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmValue? CreateLogicFromTable(byte table, LlvmValue left, LlvmValue right,
            LlvmTransformBuilder builder, bool hasOneUse) => table switch
        {
            0 => LlvmConstantInt.GetFalse(left.Context),
            1 => hasOneUse ? builder.CreateNot(builder.CreateOr(left, right)) : null,
            2 => hasOneUse ? builder.CreateAnd(builder.CreateNot(left), right) : null,
            3 => builder.CreateNot(left),
            4 => hasOneUse ? builder.CreateAnd(left, builder.CreateNot(right)) : null,
            5 => builder.CreateNot(right),
            6 => builder.CreateXor(left, right),
            7 => hasOneUse ? builder.CreateNot(builder.CreateAnd(left, right)) : null,
            8 => builder.CreateAnd(left, right),
            9 => hasOneUse ? builder.CreateNot(builder.CreateXor(left, right)) : null,
            10 => right,
            11 => hasOneUse ? builder.CreateOr(builder.CreateNot(left), right) : null,
            12 => left,
            13 => hasOneUse ? builder.CreateOr(left, builder.CreateNot(right)) : null,
            14 => builder.CreateOr(left, right),
            15 => LlvmConstantInt.GetTrue(left.Context),
            _ => throw new InvalidOperationException("Invalid four-entry truth table.")
        };

        private static bool IccSelectLike(LlvmValue value, out LlvmValue condition,
            out LlvmConstant whenTrue, out LlvmConstant whenFalse)
        {
            if (value is LlvmSelectInstruction { TrueValue: LlvmConstant a, FalseValue: LlvmConstant b } select)
            { condition = select.Condition; whenTrue = a; whenFalse = b; return true; }
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } cast && cast.Operand.Type.IsIntegerTy(1))
            {
                condition = cast.Operand;
                whenTrue = IccConstant(value, cast.Opcode == LlvmOpcode.ZExt ? new LlvmAPInt(value.Type.GetIntegerBitWidth(), 1) : LlvmAPInt.GetAllOnes(value.Type.GetIntegerBitWidth()));
                whenFalse = ICmpZero(value);
                return true;
            }
            condition = null!; whenTrue = whenFalse = null!; return false;
        }

        private LlvmInstruction? FoldICmpBinOpWithConstantViaTruthTable(LlvmICmpInstruction compare, LlvmBinaryOperator binary, LlvmAPInt constant)
        {
            if (!IccSelectLike(binary.Left, out var left, out var a, out var b)
                || !IccSelectLike(binary.Right, out var right, out var c, out var d)
                || compare.Type != left.Type || compare.Type != right.Type) return null;
            byte table = 0;
            for (int index = 0; index < 4; ++index)
            {
                LlvmConstant lhs = (index & 2) != 0 ? a : b, rhs = (index & 1) != 0 ? c : d;
                if (LlvmConstantFolding.ConstantFoldBinaryOpOperands(binary.Opcode, lhs, rhs, DL, Metrics) is not LlvmConstantInt folded) return null;
                if (LlvmConstantFolding.ConstantFoldCompareInstruction(compare.Predicate, folded, IccConstant(binary, constant)) is not LlvmConstantInt boolean) return null;
                if (!boolean.IsZero) table |= (byte)(1 << index);
            }
            Count("icmp.constant.truth-table");
            return CreateLogicFromTable(table, left, right, Builder, binary.HasOneUse) is { } condition
                ? ReplaceInstUsesWith(compare, condition) : null;
        }
    }
}
