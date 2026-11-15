// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp distributeBinOpOverSelect/SimplifySelectsFeedingBinaryOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private void DistributeBinOpOverSelect(LlvmOpcode opcode, LlvmSelectInstruction select,
            LlvmValue other, bool selectIsLeft, LlvmFastMathFlags flags,
            LlvmInstructionSimplify.BinaryQuery query, out LlvmValue? whenTrue, out LlvmValue? whenFalse)
        {
            LlvmValue SimplifySelectWithImpliedCond(LlvmValue value, bool conditionIsTrue)
            {
                if (value is not LlvmSelectInstruction inner || !ReferenceEquals(select.Condition.Type, inner.Condition.Type)) return value;
                return SimplifyNestedSelectsUsingImpliedCond(inner, select.Condition, conditionIsTrue) ?? value;
            }
            LlvmValue otherTrue = SimplifySelectWithImpliedCond(other, true);
            LlvmValue otherFalse = SimplifySelectWithImpliedCond(other, false);
            LlvmValue leftTrue = select.TrueValue, rightTrue = otherTrue;
            LlvmValue leftFalse = select.FalseValue, rightFalse = otherFalse;
            if (!selectIsLeft) { (leftTrue, rightTrue) = (rightTrue, leftTrue); (leftFalse, rightFalse) = (rightFalse, leftFalse); }
            whenTrue = LlvmInstructionSimplify.SimplifyBinOp(opcode, leftTrue, rightTrue, flags, query);
            whenFalse = LlvmInstructionSimplify.SimplifyBinOp(opcode, leftFalse, rightFalse, flags, query);
            if (!other.HasOneUse) return;
            if (whenTrue is not null && whenFalse is null && !ReferenceEquals(otherTrue, other))
                whenFalse = Builder.CreateBinOp(opcode, leftFalse, rightFalse);
            else if (whenFalse is not null && whenTrue is null && !ReferenceEquals(otherFalse, other))
                whenTrue = Builder.CreateBinOp(opcode, leftTrue, rightTrue);
        }

        private LlvmValue? SimplifySelectsFeedingBinaryOp(LlvmBinaryOperator instruction, LlvmValue left, LlvmValue right)
        {
            Count("select.distribute.queries");
            LlvmSelectInstruction? leftSelect = left as LlvmSelectInstruction, rightSelect = right as LlvmSelectInstruction;
            if (leftSelect is null && rightSelect is null) return null;
            LlvmSelectInstruction select = leftSelect ?? rightSelect!;
            LlvmFastMathFlags savedFlags = Builder.FastMathFlags;
            LlvmFastMathFlags flags = instruction.Type.IsFPOrFPVectorTy() ? instruction.FastMathFlags : LlvmFastMathFlags.None;
            if (instruction.Type.IsFPOrFPVectorTy()) Builder.FastMathFlags = flags;
            try
            {
                LlvmValue condition = select.Condition;
                LlvmValue? whenTrue = null, whenFalse = null;
                LlvmValue? FoldAddNegate(LlvmValue trueValue, LlvmValue falseValue, LlvmValue other)
                {
                    if (instruction.Opcode != LlvmOpcode.Add || (whenTrue is null) == (whenFalse is null)) return null;
                    if (whenTrue is not null && SelectMatchNeg(falseValue, out LlvmValue negative))
                        return Builder.CreateSelect(condition, whenTrue, Builder.CreateSub(other, negative), instruction.Name);
                    if (whenFalse is not null && SelectMatchNeg(trueValue, out negative))
                        return Builder.CreateSelect(condition, Builder.CreateSub(other, negative), whenFalse, instruction.Name);
                    return null;
                }
                if (leftSelect is not null && rightSelect is not null && ReferenceEquals(leftSelect.Condition, rightSelect.Condition))
                {
                    condition = leftSelect.Condition;
                    whenTrue = LlvmInstructionSimplify.SimplifyBinOp(instruction.Opcode, leftSelect.TrueValue, rightSelect.TrueValue, flags, Query(instruction));
                    whenFalse = LlvmInstructionSimplify.SimplifyBinOp(instruction.Opcode, leftSelect.FalseValue, rightSelect.FalseValue, flags, Query(instruction));
                    if (left.HasOneUse && right.HasOneUse)
                    {
                        if (whenFalse is not null && whenTrue is null) whenTrue = Builder.CreateBinOp(instruction.Opcode, leftSelect.TrueValue, rightSelect.TrueValue);
                        else if (whenTrue is not null && whenFalse is null) whenFalse = Builder.CreateBinOp(instruction.Opcode, leftSelect.FalseValue, rightSelect.FalseValue);
                    }
                }
                else
                {
                    if (leftSelect is not null && left.HasOneUse)
                    {
                        condition = leftSelect.Condition;
                        DistributeBinOpOverSelect(instruction.Opcode, select, right, true, flags, Query(instruction), out whenTrue, out whenFalse);
                        if (FoldAddNegate(leftSelect.TrueValue, leftSelect.FalseValue, right) is { } folded) return folded;
                    }
                    if (!(whenTrue is not null && whenFalse is not null) && rightSelect is not null && right.HasOneUse)
                    {
                        select = rightSelect; condition = rightSelect.Condition;
                        DistributeBinOpOverSelect(instruction.Opcode, select, left, false, flags, Query(instruction), out whenTrue, out whenFalse);
                        if (FoldAddNegate(rightSelect.TrueValue, rightSelect.FalseValue, left) is { } folded) return folded;
                    }
                }
                if (whenTrue is null || whenFalse is null) return null;
                LlvmValue result = Builder.CreateSelect(condition, whenTrue, whenFalse, instruction.Name);
                result.TakeName(instruction);
                Count("select.distribute.rewrites");
                return result;
            }
            finally { Builder.FastMathFlags = savedFlags; }
        }
    }
}
