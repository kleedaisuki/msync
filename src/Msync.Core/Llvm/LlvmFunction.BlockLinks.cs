// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Function.cpp and
// IR/SymbolTableListTraits.h; intrusive ilist node adjacency and splice.
namespace Msync.Core.Llvm;

public sealed partial class LlvmFunction
{
    private void LinkBlockBefore(LlvmBasicBlock block, LlvmBasicBlock? before)
    {
        LlvmBasicBlock? previous = before is not null ? before.PreviousNode : blocks.Count == 0 ? null : blocks[^1];
        block.PreviousNode = previous;
        block.NextNode = before;
        if (previous is not null) previous.NextNode = block;
        if (before is not null) before.PreviousNode = block;
    }

    private static void UnlinkBlock(LlvmBasicBlock block)
    {
        if (block.PreviousNode is { } previous) previous.NextNode = block.NextNode;
        if (block.NextNode is { } next) next.PreviousNode = block.PreviousNode;
        block.PreviousNode = block.NextNode = null;
    }
}
