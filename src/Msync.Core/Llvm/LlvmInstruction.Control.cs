// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instructions.h,
// lib/IR/Instructions.cpp (PHINode, ReturnInst, branches, SwitchInst).
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed class LlvmPhiInstruction : LlvmInstruction, ILlvmFastMathInstruction
{
    private LlvmBasicBlock?[] incomingBlocks;
    public LlvmPhiInstruction(LlvmType type, int reservedValues = 0) : base(type, LlvmOpcode.PHI, 0)
    {
        incomingBlocks = new LlvmBasicBlock?[reservedValues];
        if (reservedValues != 0) GrowOperands(reservedValues);
    }
    public int IncomingCount => OperandCount;
    public LlvmFastMathFlags FastMathFlags { get; set; }
    public LlvmValue GetIncomingValue(int index) => GetOperand(index)!;
    public LlvmBasicBlock GetIncomingBlock(int index) => incomingBlocks[index]!;
    public void SetIncomingValue(int index, LlvmValue value)
    {
        Debug.Assert(ReferenceEquals(value.Type, Type)); SetOperand(index, value);
    }
    public void SetIncomingBlock(int index, LlvmBasicBlock block) => incomingBlocks[index] = block;
    public void AddIncoming(LlvmValue value, LlvmBasicBlock block)
    {
        int index = IncomingCount;
        if (index == incomingBlocks.Length)
        {
            int capacity = Math.Max(2, index + index / 2);
            GrowOperands(capacity); Array.Resize(ref incomingBlocks, capacity);
        }
        SetOperandCount(index + 1);
        SetIncomingValue(index, value); SetIncomingBlock(index, block);
    }
    public LlvmValue RemoveIncomingValue(int index, bool deleteIfEmpty = true)
    {
        LlvmValue removed = GetIncomingValue(index);
        int last = IncomingCount - 1;
        if (index != last)
        {
            SetIncomingValue(index, GetIncomingValue(last)); SetIncomingBlock(index, GetIncomingBlock(last));
            GetOperandUse(index).LogicalType = GetOperandUse(last).LogicalType;
        }
        SetOperand(last, null); GetOperandUse(last).LogicalType = -1;
        incomingBlocks[last] = null; SetOperandCount(last);
        if (last == 0 && deleteIfEmpty)
        {
            ReplaceAllUsesWith(LlvmPoisonValue.Get(Type)); EraseFromParent();
        }
        return removed;
    }
    public int GetBasicBlockIndex(LlvmBasicBlock block)
    {
        for (int index = 0; index < IncomingCount; index++)
            if (ReferenceEquals(incomingBlocks[index], block)) return index;
        return -1;
    }
    public LlvmValue? HasConstantValue()
    {
        Debug.Assert(IncomingCount != 0);
        LlvmValue value = GetIncomingValue(0);
        for (int index = 1; index < IncomingCount; index++)
        {
            LlvmValue next = GetIncomingValue(index);
            if (ReferenceEquals(next, value) || ReferenceEquals(next, this)) continue;
            if (!ReferenceEquals(value, this)) return null;
            value = next;
        }
        return ReferenceEquals(value, this) ? LlvmPoisonValue.Get(Type) : value;
    }
    protected override LlvmInstruction CloneImpl()
    {
        LlvmPhiInstruction clone = new(Type, IncomingCount) { FastMathFlags = FastMathFlags };
        for (int index = 0; index < IncomingCount; index++) clone.AddIncoming(GetIncomingValue(index), GetIncomingBlock(index));
        return clone;
    }
}

public sealed class LlvmReturnInstruction : LlvmInstruction
{
    public LlvmReturnInstruction(LlvmContext context, LlvmValue? value = null)
        : base(VoidType(context), LlvmOpcode.Ret, value is null ? [] : [value]) { }
    public LlvmValue? ReturnValue => OperandCount == 0 ? null : GetOperand(0);
    public override int SuccessorCount => 0;
    protected override LlvmInstruction CloneImpl() => new LlvmReturnInstruction(Context, ReturnValue);
}

public sealed class LlvmUncondBrInstruction : LlvmInstruction
{
    public LlvmUncondBrInstruction(LlvmBasicBlock destination)
        : base(VoidType(destination.Context), LlvmOpcode.UncondBr, [destination]) { }
    public LlvmBasicBlock Destination { get => (LlvmBasicBlock)GetOperand(0)!; set => SetOperand(0, value); }
    public override int SuccessorCount => 1;
    public override LlvmBasicBlock GetSuccessor(int index) { Debug.Assert(index == 0); return Destination; }
    public override void SetSuccessor(int index, LlvmBasicBlock successor) { Debug.Assert(index == 0); Destination = successor; }
    protected override LlvmInstruction CloneImpl() => new LlvmUncondBrInstruction(Destination);
}

