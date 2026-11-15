// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LoopUtils.cpp collectChildrenInLoop / appendLoopsToWorklist;
// Analysis/LoopIterator.h LoopBlocksDFS / LoopBlocksTraversal / LoopBlocksRPO.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private sealed partial class Work
    {
        private List<LlvmBasicBlock> CollectChildrenInLoop(LlvmLoop loop)
        {
            List<LlvmBasicBlock> work = [loop.Header];
            for (int index = 0; index < work.Count; ++index)
                foreach (LlvmDominatorTreeNode child in Dominators.GetNode(work[index])!.Children)
                    if (loop.Contains(child.Block!)) work.Add(child.Block!);
            Count("traversal.dom_blocks", (ulong)work.Count);
            return work;
        }

        private List<LlvmBasicBlock> LoopReversePostOrder()
        {
            HashSet<LlvmBasicBlock> visited = [Loop.Header];
            List<LlvmBasicBlock> postorder = [];
            var pending = new Stack<(LlvmBasicBlock Block, int Successor)>();
            pending.Push((Loop.Header, 0));
            while (pending.Count != 0)
            {
                var (block, successor) = pending.Pop();
                LlvmInstruction terminator = block.GetTerminator();
                if (successor == terminator.SuccessorCount) { postorder.Add(block); continue; }
                pending.Push((block, successor + 1));
                LlvmBasicBlock child = terminator.GetSuccessor(successor);
                if (Loop.Contains(LoopInfo.GetLoopFor(child)) && visited.Add(child)) pending.Push((child, 0));
            }
            postorder.Reverse();
            Count("traversal.rpo_blocks", (ulong)postorder.Count);
            return postorder;
        }

        private bool SinkLoopNest(SinkAndHoistFlags flags)
        {
            List<LlvmLoop> pending = [Loop];
            foreach (LlvmLoop root in Loop.SubLoops.Reverse())
            {
                var preorder = new Stack<LlvmLoop>(); preorder.Push(root);
                do
                {
                    LlvmLoop current = preorder.Pop();
                    foreach (LlvmLoop child in current.SubLoops) preorder.Push(child);
                    pending.Add(current);
                } while (preorder.Count != 0);
            }
            bool changed = false;
            while (pending.Count != 0)
            {
                LlvmLoop current = pending[^1]; pending.RemoveAt(pending.Count - 1);
                changed |= SinkRegion(current, flags, Loop);
            }
            return changed;
        }
    }
}
