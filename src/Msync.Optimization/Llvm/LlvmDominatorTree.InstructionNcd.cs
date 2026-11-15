// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Dominators.cpp 309..325.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    public LlvmInstruction FindNearestCommonDominator(LlvmInstruction first, LlvmInstruction second,
        Dictionary<string, ulong>? metrics = null)
    {
        var left = first.Parent!;
        var right = second.Parent!;
        if (ReferenceEquals(left, right)) return first.ComesBefore(second) ? first : second;
        if (!IsReachableFromEntry(right)) return first;
        if (!IsReachableFromEntry(left)) return second;
        var common = FindNearestCommonDominator(left, right, metrics)!;
        if (ReferenceEquals(left, common)) return first;
        if (ReferenceEquals(right, common)) return second;
        return common.GetTerminator();
    }
}