public sealed class LlvmCondBrInstruction : LlvmInstruction
{
    public LlvmCondBrInstruction(LlvmValue condition, LlvmBasicBlock whenTrue, LlvmBasicBlock whenFalse)
        : base(VoidType(condition.Context), LlvmOpcode.CondBr, [condition, whenTrue, whenFalse])
        => Debug.Assert(condition.Type.IsIntegerTy(1));
    public LlvmValue Condition { get => GetOperand(0)!; set => SetOperand(0, value); }
    public override int SuccessorCount => 2;
    public override LlvmBasicBlock GetSuccessor(int index) { Debug.Assert((uint)index < 2); return (LlvmBasicBlock)GetOperand(index + 1)!; }
    public override void SetSuccessor(int index, LlvmBasicBlock successor) { Debug.Assert((uint)index < 2); SetOperand(index + 1, successor); }
    public void SwapSuccessors()
    {
        LlvmBasicBlock previous = GetSuccessor(0); SetSuccessor(0, GetSuccessor(1)); SetSuccessor(1, previous);
    }
    protected override LlvmInstruction CloneImpl() => new LlvmCondBrInstruction(Condition, GetSuccessor(0), GetSuccessor(1));
}

public sealed class LlvmSwitchInstruction : LlvmInstruction
{
    private LlvmConstantInt?[] caseValues;
    public LlvmSwitchInstruction(LlvmValue condition, LlvmBasicBlock defaultDestination, int reservedCases = 0)
        : base(VoidType(condition.Context), LlvmOpcode.Switch, 0)
    {
        GrowOperands(2 + reservedCases); SetOperandCount(2);
        SetOperand(0, condition); SetOperand(1, defaultDestination);
        caseValues = new LlvmConstantInt?[reservedCases];
    }
    public LlvmValue Condition { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmBasicBlock DefaultDestination { get => (LlvmBasicBlock)GetOperand(1)!; set => SetOperand(1, value); }
    public int CaseCount => OperandCount - 2;
    public LlvmConstantInt GetCaseValue(int index) => caseValues[index]!;
    public void SetCaseValue(int index, LlvmConstantInt value)
    {
        Debug.Assert((uint)index < (uint)CaseCount);
        caseValues[index] = value;
    }
    public bool DefaultDestUnreachable
    {
        get
        {
            LlvmInstruction? first = DefaultDestination.FirstInstruction;
            while (first is LlvmPhiInstruction) first = first.Next;
            return first is LlvmUnreachableInstruction;
        }
    }
    public LlvmBasicBlock GetCaseDestination(int index) => (LlvmBasicBlock)GetOperand(index + 2)!;
    public void AddCase(LlvmConstantInt value, LlvmBasicBlock destination)
    {
        int index = CaseCount;
        if (index == caseValues.Length)
        {
            int capacity = OperandCount * 3;
            GrowOperands(capacity); Array.Resize(ref caseValues, capacity - 2);
        }
        SetOperandCount(OperandCount + 1);
        caseValues[index] = value; SetOperand(index + 2, destination);
    }
    public void RemoveCase(int index)
    {
        int last = CaseCount - 1;
        if (index != last) { caseValues[index] = caseValues[last]; SetOperand(index + 2, GetOperand(last + 2)); }
        SetOperand(last + 2, null); caseValues[last] = null; SetOperandCount(OperandCount - 1);
    }
    public int FindCaseValue(LlvmConstantInt value)
    {
        for (int index = 0; index < CaseCount; index++) if (ReferenceEquals(caseValues[index], value)) return index;
        return -1;
    }
    public override int SuccessorCount => OperandCount - 1;
    public override LlvmBasicBlock GetSuccessor(int index) => (LlvmBasicBlock)GetOperand(index + 1)!;
    public override void SetSuccessor(int index, LlvmBasicBlock successor) => SetOperand(index + 1, successor);
    protected override LlvmInstruction CloneImpl()
    {
        LlvmSwitchInstruction clone = new(Condition, DefaultDestination, CaseCount);
        for (int index = 0; index < CaseCount; index++) clone.AddCase(GetCaseValue(index), GetCaseDestination(index));
        return clone;
    }
}

public sealed class LlvmUnreachableInstruction : LlvmInstruction
{
    public LlvmUnreachableInstruction(LlvmContext context) : base(VoidType(context), LlvmOpcode.Unreachable, 0) { }
    public override int SuccessorCount => 0;
    protected override LlvmInstruction CloneImpl() => new LlvmUnreachableInstruction(Context);
}
