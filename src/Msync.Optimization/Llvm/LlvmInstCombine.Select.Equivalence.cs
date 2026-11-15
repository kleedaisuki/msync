// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSelect.cpp replaceInInstruction/foldSelectValueEquivalence.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool ReplaceInInstruction(LlvmValue value, LlvmValue oldValue, LlvmValue newValue, uint depth = 0)
        {
            if (depth == 2) return false;
            System.Diagnostics.Debug.Assert(oldValue is not LlvmConstant);
            if (value is not LlvmInstruction instruction || !instruction.HasOneUse ||
                !LlvmValueTracking.IsSafeToSpeculativelyExecuteWithVariableReplaced(instruction, metrics: Metrics)) return false;
            bool changed = false;
            foreach (LlvmUse use in instruction.OperandUses)
            {
                if (ReferenceEquals(use.Value, oldValue)) { ReplaceUse(use, newValue); Worklist.Add(instruction); changed = true; }
                else changed |= ReplaceInInstruction(use.Value!, oldValue, newValue, depth + 1);
            }
            return changed;
        }
        private LlvmInstruction? FoldSelectValueEquivalence(LlvmSelectInstruction select, LlvmCmpInstruction compare)
        {
            LlvmValue whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            bool swapped = false;
            if (LlvmCmpInstAnalysis.IsEquivalence(compare, invert: true))
            { (whenTrue, whenFalse) = (whenFalse, whenTrue); swapped = true; }
            else if (!LlvmCmpInstAnalysis.IsEquivalence(compare)) return null;
            LlvmValue left = compare.Left, right = compare.Right;
            LlvmInstruction? ReplaceOldOpWithNewOp(LlvmValue oldValue, LlvmValue newValue)
            {
                if (ReferenceEquals(whenTrue, oldValue) && (oldValue is LlvmConstant || newValue is not LlvmConstant)) return null;
                LlvmValue? simplified = LlvmInstructionSimplify.SimplifyWithOpReplaced(whenTrue, oldValue, newValue, SQ, allowRefinement: true);
                if (simplified is not null)
                {
                    if (simplified is LlvmConstant && simplified is not LlvmConstantExpr && LlvmValueTracking.IsGuaranteedNotToBeUndef(simplified, DT, select, Metrics, assumptions: AC))
                        return ReplaceOperand(select, swapped ? 2 : 1, simplified);
                    if ((newValue is LlvmConstant && newValue is not LlvmConstantExpr) || (whenTrue is LlvmInstruction trueInstruction && trueInstruction.Operands.Any(operand => ReferenceEquals(operand, simplified))))
                    {
                        if (LlvmValueTracking.IsGuaranteedNotToBeUndef(newValue, DT, select, Metrics, assumptions: AC))
                            return ReplaceOperand(select, swapped ? 2 : 1, simplified);
                        return null;
                    }
                }
                if (ReferenceEquals(oldValue, left) && newValue is LlvmConstant && newValue is not LlvmConstantExpr && oldValue is not LlvmConstant &&
                    LlvmValueTracking.IsGuaranteedNotToBeUndef(newValue, DT, select, Metrics, assumptions: AC) &&
                    ReplaceInInstruction(whenTrue, oldValue, newValue)) return select;
                return null;
            }
            bool replaceLeft = LlvmCmpInstAnalysis.CanReplacePointersIfEqual(left, right, DL, Metrics);
            if (replaceLeft && ReplaceOldOpWithNewOp(left, right) is { } leftResult) return leftResult;
            bool replaceRight = LlvmCmpInstAnalysis.CanReplacePointersIfEqual(right, left, DL, Metrics);
            if (replaceRight && ReplaceOldOpWithNewOp(right, left) is { } rightResult) return rightResult;
            if (whenFalse is not LlvmInstruction) return null;
            List<LlvmInstruction> dropFlags = [];
            if ((replaceLeft && ReferenceEquals(LlvmInstructionSimplify.SimplifyWithOpReplaced(whenFalse, left, right, SQ, false, dropFlags), whenTrue)) ||
                (replaceRight && ReferenceEquals(LlvmInstructionSimplify.SimplifyWithOpReplaced(whenFalse, right, left, SQ, false, dropFlags), whenTrue)))
            {
                foreach (LlvmInstruction instruction in dropFlags) { instruction.DropPoisonGeneratingAnnotations(); Worklist.Add(instruction); }
                Count("select.equivalence.drop_flags_rewrites");
                return ReplaceInstUsesWith(select, whenFalse);
            }
            if (whenFalse.Type.IsIntegerTy(1) && whenFalse is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true } trunc &&
                ReferenceEquals(trunc.Operand, left) && right is LlvmConstant constant && constant is not LlvmConstantExpr &&
                ReferenceEquals(LlvmConstantFolding.ConstantFoldCompareInstOperands(LlvmCmpPredicate.ICmpNe, constant,
                    LlvmConstant.GetNullValue(left.Type), DL, metrics: Metrics), whenTrue))
                return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpNe, left, LlvmConstant.GetNullValue(left.Type));
            return null;
        }
    }
}
