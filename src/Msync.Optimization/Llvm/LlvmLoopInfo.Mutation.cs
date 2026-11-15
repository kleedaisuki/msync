// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/GenericLoopInfo.h
// block storage materialization, membership/parent mutation and map ownership;
// GenericLoopInfoImpl.h addBasicBlockToLoop; Analysis/LoopInfo.h replacement.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoop
{
    internal void SetBorrowedBlocks(LlvmBasicBlock[] layout, int offset, int length)
    {
        blockData = layout; BlockOffset = offset; BlockLength = length;
        blockCapacity = BorrowedCapacity; pendingHeader = null;
        RefreshBlockView();
    }
    private void RefreshBlockView() => blocks = new ArraySegment<LlvmBasicBlock>(blockData, BlockOffset, BlockLength);
    private void ReallocateBlocks(uint capacity)
    {
        Debug.Assert(capacity >= BlockLength);
        LlvmBasicBlock[] replacement = new LlvmBasicBlock[capacity];
        blockData.AsSpan(BlockOffset, BlockLength).CopyTo(replacement);
        blockData = replacement; BlockOffset = 0; blockCapacity = capacity;
        pendingHeader = null;
        RefreshBlockView();
        owner.RecordMutation("loop_info.blocks.reallocated");
        owner.RecordMutation("loop_info.blocks.copied", (ulong)BlockLength);
    }
    private void MaterializeBlocks()
    {
        if (blockCapacity != BorrowedCapacity) return;
        ReallocateBlocks((uint)BlockLength);
        owner.RecordMutation("loop_info.blocks.materialized");
    }
    public void ReserveBlocks(uint size)
    {
        Debug.Assert(!IsInvalid);
        if (blockCapacity < size) ReallocateBlocks(size);
    }
    public void AddBlockEntry(LlvmBasicBlock block)
    {
        Debug.Assert(!IsInvalid);
        if (blockCapacity == BorrowedCapacity || BlockLength == blockCapacity)
            ReallocateBlocks(Math.Max((uint)BlockLength * 2, 4));
        blockData[BlockOffset + BlockLength++] = block;
        RefreshBlockView();
        owner.RecordMutation("loop_info.blocks.appended");
    }
    public void MoveToHeader(LlvmBasicBlock block)
    {
        Debug.Assert(!IsInvalid);
        if (ReferenceEquals(Header, block)) return;
        MaterializeBlocks();
        int index = Array.IndexOf(blockData, block, BlockOffset, BlockLength);
        Debug.Assert(index >= BlockOffset);
        blockData[index] = blockData[BlockOffset];
        blockData[BlockOffset] = block;
        owner.RecordMutation("loop_info.headers.moved");
    }
    public void RemoveBlockFromLoop(LlvmBasicBlock block)
    {
        Debug.Assert(!IsInvalid);
        MaterializeBlocks();
        int index = Array.IndexOf(blockData, block, BlockOffset, BlockLength);
        Debug.Assert(index >= BlockOffset);
        Array.Copy(blockData, index + 1, blockData, index, BlockOffset + BlockLength - index - 1);
        --BlockLength;
        RefreshBlockView();
        owner.RecordMutation("loop_info.blocks.removed");
    }
    public void AddBasicBlockToLoop(LlvmBasicBlock block, LlvmLoopInfo loops)
    {
        Debug.Assert(!IsInvalid);
        if (BlockLength != 0)
        {
            LlvmLoop? sameHeader = loops.GetLoopFor(Header);
            Debug.Assert(Contains(sameHeader) && ReferenceEquals(Header, sameHeader!.Header));
        }
        Debug.Assert(loops.GetLoopFor(block) is null);
        loops.ChangeLoopFor(block, this);
        for (LlvmLoop? loop = this; loop is not null; loop = loop.ParentLoop) loop.AddBlockEntry(block);
    }
    public void SetParentLoop(LlvmLoop? parent) { Debug.Assert(!IsInvalid); ParentLoop = parent; }
    public void AddChildLoop(LlvmLoop child)
    {
        Debug.Assert(!IsInvalid && child.ParentLoop is null);
        child.ParentLoop = this; Children.Add(child);
        owner.RecordMutation("loop_info.children.added");
    }
    public LlvmLoop RemoveChildLoop(int index)
    {
        Debug.Assert(!IsInvalid && (uint)index < Children.Count);
        LlvmLoop child = Children[index];
        Debug.Assert(ReferenceEquals(child.ParentLoop, this));
        Children.RemoveAt(index); child.ParentLoop = null;
        owner.RecordMutation("loop_info.children.removed");
        return child;
    }
    public LlvmLoop RemoveChildLoop(LlvmLoop child) => RemoveChildLoop(Children.IndexOf(child));
    internal void Destroy()
    {
        Debug.Assert(Children.Count == 0);
        IsInvalid = true;
        blockData = []; blocks = Array.Empty<LlvmBasicBlock>(); BlockLength = 0;
        owner.RecordMutation("loop_info.loops.destroyed");
    }
}

