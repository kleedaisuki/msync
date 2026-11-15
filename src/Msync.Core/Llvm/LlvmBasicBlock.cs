// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicBlock.h/.cpp, SymbolTableListTraits.h.
namespace Msync.Core.Llvm;

public sealed partial class LlvmBasicBlock : LlvmValue
{
    private int instructionCount;
    private readonly InstructionSequence instructions;
    public LlvmInstruction? FirstInstruction { get; private set; }
    public LlvmInstruction? LastInstruction { get; private set; }
    public LlvmFunction? Parent { get; private set; }
    public LlvmBasicBlock? NextNode { get; internal set; }
    public LlvmBasicBlock? PreviousNode { get; internal set; }
    public uint Number { get; internal set; } = uint.MaxValue;
    public LlvmModule? Module => Parent?.Parent;
    public IReadOnlyCollection<LlvmInstruction> Instructions => instructions;
    public LlvmBasicBlock(LlvmContext context, string name = "", LlvmFunction? parent = null,
        LlvmBasicBlock? insertBefore = null) : base(LlvmType.GetLabelTy(context), (byte)LlvmValueKind.BasicBlockVal)
    {
        instructions = new InstructionSequence(this);
        if (parent is not null) parent.Insert(this, insertBefore);
        else System.Diagnostics.Debug.Assert(insertBefore is null);
        SetName(name);
    }
    internal void SetParent(LlvmFunction? parent)
    {
        if (Parent == parent) return;
        foreach (var instruction in instructions)
            if (instruction.HasName) Parent?.ValueSymbolTable?.RemoveValueName(instruction.GetValueName()!);
        Number = parent is null ? uint.MaxValue : parent.NextBlockNum++;
        Parent = parent;
        foreach (var instruction in instructions)
            if (instruction.HasName) Parent?.ValueSymbolTable?.ReinsertValue(instruction);
    }
    public bool HasTerminator => LastInstruction is { IsTerminator: true };
    public LlvmInstruction GetTerminator()
    {
        System.Diagnostics.Debug.Assert(HasTerminator);
        return LastInstruction!;
    }
    public LlvmInstruction? GetTerminatorOrNull() => HasTerminator ? LastInstruction : null;
    public void Append(LlvmInstruction instruction) => InsertBefore(instruction, null);
    public void InsertBefore(LlvmInstruction instruction, LlvmInstruction? before)
    {
        System.Diagnostics.Debug.Assert(instruction.Parent is null);
        System.Diagnostics.Debug.Assert(before is null || before.Parent == this);
        InvalidateOrders();
        LlvmInstruction? previous = before is null ? LastInstruction : before.Previous;
        instruction.Parent = this;
        instruction.Previous = previous;
        instruction.Next = before;
        if (previous is null) FirstInstruction = instruction;
        else previous.Next = instruction;
        if (before is null) LastInstruction = instruction;
        else before.Previous = instruction;
        ++instructionCount;
        if (instruction.HasName) Parent?.ValueSymbolTable?.ReinsertValue(instruction);
    }
    public void Remove(LlvmInstruction instruction)
    {
        System.Diagnostics.Debug.Assert(instruction.Parent == this);
        if (instruction.HasName) Parent?.ValueSymbolTable?.RemoveValueName(instruction.GetValueName()!);
        if (instruction.Previous is null) FirstInstruction = instruction.Next;
        else instruction.Previous.Next = instruction.Next;
        if (instruction.Next is null) LastInstruction = instruction.Previous;
        else instruction.Next.Previous = instruction.Previous;
        instruction.Parent = null;
        instruction.Previous = null;
        instruction.Next = null;
        --instructionCount;
    }
    // BasicBlock.cpp moveBefore/moveAfter, intra-function splice subset.
    public void MoveBefore(LlvmBasicBlock before) => Parent!.MoveBlockBefore(this, before);
    public void MoveAfter(LlvmBasicBlock after) => Parent!.MoveBlockAfter(this, after);
    public void RemoveFromParent() => Parent!.Remove(this);
    public void InsertInto(LlvmFunction parent, LlvmBasicBlock? before = null) => parent.Insert(this, before);

    internal void ReplaceSuccessorsPhiUsesWith(LlvmBasicBlock replacement)
    {
        var terminator = GetTerminatorOrNull();
        if (terminator is null) return;
        for (int i = 0; i < terminator.SuccessorCount; ++i)
            terminator.GetSuccessor(i).ReplacePhiUsesWith(this, replacement);
    }
    private void ReplacePhiUsesWith(LlvmBasicBlock oldBlock, LlvmBasicBlock newBlock)
    {
        foreach (var instruction in instructions)
        {
            if (instruction is not LlvmPhiInstruction phi) break;
            for (int i = 0; i < phi.IncomingCount; ++i)
                if (phi.GetIncomingBlock(i) == oldBlock) phi.SetIncomingBlock(i, newBlock);
        }
    }
    private sealed class InstructionSequence(LlvmBasicBlock block) : IReadOnlyCollection<LlvmInstruction>
    {
        public int Count => block.instructionCount;
        public IEnumerator<LlvmInstruction> GetEnumerator()
        {
            for (LlvmInstruction? instruction = block.FirstInstruction; instruction is not null; instruction = instruction.Next)
                yield return instruction;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

}
