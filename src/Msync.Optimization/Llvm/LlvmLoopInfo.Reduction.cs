// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericLoopInfoImpl.h analyzeImpl irreducible-loop reduction.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoopInfo
{
    private sealed partial class Discovery
    {
        private void ReduceReentries()
        {
            foreach (int header in reentries) info[header].Pos = IsReentered;
            dominators.UpdateDFSNumbers();
            int[] mark = new int[info.Length], firstChild = new int[info.Length], nextSibling = new int[info.Length];
            Array.Fill(mark, NoBlock); Array.Fill(firstChild, NoBlock); Array.Fill(nextSibling, NoBlock);
            var blocks = new LlvmBasicBlock?[info.Length];
            foreach (LlvmBasicBlock block in postorder)
            {
                int number = (int)block.Number;
                blocks[number] = block;
                int parent = info[number].Header;
                if (parent == NoBlock) continue;
                nextSibling[number] = firstChild[parent]; firstChild[parent] = number;
            }
            var pending = new List<LlvmBasicBlock>();
            foreach (LlvmBasicBlock headerBlock in postorder)
            {
                int header = (int)headerBlock.Number;
                if (info[header].Pos != IsReentered) continue;
                Count("reduction.headers"); mark[header] = header; pending.Clear();
                void Enqueue(LlvmBasicBlock predecessor)
                {
                    int number = (int)predecessor.Number;
                    for (int ancestor = number; ancestor != NoBlock; ancestor = info[ancestor].Header)
                    {
                        Count("reduction.collapse_steps");
                        if (info[ancestor].Header != header) continue;
                        number = ancestor; predecessor = blocks[ancestor]!; break;
                    }
                    if (mark[number] == header) { Count("reduction.already_marked"); return; }
                    mark[number] = header; pending.Add(predecessor); Count("reduction.enqueued");
                }
                LlvmDominatorTreeNode? domNode = dominators.GetNode(headerBlock);
                Debug.Assert(domNode is not null);
                bool hasBackedge = false;
                foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(headerBlock))
                {
                    Count("reduction.header_predecessors");
                    LlvmDominatorTreeNode? predecessorNode = dominators.GetNode(predecessor);
                    if (predecessorNode is null) { Count("reduction.unreachable_predecessors"); continue; }
                    Count("reduction.dominator_comparisons");
                    if (!dominators.Dominates(domNode, predecessorNode)) continue;
                    hasBackedge = true; Count("reduction.natural_backedges"); Enqueue(predecessor);
                }
                for (int index = 0; index < pending.Count; ++index)
                {
                    foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(pending[index]))
                    {
                        Count("reduction.reverse_edges");
                        if (blocks[(int)predecessor.Number] is not null) Enqueue(predecessor);
                        else Count("reduction.unreachable_predecessors");
                    }
                }
                info[header].Pos = hasBackedge ? IsHeader : OffPath;
                if (!hasBackedge) Count("reduction.headers_removed");
                int enclosing = info[header].Header, kept = NoBlock;
                for (int block = firstChild[header], next; block != NoBlock; block = next)
                {
                    next = nextSibling[block];
                    if (mark[block] == header)
                    {
                        nextSibling[block] = kept; kept = block; Count("reduction.members_kept");
                    }
                    else
                    {
                        info[block].Header = enclosing; Count("reduction.members_reparented");
                        if (enclosing == NoBlock) continue;
                        nextSibling[block] = firstChild[enclosing]; firstChild[enclosing] = block;
                    }
                }
                firstChild[header] = kept;
            }
        }
    }
}
