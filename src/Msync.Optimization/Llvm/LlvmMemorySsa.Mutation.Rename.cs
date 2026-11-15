// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSA.cpp renameBlock/renameSuccessorPhis/renamePass partial-update overloads.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsa
{
    private LlvmMemoryAccess? RenameUpdatedBlock(LlvmBasicBlock block, LlvmMemoryAccess? incoming)
    {
        if (GetBlockAccesses(block) is not { } accesses) return incoming;
        foreach (var access in accesses)
        {
            Count("update.rename.accesses");
            if (access is LlvmMemoryUseOrDef useOrDef)
            {
                useOrDef.SetDefiningAccess(incoming);
                if (access is LlvmMemoryDef) incoming = access;
            }
            else incoming = access;
        }
        return incoming;
    }

    private void RenameUpdatedSuccessorPhis(LlvmBasicBlock block, LlvmMemoryAccess incoming)
    {
        foreach (var successor in LlvmDominatorTree.Successors(block))
        {
            if (GetMemoryAccess(successor) is not { } phi) continue;
            bool replaced = false;
            for (int index = 0; index < phi.IncomingCount; ++index)
                if (ReferenceEquals(phi.GetIncomingBlock(index), block))
                { phi.SetIncomingValue(index, incoming); replaced = true; Count("update.rename.phi_edges"); }
            System.Diagnostics.Debug.Assert(replaced);
        }
    }

    internal void RenamePass(LlvmBasicBlock block, LlvmMemoryAccess? incoming, HashSet<LlvmBasicBlock> visited)
    {
        var root = DominatorTree.GetNode(block)!;
        if (!visited.Add(block)) return;
        Count("update.rename.blocks");
        incoming = RenameUpdatedBlock(block, incoming);
        RenameUpdatedSuccessorPhis(block, incoming!);
        var stack = new List<(LlvmDominatorTreeNode? Child, LlvmMemoryAccess? Incoming)> { (root.FirstChild, incoming) };
        while (stack.Count != 0)
        {
            var frame = stack[^1];
            if (frame.Child is not { } child) { stack.RemoveAt(stack.Count - 1); continue; }
            stack[^1] = (child.Sibling, frame.Incoming);
            incoming = frame.Incoming;
            block = child.Block!;
            Count("update.rename.blocks");
            if (!visited.Add(block))
            {
                if (GetBlockDefs(block) is { } definitions) incoming = definitions.Last!;
            }
            else incoming = RenameUpdatedBlock(block, incoming);
            RenameUpdatedSuccessorPhis(block, incoming!);
            stack.Add((child.FirstChild, incoming));
        }
    }
}
