// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: GenericLoopInfo.h isLoopExiting.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoop
{
    public bool IsLoopExiting(LlvmBasicBlock block)
    {
        Debug.Assert(!IsInvalid && Contains(block));
        owner.RecordMutation("loop_info.query.exiting");
        foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
            if (!Contains(successor)) return true;
        return false;
    }
}
