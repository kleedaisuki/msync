// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp adjustKnownFPClassForSelectArm.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmKnownFPClasses
{
    public static LlvmKnownFPClass AdjustForSelectArm(LlvmKnownFPClass known, LlvmValue arm,
        LlvmValue condition, bool invert, LlvmInstruction? context, LlvmDominatorTree? tree,
        Dictionary<string, ulong>? metrics, int depth, LlvmAssumptionCache? assumptions,
        bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        var conditional = new LlvmKnownFPClass();
        FromCondition(arm, condition, !invert, ref conditional, metrics, depth + 1);
        conditional = conditional.UnionWith(known);
        return !conditional.IsUnknown && IsGuaranteedNotToBeUndef(arm, context, tree, metrics, depth + 1,
            assumptions, allowEphemerals, conditions) ? conditional : known;
    }
}
