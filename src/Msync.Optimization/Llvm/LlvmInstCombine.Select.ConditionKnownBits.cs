// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSelect.cpp hasAffectedValue and visitSelectInst conditional KnownBits.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private const uint SelectAffectedMaxDepth = 6;

        private bool SelectHasAffectedValue(LlvmValue value, HashSet<LlvmValue> affected, uint depth)
        {
            Count("select.condition_known_bits.affected_visits");
            if (depth == SelectAffectedMaxDepth) return false;
            // Directly affected arms have cheaper dedicated folds earlier in visitSelectInst.
            if (depth != 0 && affected.Contains(value)) return true;
            if (value is not LlvmInstruction instruction) return false;
            if (instruction is LlvmPhiInstruction)
            {
                if (depth == SelectAffectedMaxDepth - 1) return false;
                depth = SelectAffectedMaxDepth - 2;
            }
            for (int index = 0; index < instruction.OperandCount; ++index)
            {
                LlvmValue operand = instruction.GetOperand(index)!;
                if (operand.Type.IsIntOrIntVectorTy() && SelectHasAffectedValue(operand, affected, depth + 1))
                    return true;
            }
            return false;
        }

        private LlvmInstruction? FoldSelectConditionKnownBits(LlvmSelectInstruction select)
        {
            // Upstream limits this fold to scalars because KnownBits can cross vector lanes.
            if (!select.Type.IsIntegerTy() || select.TrueValue is LlvmConstant && select.FalseValue is LlvmConstant)
                return null;
            Count("select.condition_known_bits.queries");
            LlvmCondContext condition = new(select.Condition);
            LlvmValueTracking.FindValuesAffectedByCondition(select.Condition, false, value => { condition.AffectedValues.Add(value); });
            if (condition.AffectedValues.Count == 0)
            {
                Count("select.condition_known_bits.no_affected_values");
                return null;
            }
            LlvmInstructionSimplify.BinaryQuery query = Query(select).WithCondContext(condition);
            for (int index = 1; index <= 2; ++index)
            {
                condition.Invert = index == 2;
                LlvmValue arm = select.GetOperand(index)!;
                if (arm is LlvmConstant || !SelectHasAffectedValue(arm, condition.AffectedValues, 0)) continue;
                Count("select.condition_known_bits.arm_queries");
                LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(arm, context: query.Context, tree: query.Tree,
                    metrics: query.Metrics, useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions,
                    allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: condition, layout: query.Layout);
                if (!known.IsConstant) continue;
                Count(index == 1 ? "select.condition_known_bits.true_rewrites" : "select.condition_known_bits.false_rewrites");
                return ReplaceOperand(select, index, LlvmConstantInt.Get(select.Type, known.GetConstant()));
            }
            Count("select.condition_known_bits.no_constant_arm");
            return null;
        }
    }
}
