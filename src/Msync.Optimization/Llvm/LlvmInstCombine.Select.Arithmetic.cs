// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// getSelectFoldableOperands, foldAddSubSelect, foldSelectNegNot, foldSetClearBits.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static uint GetSelectFoldableOperands(LlvmBinaryOperator instruction) => instruction.Opcode switch
        {
            LlvmOpcode.Add or LlvmOpcode.FAdd or LlvmOpcode.Mul or LlvmOpcode.FMul or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor => 3,
            LlvmOpcode.Sub or LlvmOpcode.FSub or LlvmOpcode.FDiv or LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr => 1,
            _ => 0
        };
        private LlvmInstruction? FoldAddSubSelect(LlvmSelectInstruction select)
        {
            if (select.TrueValue is not LlvmBinaryOperator whenTrue || select.FalseValue is not LlvmBinaryOperator whenFalse ||
                !whenTrue.HasOneUse || !whenFalse.HasOneUse) return null;
            LlvmBinaryOperator add, sub;
            if ((whenTrue.Opcode == LlvmOpcode.Sub && whenFalse.Opcode == LlvmOpcode.Add) ||
                (whenTrue.Opcode == LlvmOpcode.FSub && whenFalse.Opcode == LlvmOpcode.FAdd))
            { add = whenFalse; sub = whenTrue; }
            else if ((whenFalse.Opcode == LlvmOpcode.Sub && whenTrue.Opcode == LlvmOpcode.Add) ||
                (whenFalse.Opcode == LlvmOpcode.FSub && whenTrue.Opcode == LlvmOpcode.FAdd))
            { add = whenTrue; sub = whenFalse; }
            else return null;
            LlvmValue? other = ReferenceEquals(sub.Left, add.Left) ? add.Right : ReferenceEquals(sub.Left, add.Right) ? add.Left : null;
            if (other is null) return null;
            bool floating = select.Type.IsFPOrFPVectorTy();
            LlvmValue negative = floating ? Builder.CreateFNeg(sub.Right) : Builder.CreateNeg(sub.Right);
            LlvmFastMathFlags flags = add.FastMathFlags & sub.FastMathFlags;
            if (floating && negative is ILlvmFastMathInstruction negativeInstruction) negativeInstruction.FastMathFlags = flags;
            LlvmValue trueValue = other, falseValue = negative;
            if (!ReferenceEquals(add, whenTrue)) (trueValue, falseValue) = (falseValue, trueValue);
            LlvmValue newSelect = Builder.CreateSelect(select.Condition, trueValue, falseValue, select.Name + ".p");
            Count("select.add_sub.rewrites");
            return new LlvmBinaryOperator(floating ? LlvmOpcode.FAdd : LlvmOpcode.Add, sub.Left, newSelect)
            { FastMathFlags = floating ? flags : LlvmFastMathFlags.None };
        }
        private LlvmInstruction? FoldSelectNegNot(LlvmSelectInstruction select)
        {
            if (!select.Type.IsIntegerTy() || select.Type.IsIntegerTy(1)) return null;
            bool Match(LlvmValue negative, LlvmValue not, out LlvmValue operand)
            {
                operand = negative;
                return negative.HasOneUse && not.HasOneUse && SelectMatchNeg(negative, out operand) &&
                    SelectMatchNot(not, out LlvmValue notOperand) && ReferenceEquals(operand, notOperand);
            }
            if (Match(select.TrueValue, select.FalseValue, out LlvmValue value))
            {
                LlvmValue inverted = Builder.CreateNot(select.Condition, "not." + select.Condition.Name);
                return new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateSExt(inverted, select.Type), value);
            }
            if (Match(select.FalseValue, select.TrueValue, out value))
                return new LlvmBinaryOperator(LlvmOpcode.Sub, Builder.CreateSExt(select.Condition, select.Type), value);
            return null;
        }
        private LlvmInstruction? FoldSetClearBits(LlvmSelectInstruction select)
        {
            bool Match(LlvmValue andValue, LlvmValue orValue, out LlvmConstantInt constant)
            {
                constant = null!;
                if (andValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and || and.Right is not LlvmConstantInt notC ||
                    orValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or || !or.HasOneUse ||
                    !ReferenceEquals(and.Left, or.Left) || or.Right is not LlvmConstantInt c || notC.Value != ~c.Value) return false;
                constant = c; return true;
            }
            LlvmValue zero = LlvmConstant.GetNullValue(select.Type);
            if (Match(select.TrueValue, select.FalseValue, out LlvmConstantInt constant))
                return new LlvmBinaryOperator(LlvmOpcode.Or, select.TrueValue, Builder.CreateSelect(select.Condition, zero, constant, "masksel"));
            if (Match(select.FalseValue, select.TrueValue, out constant))
                return new LlvmBinaryOperator(LlvmOpcode.Or, select.FalseValue, Builder.CreateSelect(select.Condition, constant, zero, "masksel"));
            return null;
        }
        private LlvmInstruction? FoldSelectExtConst(LlvmSelectInstruction select)
        {
            LlvmConstant? constant = select.TrueValue as LlvmConstant ?? select.FalseValue as LlvmConstant;
            if (constant is null) return null;
            LlvmInstruction? instruction = select.TrueValue as LlvmInstruction ?? select.FalseValue as LlvmInstruction;
            if (instruction is not LlvmCastInstruction cast || cast.Opcode is not (LlvmOpcode.ZExt or LlvmOpcode.SExt)) return null;
            LlvmType smallType = cast.Operand.Type;
            if (!smallType.IsIntegerTy(1) &&
                (select.Condition is not (LlvmCmpInstruction or LlvmCastInstruction { Opcode: LlvmOpcode.Trunc }) ||
                !ReferenceEquals(((LlvmInstruction)select.Condition).GetOperand(0)!.Type, smallType))) return null;
            LlvmConstant? truncated = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.Trunc, constant, smallType, DL, Metrics);
            if (truncated is null || !ReferenceEquals(LlvmConstantFolding.ConstantFoldCastOperand(cast.Opcode, truncated, select.Type, DL, Metrics), constant) || !cast.HasOneUse)
                return null;
            LlvmValue whenTrue = cast.Operand, whenFalse = truncated;
            if (ReferenceEquals(cast, select.FalseValue)) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            LlvmValue narrowSelect = Builder.CreateSelect(select.Condition, whenTrue, whenFalse, "narrow");
            Count("select.ext_constant.rewrites");
            return new LlvmCastInstruction(cast.Opcode, narrowSelect, select.Type);
        }
    }
}
