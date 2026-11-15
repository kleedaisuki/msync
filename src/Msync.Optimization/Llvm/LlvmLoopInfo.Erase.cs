// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/LoopInfo.cpp
// LoopInfo::erase and UnloopUpdater; Analysis/LoopIterator.h and
// ADT/PostOrderIterator.h streaming loop DFS and cached postorder.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoopInfo
{
    public void Erase(LlvmLoop unloop)
    {
        Debug.Assert(!unloop.IsInvalid);
        RecordMutation("loop_info.erase.queries");
        try
        {
            if (unloop.IsOutermost)
            {
                foreach (LlvmBasicBlock block in unloop.Blocks)
                    if (ReferenceEquals(GetLoopFor(block), unloop)) ChangeLoopFor(block, null);
                RemoveLoop(unloop);
                while (!unloop.IsInnermost) AddTopLevelLoop(unloop.RemoveChildLoop(unloop.Children.Count - 1));
                return;
            }
            UnloopUpdater updater = new(unloop, this);
            updater.UpdateBlockParents();
            updater.RemoveBlocksFromAncestors();
            updater.UpdateSubloopParents();
            unloop.ParentLoop!.RemoveChildLoop(unloop);
        }
        finally { unloop.Destroy(); }
    }
    private sealed class UnloopUpdater(LlvmLoop unloop, LlvmLoopInfo info)
    {
        private readonly Dictionary<LlvmLoop, LlvmLoop?> subloopParents = [];
        private readonly HashSet<LlvmBasicBlock> preorder = [], postorderVisited = [];
        private readonly List<LlvmBasicBlock> postorder = [];
        private bool foundIrreducibleBackedge;
        private struct Frame(LlvmBasicBlock block) { internal LlvmBasicBlock Block = block; internal int Next; }

        internal void UpdateBlockParents()
        {
            if (unloop.BlockCount != 0)
            {
                foreach (LlvmBasicBlock block in Traverse())
                {
                    LlvmLoop? loop = info.GetLoopFor(block), nearest = GetNearestLoop(block, loop);
                    if (!ReferenceEquals(nearest, loop))
                    {
                        Debug.Assert(!ReferenceEquals(nearest, unloop) && (nearest is null || nearest.Contains(unloop)));
                        info.ChangeLoopFor(block, nearest);
                    }
                    else Debug.Assert(foundIrreducibleBackedge || unloop.Contains(loop));
                }
            }
            bool changed = foundIrreducibleBackedge;
            for (int iteration = 0; changed; ++iteration)
            {
                Debug.Assert(iteration < unloop.BlockCount);
                info.RecordMutation("loop_info.erase.irreducible_iterations");
                changed = false;
                foreach (LlvmBasicBlock block in postorder)
                {
                    LlvmLoop? loop = info.GetLoopFor(block), nearest = GetNearestLoop(block, loop);
                    if (ReferenceEquals(nearest, loop)) continue;
                    Debug.Assert(!ReferenceEquals(nearest, unloop) && (nearest is null || nearest.Contains(unloop)));
                    info.ChangeLoopFor(block, nearest);
                    changed = true;
                }
            }
        }
        private IEnumerable<LlvmBasicBlock> Traverse()
        {
            // finishPostorder occurs when advancing the source iterator, after
            // the caller processes the yielded block and changes its loop map.
            List<Frame> pending = [];
            if (InsertEdge(unloop.Header)) pending.Add(new(unloop.Header));
            while (pending.Count != 0)
            {
                int last = pending.Count - 1;
                Frame frame = pending[last];
                LlvmInstruction terminator = frame.Block.GetTerminator();
                if (frame.Next < terminator.SuccessorCount)
                {
                    LlvmBasicBlock successor = terminator.GetSuccessor(frame.Next++);
                    pending[last] = frame;
                    info.RecordMutation("loop_info.erase.dfs_edges");
                    if (InsertEdge(successor)) pending.Add(new(successor));
                    continue;
                }
                yield return frame.Block;
                postorder.Add(frame.Block); postorderVisited.Add(frame.Block);
                pending.RemoveAt(last);
                info.RecordMutation("loop_info.erase.dfs_postorder");
            }
            Debug.Assert(postorder.Count == unloop.BlockCount);
        }
        private bool InsertEdge(LlvmBasicBlock block) => unloop.Contains(info.GetLoopFor(block)) && preorder.Add(block);
        private LlvmLoop? SubloopParent(LlvmLoop loop)
        {
            if (!subloopParents.TryGetValue(loop, out LlvmLoop? parent)) subloopParents.Add(loop, null);
            return parent;
        }
        private LlvmLoop? GetNearestLoop(LlvmBasicBlock block, LlvmLoop? blockLoop)
        {
            LlvmLoop? nearest = blockLoop, subloop = null;
            if (!ReferenceEquals(nearest, unloop) && unloop.Contains(nearest))
            {
                subloop = nearest!;
                while (!ReferenceEquals(subloop.ParentLoop, unloop))
                {
                    subloop = subloop.ParentLoop!;
                    Debug.Assert(subloop is not null);
                }
                subloopParents.TryAdd(subloop, unloop);
                nearest = subloopParents[subloop];
            }
            if (block.GetTerminator().SuccessorCount == 0) { Debug.Assert(subloop is null); nearest = null; }
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
            {
                if (ReferenceEquals(successor, block)) continue;
                LlvmLoop? loop = info.GetLoopFor(successor);
                if (ReferenceEquals(loop, unloop))
                {
                    Debug.Assert(foundIrreducibleBackedge || !postorderVisited.Contains(successor));
                    foundIrreducibleBackedge = true;
                }
                if (!ReferenceEquals(loop, unloop) && unloop.Contains(loop))
                {
                    if (subloop is not null) continue;
                    Debug.Assert(ReferenceEquals(loop!.ParentLoop, unloop));
                    loop = SubloopParent(loop!);
                }
                if (ReferenceEquals(loop, unloop)) continue;
                if (loop is not null && !loop.Contains(unloop)) loop = loop.ParentLoop;
                if (ReferenceEquals(nearest, unloop) || nearest is null || nearest.Contains(loop)) nearest = loop;
            }
            if (subloop is null) return nearest;
            subloopParents[subloop] = nearest;
            return blockLoop;
        }
        internal void RemoveBlocksFromAncestors()
        {
            foreach (LlvmBasicBlock block in unloop.Blocks)
            {
                LlvmLoop? outer = info.GetLoopFor(block);
                if (unloop.Contains(outer))
                {
                    while (!ReferenceEquals(outer!.ParentLoop, unloop)) outer = outer!.ParentLoop;
                    outer = SubloopParent(outer!);
                }
                for (LlvmLoop? old = unloop.ParentLoop; !ReferenceEquals(old, outer); old = old!.ParentLoop)
                {
                    Debug.Assert(old is not null);
                    old.RemoveBlockFromLoop(block);
                }
            }
        }
        internal void UpdateSubloopParents()
        {
            while (!unloop.IsInnermost)
            {
                LlvmLoop subloop = unloop.RemoveChildLoop(unloop.Children.Count - 1);
                Debug.Assert(subloopParents.ContainsKey(subloop));
                if (subloopParents[subloop] is { } parent) parent.AddChildLoop(subloop);
                else info.AddTopLevelLoop(subloop);
            }
        }
    }
}
