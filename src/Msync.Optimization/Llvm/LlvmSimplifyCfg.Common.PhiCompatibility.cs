// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// incomingValuesAreCompatible and safeToMergeTerminators.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool IncomingValuesAreCompatible(LlvmBasicBlock block, IReadOnlyList<LlvmBasicBlock> incomingBlocks,
            HashSet<LlvmValue>? equivalent = null)
        {
            Debug.Assert(incomingBlocks.Count == 2);
            foreach (var phi in SinkPhis(block))
            {
                Count("merge.phi_compatibility_checked");
                var first = phi.GetIncomingValue(phi.GetBasicBlockIndex(incomingBlocks[0]));
                var second = phi.GetIncomingValue(phi.GetBasicBlockIndex(incomingBlocks[1]));
                if (first == second || equivalent is not null && equivalent.Contains(first) && equivalent.Contains(second)) continue;
                Count("merge.phi_incompatible"); return false;
            }
            return true;
        }

        private bool SafeToMergeTerminators(LlvmInstruction first, LlvmInstruction second,
            ICollection<LlvmBasicBlock>? failBlocks = null)
        {
            if (first == second) return false;
            HashSet<LlvmBasicBlock> firstSuccessors = new(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < first.SuccessorCount; ++index) firstSuccessors.Add(first.GetSuccessor(index));
            bool failed = false;
            for (int index = 0; index < second.SuccessorCount; ++index)
            {
                var successor = second.GetSuccessor(index);
                if (!firstSuccessors.Contains(successor) || IncomingValuesAreCompatible(successor, [first.Parent!, second.Parent!])) continue;
                failed = true;
                if (failBlocks is null) break;
                if (!failBlocks.Contains(successor)) failBlocks.Add(successor);
            }
            return !failed;
        }
    }
}