public sealed partial class LlvmLoopInfo
{
    public LlvmLoop AllocateLoop()
    {
        RecordMutation("loop_info.loops.allocated");
        return new(this);
    }
    public void ChangeLoopFor(LlvmBasicBlock block, LlvmLoop? loop)
    {
        Debug.Assert(ReferenceEquals(block.Parent, Function));
        if (block.Number >= blockMap.Length)
        {
            Debug.Assert(block.Number < Function.NextBlockNum);
            Array.Resize(ref blockMap, (int)Function.NextBlockNum);
            RecordMutation("loop_info.block_map.grown");
        }
        blockMap[block.Number] = loop;
        RecordMutation("loop_info.block_map.changed");
    }
    public void ReplaceLoop(LlvmLoop oldLoop, LlvmLoop newLoop)
    {
        Debug.Assert(newLoop.ParentLoop is null);
        LlvmLoop? parent = oldLoop.ParentLoop;
        List<LlvmLoop> siblings = parent is null ? roots : parent.Children;
        int index = siblings.IndexOf(oldLoop);
        Debug.Assert(index >= 0);
        siblings[index] = newLoop;
        oldLoop.SetParentLoop(null); newLoop.SetParentLoop(parent);
        RecordMutation("loop_info.loops.replaced");
    }
    public void AddTopLevelLoop(LlvmLoop loop)
    {
        Debug.Assert(loop.IsOutermost);
        roots.Add(loop); RecordMutation("loop_info.roots.added");
    }
    public LlvmLoop RemoveLoop(int index)
    {
        Debug.Assert((uint)index < roots.Count && roots[index].IsOutermost);
        LlvmLoop loop = roots[index]; roots.RemoveAt(index);
        RecordMutation("loop_info.roots.removed"); return loop;
    }
    public LlvmLoop RemoveLoop(LlvmLoop loop) => RemoveLoop(roots.IndexOf(loop));
    public void RemoveBlock(LlvmBasicBlock block)
    {
        Debug.Assert(ReferenceEquals(block.Parent, Function));
        if (block.Number >= blockMap.Length) return;
        for (LlvmLoop? loop = blockMap[block.Number]; loop is not null; loop = loop.ParentLoop) loop.RemoveBlockFromLoop(block);
        blockMap[block.Number] = null;
        RecordMutation("loop_info.block_map.removed");
    }
    public bool ReplacementPreservesLCSSAForm(LlvmInstruction from, LlvmValue to)
    {
        if (to is not LlvmInstruction instruction || ReferenceEquals(instruction.Parent, from.Parent)) return true;
        LlvmLoop? toLoop = GetLoopFor(instruction.Parent!);
        return toLoop is null || toLoop.Contains(GetLoopFor(from.Parent!));
    }
    internal void RecordMutation(string name, ulong work = 1)
    {
        if (mutationMetrics is null) return;
        ulong count = mutationMetrics.GetValueOrDefault(name) + work;
        mutationMetrics[name] = count;
        telemetry!.Metric(name, (long)count);
    }
}
