// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/GenericLoopInfo.h/.cpp
// loop edges/exits/predecessor/preheader/latch queries; BasicBlock.cpp hoist check.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoop
{
    public bool Contains(LlvmInstruction instruction) => Contains(instruction.Parent!);
    public LlvmBasicBlock? GetLoopPredecessor()
    {
        Debug.Assert(!IsInvalid);
        owner.RecordMutation("loop_info.query.predecessor");
        LlvmBasicBlock? outside = null;
        foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(Header))
        {
            owner.RecordMutation("loop_info.query.predecessor_edges");
            if (Contains(predecessor)) continue;
            if (outside is not null && !ReferenceEquals(outside, predecessor)) return null;
            outside = predecessor;
        }
        return outside;
    }
    public LlvmBasicBlock? GetLoopPreheader()
    {
        LlvmBasicBlock? outside = GetLoopPredecessor();
        if (outside is null) return null;
        LlvmInstruction terminator = outside.GetTerminator();
        Debug.Assert(terminator.SuccessorCount > 0);
        if (terminator.Opcode is LlvmOpcode.CatchSwitch or LlvmOpcode.CatchRet or LlvmOpcode.CleanupRet or
            LlvmOpcode.Invoke or LlvmOpcode.Resume or LlvmOpcode.CallBr) return null;
        return terminator.SuccessorCount == 1 ? outside : null;
    }
    public LlvmBasicBlock? GetLoopLatch()
    {
        Debug.Assert(!IsInvalid);
        owner.RecordMutation("loop_info.query.latch");
        LlvmBasicBlock? latch = null;
        foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(Header))
        {
            if (!Contains(predecessor)) continue;
            if (latch is not null) return null;
            latch = predecessor;
        }
        return latch;
    }
    public uint GetNumBackEdges()
    {
        Debug.Assert(!IsInvalid);
        uint count = 0;
        foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(Header)) if (Contains(predecessor)) ++count;
        return count;
    }
    public void GetLoopLatches(List<LlvmBasicBlock> latches)
    {
        Debug.Assert(!IsInvalid);
        foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(Header)) if (Contains(predecessor)) latches.Add(predecessor);
    }
    public bool IsLoopLatch(LlvmBasicBlock block)
    {
        Debug.Assert(!IsInvalid && Contains(block));
        return LlvmDominatorTree.Predecessors(Header).Contains(block);
    }
    public void GetExitingBlocks(List<LlvmBasicBlock> exiting)
    {
        Debug.Assert(!IsInvalid);
        foreach (LlvmBasicBlock block in Blocks)
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
                if (!Contains(successor)) { exiting.Add(block); break; }
    }
    public void GetExitBlocks(List<LlvmBasicBlock> exits)
    {
        Debug.Assert(!IsInvalid);
        foreach (LlvmBasicBlock block in Blocks)
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
                if (!Contains(successor)) exits.Add(successor);
    }
    public void GetUniqueExitBlocks(List<LlvmBasicBlock> exits)
    {
        Debug.Assert(!IsInvalid);
        HashSet<LlvmBasicBlock> visited = [];
        foreach (LlvmBasicBlock block in Blocks)
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
                if (!Contains(successor) && visited.Add(successor)) exits.Add(successor);
    }
    public bool HasDedicatedExits()
    {
        List<LlvmBasicBlock> exits = [];
        GetUniqueExitBlocks(exits);
        foreach (LlvmBasicBlock exit in exits)
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(exit))
                if (!Contains(predecessor)) return false;
        return true;
    }
    public bool IsLCSSAForm(LlvmDominatorTree tree, bool ignoreTokens = true)
    {
        foreach (LlvmBasicBlock block in Blocks) if (!IsBlockInLCSSAForm(this, block, tree, ignoreTokens)) return false;
        return true;
    }
    public bool IsRecursivelyLCSSAForm(LlvmDominatorTree tree, LlvmLoopInfo loops, bool ignoreTokens = true)
    {
        foreach (LlvmBasicBlock block in Blocks)
            if (!IsBlockInLCSSAForm(loops.GetLoopFor(block)!, block, tree, ignoreTokens)) return false;
        return true;
    }
    private static bool IsBlockInLCSSAForm(LlvmLoop loop, LlvmBasicBlock block, LlvmDominatorTree tree, bool ignoreTokens)
    {
        foreach (LlvmInstruction instruction in block.Instructions)
        {
            if (ignoreTokens && instruction.Type.IsTokenLikeTy()) continue;
            foreach (LlvmUse use in instruction.Uses)
            {
                loop.owner.RecordMutation("loop_info.query.lcssa_uses");
                LlvmInstruction user = (LlvmInstruction)use.User;
                LlvmBasicBlock useBlock = user is LlvmPhiInstruction phi ? phi.GetIncomingBlock(use.OperandNo) : user.Parent!;
                if (!ReferenceEquals(useBlock, block) && !loop.Contains(useBlock) && tree.IsReachableFromEntry(useBlock)) return false;
            }
        }
        return true;
    }
    public bool IsLoopSimplifyForm() => GetLoopPreheader() is not null && GetLoopLatch() is not null && HasDedicatedExits();
    public LlvmBasicBlock? GetExitingBlock()
    {
        LlvmBasicBlock? result = null;
        foreach (LlvmBasicBlock block in Blocks)
        {
            if (!IsExiting(block)) continue;
            if (result is not null) return null;
            result = block;
        }
        return result;
    }
    private bool IsExiting(LlvmBasicBlock block)
    {
        foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block)) if (!Contains(successor)) return true;
        return false;
    }
    public LlvmBasicBlock? GetExitBlock()
    {
        LlvmBasicBlock? result = null;
        foreach (LlvmBasicBlock block in Blocks)
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
            {
                if (Contains(successor)) continue;
                if (result is not null) return null;
                result = successor;
            }
        return result;
    }
    public LlvmBasicBlock? GetUniqueExitBlock()
    {
        LlvmBasicBlock? result = null;
        foreach (LlvmBasicBlock block in Blocks)
            foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
            {
                if (Contains(successor)) continue;
                if (result is not null && !ReferenceEquals(result, successor)) return null;
                result = successor;
            }
        return result;
    }
}
