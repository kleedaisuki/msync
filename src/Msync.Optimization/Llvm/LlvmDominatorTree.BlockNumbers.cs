// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/GenericDomTree.h
// updateBlockNumberEpoch / updateBlockNumbers.
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    private uint blockNumberEpoch;

    public void UpdateBlockNumbers(Dictionary<string, ulong>? metrics = null)
    {
        blockNumberEpoch = Function.GetBlockNumberEpoch();
        nodeCapacity = Function.GetMaxBlockNumber() + (IsPostDominator ? 1U : 0U);
        // Nodes are keyed by block identity rather than numbered vector slots;
        // the source's reindex preserves every existing node and tree edge.
        CountMutation(metrics, "domtree.block_numbers.updated");
        CountMutation(metrics, "domtree.block_numbers.nodes_preserved", (ulong)nodes.Count + (IsPostDominator ? 1UL : 0UL));
    }
}
