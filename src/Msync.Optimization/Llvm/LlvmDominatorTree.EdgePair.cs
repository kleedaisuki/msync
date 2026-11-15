// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Dominators.cpp
// DominatorTree::dominates(const BasicBlockEdge &, const BasicBlockEdge &).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    public bool DominatesEdge(LlvmBasicBlock start, LlvmBasicBlock end,
        LlvmBasicBlock useStart, LlvmBasicBlock useEnd)
    {
        if (ReferenceEquals(start, useStart) && ReferenceEquals(end, useEnd))
        { trace?.Metric("domtree.edge_pair.identical", 1); return true; }
        return DominatesEdge(start, end, useStart);
    }
}
