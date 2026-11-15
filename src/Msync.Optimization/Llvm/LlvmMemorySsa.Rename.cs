// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: MemorySSA.cpp
// renameBlock/renameSuccessorPhis/renamePass and markUnreachableAsLiveOnEntry.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsa
{
    private LlvmMemoryAccess RenameBlock(LlvmBasicBlock block, LlvmMemoryAccess incoming)
    {
        if (GetBlockAccesses(block) is not { } accesses) return incoming;
        foreach (LlvmMemoryAccess access in accesses)
        {
            Count("rename.accesses");
            if (access is LlvmMemoryUseOrDef useOrDef)
            {
                if (useOrDef.DefiningAccess is null) useOrDef.SetDefiningAccess(incoming);
                if (access is LlvmMemoryDef) incoming = access;
            }
            else incoming = access;
        }
        return incoming;
    }
    private void RenameSuccessorPhis(LlvmBasicBlock block, LlvmMemoryAccess incoming)
    {
        foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
        {
            Count("rename.successor_edges");
            if (GetBlockAccesses(successor)?.First is not LlvmMemoryPhi phi) continue;
            phi.AddIncoming(incoming, block); Count("rename.phi_incoming");
        }
    }
    private void RenamePass(HashSet<LlvmBasicBlock> visited)
    {
        var stack = new List<(LlvmDominatorTreeNode? Child, LlvmMemoryAccess Incoming)>();
        LlvmDominatorTreeNode root = DominatorTree.RootNode!;
        visited.Add(root.Block!); Count("rename.blocks");
        LlvmMemoryAccess incoming = RenameBlock(root.Block!, LiveOnEntryDef);
        RenameSuccessorPhis(root.Block!, incoming);
        stack.Add((root.FirstChild, incoming));
        while (stack.Count != 0)
        {
            var frame = stack[^1];
            if (frame.Child is not { } child) { stack.RemoveAt(stack.Count - 1); continue; }
            stack[^1] = (child.Sibling, frame.Incoming);
            visited.Add(child.Block!); Count("rename.blocks");
            incoming = RenameBlock(child.Block!, frame.Incoming);
            RenameSuccessorPhis(child.Block!, incoming);
            stack.Add((child.FirstChild, incoming));
        }
    }
    private void MarkUnreachable(HashSet<LlvmBasicBlock> visited)
    {
        foreach (LlvmBasicBlock block in Function.BasicBlocks)
        {
            if (visited.Contains(block)) continue;
            Count("unreachable.blocks");
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
            {
                if (!DominatorTree.IsReachableFromEntry(successor)) continue;
                if (GetMemoryAccess(successor) is not { } phi) continue;
                phi.AddIncoming(LiveOnEntryDef, block); Count("unreachable.reachable_phi_edges");
            }
            if (GetBlockAccesses(block) is not { } accesses) continue;
            LlvmMemoryAccess? access = accesses.First;
            while (access is not null)
            {
                LlvmMemoryAccess? next = access.Next;
                if (access is LlvmMemoryUseOrDef useOrDef)
                { useOrDef.SetDefiningAccess(LiveOnEntryDef); Count("unreachable.live_on_entry_accesses"); }
                else
                {
                    LlvmMemoryAccessList definitions = perBlockDefs[block];
                    definitions.Remove(access);
                    if (definitions.Count == 0) perBlockDefs.Remove(block);
                    accesses.Remove(access);
                    if (accesses.Count == 0) { perBlockAccesses.Remove(block); numberingValid.Remove(block); }
                    valueToAccess.Remove(block); access.DropAllReferences(); Count("unreachable.phis_removed");
                }
                access = next;
            }
        }
    }
}
