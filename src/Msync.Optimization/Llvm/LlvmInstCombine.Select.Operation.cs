// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp 1823–1921, 1971–2005, 2393–2407.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyOperationIntoSelectOperand(LlvmInstruction instruction, LlvmSelectInstruction select, bool isTrueArm)
        {
            List<LlvmValue> operands = [];
            foreach (LlvmValue? operand in instruction.Operands)
            {
                LlvmValue value;
                if (ReferenceEquals(operand, select)) value = isTrueArm ? select.TrueValue : select.FalseValue;
                else if (select.Condition is LlvmICmpInstruction compare &&
                    compare.Predicate == (isTrueArm ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe) &&
                    ReferenceEquals(compare.Left, operand) && LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(compare.Right))
                    value = compare.Right;
                else if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } cast && ReferenceEquals(cast.Operand, select.Condition))
                    value = LlvmConstantInt.Get((LlvmIntegerType)operand.Type, isTrueArm ? 1UL : 0UL);
                else value = operand!;
                operands.Add(value);
            }
            // Source overload takes only DataLayout here, not this pass's SQ.
            return LlvmInstructionSimplify.SimplifyInstructionWithOperands(instruction, operands, new(DL, null, Metrics));
        }
        private LlvmValue FoldOperationIntoSelectOperand(LlvmInstruction instruction, LlvmSelectInstruction select, LlvmValue replacement)
        {
            LlvmInstruction clone = instruction.Clone();
            clone.ReplaceUsesOfWith(select, replacement);
            clone.DropUBImplyingAttrsAndMetadata();
            return InsertNewInstBefore(clone, instruction);
        }
        private LlvmInstruction? FoldOpIntoSelect(LlvmInstruction instruction, LlvmSelectInstruction select,
            bool foldWithMultiUse = false, bool simplifyBothArms = false)
        {
            Count("select.fold_operation.queries");
            if (!select.HasOneUser && !foldWithMultiUse) return null;
            if (select.Type.IsIntegerTy(1)) return null;
            if (instruction is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmIntrinsic.IsMinMax(descriptor.Kind))
                foreach (LlvmValue? operand in instruction.Operands)
                    if (operand is LlvmPhiInstruction phi)
                        foreach (LlvmValue? incoming in phi.Operands)
                            if (ReferenceEquals(incoming, instruction)) return null;
            if (select.Condition is LlvmFCmpInstruction compare && compare.HasOneUse &&
                ((ReferenceEquals(select.TrueValue, compare.Left) && ReferenceEquals(select.FalseValue, compare.Right)) ||
                (ReferenceEquals(select.FalseValue, compare.Left) && ReferenceEquals(select.TrueValue, compare.Right))) && !compare.IsCommutative)
                return null;
            LlvmValue? whenTrue = SimplifyOperationIntoSelectOperand(instruction, select, true);
            LlvmValue? whenFalse = SimplifyOperationIntoSelectOperand(instruction, select, false);
            if (whenTrue is null && whenFalse is null) return null;
            if (simplifyBothArms && (whenTrue is null || whenFalse is null)) return null;
            whenTrue ??= FoldOperationIntoSelectOperand(instruction, select, select.TrueValue);
            whenFalse ??= FoldOperationIntoSelectOperand(instruction, select, select.FalseValue);
            Count("select.fold_operation.rewrites");
            return new LlvmSelectInstruction(select.Condition, whenTrue, whenFalse) { Source = select.Source };
        }
        private LlvmInstruction? FoldBinOpSelectBinOp(LlvmBinaryOperator instruction)
        {
            System.Diagnostics.Debug.Assert(instruction.IsAssociative);
            if (instruction.Left is not LlvmSelectInstruction select || instruction.Right is not LlvmConstant constant || constant is LlvmConstantExpr ||
                !instruction.HasOneUse || !select.HasOneUse) return null;
            LlvmValue input; LlvmValue? whenTrue, whenFalse;
            if (select.TrueValue is LlvmBinaryOperator trueOp && trueOp.HasOneUse && trueOp.Opcode == instruction.Opcode &&
                ReferenceEquals(trueOp.Left, select.FalseValue) && trueOp.Right is LlvmConstant other && other is not LlvmConstantExpr)
            {
                whenTrue = LlvmConstantFolding.ConstantFoldBinaryOpOperands(instruction.Opcode, constant, other, DL, Metrics);
                whenFalse = constant; input = select.FalseValue;
            }
            else if (select.FalseValue is LlvmBinaryOperator falseOp && falseOp.HasOneUse && falseOp.Opcode == instruction.Opcode &&
                ReferenceEquals(falseOp.Left, select.TrueValue) && falseOp.Right is LlvmConstant otherFalse && otherFalse is not LlvmConstantExpr)
            {
                whenTrue = constant;
                whenFalse = LlvmConstantFolding.ConstantFoldBinaryOpOperands(instruction.Opcode, constant, otherFalse, DL, Metrics);
                input = select.TrueValue;
            }
            else return null;
            if (whenTrue is null || whenFalse is null) return null;
            LlvmValue newSelect = Builder.CreateSelect(select.Condition, whenTrue, whenFalse);
            Count("select.binop_select_binop.rewrites");
            return new LlvmBinaryOperator(instruction.Opcode, newSelect, input);
        }
        private LlvmInstruction? FoldBinOpIntoSelectOrPhi(LlvmBinaryOperator instruction)
        {
            LlvmInstruction? TryFoldOperand(int index, bool otherIsConstant) => instruction.GetOperand(index) switch
            {
                LlvmSelectInstruction select => FoldOpIntoSelect(instruction, select, false, !otherIsConstant),
                LlvmPhiInstruction phi => FoldOpIntoPhi(instruction, phi),
                _ => null
            };
            return TryFoldOperand(0, instruction.Right is LlvmConstant) ?? TryFoldOperand(1, instruction.Left is LlvmConstant);
        }
        private LlvmValue? SimplifyNestedSelectsUsingImpliedCond(LlvmSelectInstruction select, LlvmValue condition, bool conditionIsTrue)
        {
            System.Diagnostics.Debug.Assert(ReferenceEquals(condition.Type, select.Condition.Type));
            bool? implied = LlvmInstructionSimplify.IsImpliedCondition(condition, select.Condition, new(DL, null, Metrics), conditionIsTrue);
            return implied.HasValue ? implied.Value ? select.TrueValue : select.FalseValue : null;
        }
        private LlvmInstruction? FoldAndOrOfSelectUsingImpliedCond(LlvmValue operand, LlvmSelectInstruction select, bool isAnd)
        {
            System.Diagnostics.Debug.Assert(operand.Type.IsIntegerTy(1));
            if (!ReferenceEquals(operand.Type, select.Condition.Type)) return null;
            LlvmValue? value = SimplifyNestedSelectsUsingImpliedCond(select, operand, isAnd);
            if (value is null) return null;
            return new LlvmSelectInstruction(operand, isAnd ? value : LlvmConstantInt.Get((LlvmIntegerType)operand.Type, 1),
                isAnd ? LlvmConstantInt.Get((LlvmIntegerType)operand.Type, 0) : value);
        }
    }
}
