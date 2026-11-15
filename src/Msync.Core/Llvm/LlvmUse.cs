// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Use.h/.cpp and Value.h.
namespace Msync.Core.Llvm;

public sealed class LlvmUse
{
    private LlvmValue? value;
    internal LlvmUse? Next;
    internal LlvmUse? Previous;
    internal bool Linked;

    internal LlvmUse(LlvmUser user, int operandNo) => (User, OperandNo) = (user, operandNo);
    public LlvmUser User { get; }
    public int OperandNo { get; }
    public LlvmValue? Value => value;
    public LlvmValue? Get() => value;
    // Project target-ABI expectation for this operand, not an upstream LLVM Use field.
    // Constants remain context-uniqued independently of their native representation.
    public int LogicalType { get; set; } = -1;
    // Declared physical storage retained when a zero GEP folds to its base.
    public LlvmStructType? TargetStorageType { get; set; }

    public void Set(LlvmValue? replacement)
    {
        if (value is LlvmGetElementPtrInstruction gep && ReferenceEquals(replacement, gep.PointerOperand))
            TargetStorageType ??= gep.TargetStorageType;
        RemoveFromList();
        value = replacement;
        if (replacement is { HasUseList: true }) AddToList(replacement);
    }

    private void AddToList(LlvmValue owner)
    {
        Next = owner.FirstUse;
        if (Next is not null) Next.Previous = this;
        Previous = null;
        Linked = true;
        owner.FirstUse = this;
    }

    private void RemoveFromList()
    {
        if (!Linked) return;
        if (Previous is null) value!.FirstUse = Next;
        else Previous.Next = Next;
        if (Next is not null) Next.Previous = Previous;
        Next = Previous = null;
        Linked = false;
    }

    public void Swap(LlvmUse other)
    {
        (LogicalType, other.LogicalType) = (other.LogicalType, LogicalType);
        (TargetStorageType, other.TargetStorageType) = (other.TargetStorageType, TargetStorageType);
        if (ReferenceEquals(value, other.value)) return;
        (value, other.value) = (other.value, value);
        (Next, other.Next) = (other.Next, Next);
        (Previous, other.Previous) = (other.Previous, Previous);
        (Linked, other.Linked) = (other.Linked, Linked);
        RepairLinks();
        other.RepairLinks();
    }

    private void RepairLinks()
    {
        if (Linked)
        {
            if (Previous is null) value!.FirstUse = this;
            else Previous.Next = this;
        }
        if (Next is not null) Next.Previous = this;
    }

    internal static void Zap(LlvmUse[] uses, int count)
    {
        while (count != 0) uses[--count].Set(null);
    }
}
