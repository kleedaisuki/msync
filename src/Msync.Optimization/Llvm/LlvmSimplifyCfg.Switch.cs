// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFGOpt::simplifySwitch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool RetainSwitch(string reason)
        {
            if (Metrics is not null) Count("switch.retained." + reason);
            return false;
        }

        private bool SimplifySwitch(LlvmSwitchInstruction selection, LlvmTransformBuilder builder)
        {
            Count("switch.queries");
            LlvmBasicBlock block = selection.Parent!;
            if (IsValueEqualityComparison(selection) is not null)
            {
                if (LlvmBasicBlockUtils.GetSinglePredecessor(block) is { } predecessor &&
                    SimplifyEqualityComparisonWithOnlyPredecessor(selection, predecessor, builder)) return RequestResimplify();
                if (selection.Condition is LlvmSelectInstruction select && SimplifySwitchOnSelect(selection, select)) return RequestResimplify();
                if (selection == block.FirstInstruction && FoldValueComparisonIntoPredecessors(selection, builder)) return RequestResimplify();
            }
            if (Options.ConvertSwitchRangeToICmp && TurnSwitchRangeIntoICmp(selection, builder)) return RequestResimplify();
            if (EliminateDeadSwitchCases(selection)) return RequestResimplify();
            if (SimplifySwitchOfCmpIntrinsic(selection, builder)) return RequestResimplify();
            if (TrySwitchToSelect(selection, builder)) return RequestResimplify();
            if (Options.ForwardSwitchCondToPhi && ForwardSwitchConditionToPHI(selection)) return RequestResimplify();
            if ((Options.ConvertSwitchToArithmetic || Options.ConvertSwitchToLookupTable) &&
                SimplifySwitchLookup(selection, builder, Options.ConvertSwitchToLookupTable)) return RequestResimplify();
            if (SimplifySwitchOfPowersOfTwo(selection, builder)) return RequestResimplify();
            if (ReduceSwitchRange(selection, builder)) return RequestResimplify();
            if (Tuning.HoistCommon && HoistCommonCodeFromSuccessors(selection, !Options.HoistCommonInsts)) return RequestResimplify();
            if (SimplifyDuplicateSwitchArms(selection)) return RequestResimplify();
            if (SimplifySwitchWhenUMin(selection)) return RequestResimplify();
            if (SimplifySwitchDefaultBranch(selection)) return RequestResimplify();
            Count("switch.unchanged");
            return false;
        }
    }
}
