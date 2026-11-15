// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: User.h/.cpp.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract class LlvmUser : LlvmValue
{
    private LlvmUse[] operands;

    protected LlvmUser(LlvmType type, byte subclassId, int operandCount) : base(type, subclassId)
    {
        operands = AllocateOperands(operandCount);
        OperandCount = operandCount;
    }

    protected LlvmUser(LlvmType type, byte subclassId, IReadOnlyList<LlvmValue?> values)
        : this(type, subclassId, values.Count)
    {
        for (int i = 0; i < values.Count; ++i) operands[i].Set(values[i]);
    }

    // User.cpp: only assume is in the represented droppable intrinsic registry.
    public bool IsDroppable() => this is LlvmCallBase { CalledFunction.IntrinsicDescriptor.IsAssume: true };
    public int OperandCount { get; private set; }
    protected int OperandCapacity => operands.Length;
    public LlvmValue? GetOperand(int index)
    {
        Debug.Assert((uint)index < (uint)OperandCount);
        return operands[index].Value;
    }
    public LlvmUse GetOperandUse(int index)
    {
        Debug.Assert((uint)index < (uint)OperandCount);
        return operands[index];
    }
    public void SetOperand(int index, LlvmValue? value)
    {
        Debug.Assert(this is not LlvmConstant || this is LlvmGlobalValue);
        GetOperandUse(index).Set(value);
    }
    public IEnumerable<LlvmUse> OperandUses
    {
        get { for (int i = 0; i < OperandCount; ++i) yield return operands[i]; }
    }
    public IEnumerable<LlvmValue?> Operands
    {
        get { for (int i = 0; i < OperandCount; ++i) yield return operands[i].Value; }
    }
    public void DropAllReferences()
    {
        for (int i = 0; i < OperandCount; ++i) operands[i].Set(null);
    }
    public bool ReplaceUsesOfWith(LlvmValue from, LlvmValue? to)
    {
        if (ReferenceEquals(from, to)) return false;
        Debug.Assert(this is not LlvmConstant || this is LlvmGlobalValue);
        bool changed = false;
        for (int i = 0; i < OperandCount; ++i)
            if (ReferenceEquals(operands[i].Value, from))
            {
                if (operands[i].LogicalType < 0) operands[i].LogicalType = from.LogicalType;
                operands[i].Set(to);
                changed = true;
            }
        return changed;
    }

    protected void GrowOperands(int capacity)
    {
        Debug.Assert(capacity > OperandCount);
        var replacement = AllocateOperands(capacity);
        for (int i = 0; i < OperandCount; ++i)
        {
            replacement[i].Set(operands[i].Value);
            replacement[i].LogicalType = operands[i].LogicalType;
        }
        LlvmUse.Zap(operands, OperandCount);
        operands = replacement;
    }
    protected void SetOperandCount(int count)
    {
        Debug.Assert((uint)count <= (uint)operands.Length);
        OperandCount = count;
    }
    private LlvmUse[] AllocateOperands(int count)
    {
        var result = new LlvmUse[count];
        for (int i = 0; i < count; ++i) result[i] = new LlvmUse(this, i);
        return result;
    }
}
