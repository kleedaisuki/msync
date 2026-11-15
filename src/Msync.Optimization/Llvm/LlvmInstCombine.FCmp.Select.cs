// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineCompares.cpp foldCmpSelectOfConstants (shared integer/FP helper).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldCmpSelectOfConstants(LlvmCmpInstruction comparison)
        {
            if (comparison.Left is not LlvmSelectInstruction { TrueValue: LlvmConstant trueLeft, FalseValue: LlvmConstant falseLeft } left ||
                comparison.Right is not LlvmSelectInstruction { TrueValue: LlvmConstant trueRight, FalseValue: LlvmConstant falseRight } right ||
                comparison.Type != left.Condition.Type || comparison.Type != right.Condition.Type) return null;
            Count("compare.select_constant_table.queries");
            LlvmConstant?[] results = [
                LlvmConstantFolding.ConstantFoldCompareInstOperands(comparison.Predicate, falseLeft, falseRight, DL, F, Metrics),
                LlvmConstantFolding.ConstantFoldCompareInstOperands(comparison.Predicate, falseLeft, trueRight, DL, F, Metrics),
                LlvmConstantFolding.ConstantFoldCompareInstOperands(comparison.Predicate, trueLeft, falseRight, DL, F, Metrics),
                LlvmConstantFolding.ConstantFoldCompareInstOperands(comparison.Predicate, trueLeft, trueRight, DL, F, Metrics)
            ];
            byte table = 0;
            for (int i = 0; i < results.Length; ++i)
            {
                if (results[i] is not LlvmConstantInt result || !result.IsZero && !result.Value.IsAllOnes) return null;
                if (!result.IsZero) table |= (byte)(1 << i);
            }
            LlvmValue? folded = CreateLogicFromTable(table, left.Condition, right.Condition, Builder, left.HasOneUse && right.HasOneUse);
            if (folded is null) return null;
            Count("compare.rewrite.select_constant_table");
            return ReplaceInstUsesWith(comparison, folded);
        }
    }
}
