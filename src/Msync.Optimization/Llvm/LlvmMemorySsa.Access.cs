// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/MemorySSA.h
// MemoryAccess/MemoryUseOrDef/MemoryUse/MemoryDef/MemoryPhi.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public abstract partial class LlvmMemoryAccess : LlvmUser
{
    private protected LlvmMemoryAccess(LlvmBasicBlock block, LlvmValueKind kind, int operands)
        : base(LlvmType.GetVoidTy(block.Context), (byte)kind, operands) => Block = block;
    public LlvmBasicBlock Block { get; internal set; }
    public LlvmMemoryAccess? Previous { get; internal set; }
    public LlvmMemoryAccess? Next { get; internal set; }
    public LlvmMemoryAccess? PreviousDef { get; internal set; }
    public LlvmMemoryAccess? NextDef { get; internal set; }
    public uint GetID() => this switch
    {
        LlvmMemoryDef definition => definition.ID,
        LlvmMemoryPhi phi => phi.ID,
        _ => throw new InvalidOperationException("Only MemoryDef and MemoryPhi have IDs.")
    };
}

public abstract partial class LlvmMemoryUseOrDef : LlvmMemoryAccess
{
    private protected LlvmMemoryUseOrDef(LlvmBasicBlock block, LlvmValueKind kind, int operands,
        LlvmInstruction? instruction, LlvmMemoryAccess? definition) : base(block, kind, operands)
    { MemoryInstruction = instruction; SetDefiningAccess(definition); }
    public LlvmInstruction? MemoryInstruction { get; }
    public LlvmMemoryAccess? DefiningAccess => (LlvmMemoryAccess?)GetOperand(0);
    public void SetDefiningAccess(LlvmMemoryAccess? access, bool optimized = false)
    {
        if (optimized) SetOptimized(access!);
        else SetOperand(0, access);
    }
    public abstract bool IsOptimized { get; }
    public abstract LlvmMemoryAccess? GetOptimized();
    public abstract void SetOptimized(LlvmMemoryAccess access);
    public abstract void ResetOptimized();
}

public sealed partial class LlvmMemoryUse : LlvmMemoryUseOrDef
{
    private uint optimizedId = uint.MaxValue;
    internal LlvmMemoryUse(LlvmBasicBlock block, LlvmInstruction instruction, LlvmMemoryAccess? definition = null)
        : base(block, LlvmValueKind.MemoryUseVal, 1, instruction, definition) { }
    public override bool IsOptimized => DefiningAccess is { } definition && optimizedId == definition.GetID();
    public override LlvmMemoryAccess? GetOptimized() => DefiningAccess;
    public override void SetOptimized(LlvmMemoryAccess access)
    { optimizedId = access.GetID(); SetOperand(0, access); }
    public override void ResetOptimized() => optimizedId = uint.MaxValue;
}

public sealed partial class LlvmMemoryDef : LlvmMemoryUseOrDef
{
    private uint optimizedId = uint.MaxValue;
    internal LlvmMemoryDef(LlvmBasicBlock block, LlvmInstruction? instruction, uint id, LlvmMemoryAccess? definition = null)
        : base(block, LlvmValueKind.MemoryDefVal, 2, instruction, definition) => ID = id;
    public uint ID { get; }
    public override LlvmMemoryAccess? GetOptimized() => (LlvmMemoryAccess?)GetOperand(1);
    public override bool IsOptimized => GetOptimized() is { } access && optimizedId == access.GetID();
    public override void SetOptimized(LlvmMemoryAccess access)
    { SetOperand(1, access); optimizedId = access.GetID(); }
    public override void ResetOptimized() { optimizedId = uint.MaxValue; SetOperand(1, null); }
}

public sealed partial class LlvmMemoryPhi : LlvmMemoryAccess
{
    private LlvmBasicBlock?[] incomingBlocks;
    internal LlvmMemoryPhi(LlvmBasicBlock block, uint id, int reservedPredecessors = 0)
        : base(block, LlvmValueKind.MemoryPhiVal, 0)
    {
        ID = id; incomingBlocks = new LlvmBasicBlock?[reservedPredecessors];
        if (reservedPredecessors != 0) GrowOperands(reservedPredecessors);
    }
    public uint ID { get; }
    public int IncomingCount => OperandCount;
    public LlvmMemoryAccess GetIncomingValue(int index) => (LlvmMemoryAccess)GetOperand(index)!;
    public LlvmBasicBlock GetIncomingBlock(int index) => incomingBlocks[index]!;
    public LlvmBasicBlock GetIncomingBlock(LlvmUse use)
    { Debug.Assert(ReferenceEquals(use.User, this)); return GetIncomingBlock(use.OperandNo); }
    public void SetIncomingValue(int index, LlvmMemoryAccess access) => SetOperand(index, access);
    public void SetIncomingBlock(int index, LlvmBasicBlock block) => incomingBlocks[index] = block;
    public void AddIncoming(LlvmMemoryAccess access, LlvmBasicBlock block)
    {
        int index = IncomingCount;
        if (index == incomingBlocks.Length)
        {
            int capacity = Math.Max(index + index / 2, 2);
            GrowOperands(capacity); Array.Resize(ref incomingBlocks, capacity);
        }
        SetOperandCount(index + 1); SetIncomingValue(index, access); SetIncomingBlock(index, block);
    }
    public int GetBasicBlockIndex(LlvmBasicBlock block)
    {
        for (int index = 0; index < IncomingCount; ++index)
            if (ReferenceEquals(incomingBlocks[index], block)) return index;
        return -1;
    }
    public LlvmMemoryAccess GetIncomingValueForBlock(LlvmBasicBlock block)
    { int index = GetBasicBlockIndex(block); Debug.Assert(index >= 0); return GetIncomingValue(index); }
    public void UnorderedDeleteIncoming(int index)
    {
        int last = IncomingCount - 1;
        Debug.Assert((uint)index < (uint)IncomingCount && IncomingCount >= 2);
        SetIncomingValue(index, GetIncomingValue(last)); SetIncomingBlock(index, GetIncomingBlock(last));
        SetOperand(last, null); incomingBlocks[last] = null; SetOperandCount(last);
    }
}
