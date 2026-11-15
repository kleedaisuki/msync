// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Value.h/.cpp.
// License: vendor/llvm-reference/llvm/LICENSE.TXT.
using System.Diagnostics;
using System.Text;

namespace Msync.Core.Llvm;

public abstract partial class LlvmValue
{
    public const uint MaxAlignmentExponent = 32;
    public const ulong MaximumAlignment = 1UL << (int)MaxAlignmentExponent;
    protected LlvmValue(LlvmType type, byte subclassId) => (Type, SubclassId) = (type, subclassId);
    public LlvmType Type { get; private set; }
    public LlvmContext Context => Type.Context;
    // Project target-ABI annotation, not an upstream LLVM Value field.
    public int LogicalType { get; set; } = -1;
    public byte SubclassId { get; }
    protected internal byte SubclassOptionalData { get; set; }
    protected ushort SubclassData { get; set; }
    internal LlvmUse? FirstUse;
    internal LlvmValueHandleNode? FirstValueHandle;
    internal bool HasUseList => SubclassId > (byte)LlvmValueKind.ConstantDataLastVal;
    public bool UseEmpty => FirstUse is null;
    public bool HasOneUse => FirstUse is { Next: null };
    public bool HasOneUser
    {
        get
        {
            if (FirstUse is null) return false;
            LlvmUser user = FirstUse.User;
            for (LlvmUse? use = FirstUse.Next; use is not null; use = use.Next)
                if (!ReferenceEquals(use.User, user)) return false;
            return true;
        }
    }
    public IEnumerable<LlvmUse> Uses
    {
        get { for (LlvmUse? use = FirstUse; use is not null; use = use.Next) yield return use; }
    }
    public IEnumerable<LlvmUser> Users
    {
        get { foreach (LlvmUse use in Uses) yield return use.User; }
    }
    public int GetNumUses()
    {
        int count = 0;
        for (LlvmUse? use = FirstUse; use is not null; use = use.Next) ++count;
        return count;
    }
    public bool HasNUses(int count)
    {
        LlvmUse? use = FirstUse;
        while (count > 0 && use is not null) { --count; use = use.Next; }
        return count == 0 && use is null;
    }
    public bool HasNUsesOrMore(int count)
    {
        for (LlvmUse? use = FirstUse; count > 0; use = use.Next)
        {
            if (use is null) return false;
            --count;
        }
        return true;
    }
    public void MutateType(LlvmType type) => Type = type;

    public void ReplaceAllUsesWith(LlvmValue replacement)
    {
        Debug.Assert(HasUseList);
        Debug.Assert(!ReferenceEquals(this, replacement));
        Debug.Assert(ReferenceEquals(Type, replacement.Type));
        // Unshared SSA definitions can inherit the replaced value's Native ABI.
        // Context-unique constants keep their ABI expectations on individual Uses.
        if (LogicalType >= 0 && replacement is LlvmInstruction { LogicalType: < 0 } instruction)
            instruction.LogicalType = LogicalType;
        if (this is LlvmGetElementPtrInstruction previous && replacement is LlvmGetElementPtrInstruction gep)
            gep.TargetStorageType ??= previous.GetTargetStorageType();
        if (FirstValueHandle is not null) LlvmValueHandle.ValueIsReplaced(this, replacement);
        while (FirstUse is { } use)
        {
            if (use.LogicalType < 0) use.LogicalType = LogicalType;
            if (use.User is LlvmConstant constant && constant is not LlvmGlobalValue)
                constant.HandleOperandChange(this, replacement);
            else use.Set(replacement);
        }
        if (this is LlvmBasicBlock block)
            block.ReplaceSuccessorsPhiUsesWith((LlvmBasicBlock)replacement);
    }

    public bool HasName => Context.ValueNames.ContainsKey(this);
    public ReadOnlyMemory<byte> NameBytes => GetValueName()?.Key ?? ReadOnlyMemory<byte>.Empty;
    public string Name => Encoding.UTF8.GetString(NameBytes.Span);
    internal LlvmValueName? GetValueName() => Context.ValueNames.GetValueOrDefault(this);
    internal void SetValueName(LlvmValueName? name)
    {
        if (name is null) Context.ValueNames.Remove(this);
        else Context.ValueNames[this] = name;
    }
    internal void DestroyValueName() => SetValueName(null);

    internal void NotifyValueDeletion()
    {
        if (FirstValueHandle is not null) LlvmValueHandle.ValueIsDeleted(this);
    }

    private bool GetSymbolTable(out LlvmValueSymbolTable? table)
    {
        table = this switch
        {
            LlvmInstruction instruction => instruction.Parent?.Parent?.ValueSymbolTable,
            LlvmBasicBlock block => block.Parent?.ValueSymbolTable,
            LlvmGlobalValue global => global.Parent?.ValueSymbolTable,
            LlvmArgument argument => argument.Parent?.ValueSymbolTable,
            _ => null
        };
        return this is not (LlvmInstruction or LlvmBasicBlock or LlvmGlobalValue or LlvmArgument);
    }

    public void SetName(string name) => SetName(Encoding.UTF8.GetBytes(name));
    public void SetName(ReadOnlySpan<byte> name)
    {
        SetNameImpl(name);
        if (this is LlvmFunction function) function.UpdateAfterNameChange();
    }
    private void SetNameImpl(ReadOnlySpan<byte> name)
    {
        bool needNewName = !Context.ShouldDiscardValueNames || this is LlvmGlobalValue;
        if ((!needNewName || name.IsEmpty) && !HasName) return;
        if (!needNewName) name = ReadOnlySpan<byte>.Empty;
        Debug.Assert(!name.Contains((byte)0));
        if (NameBytes.Span.SequenceEqual(name)) return;
        Debug.Assert(!Type.IsVoidTy());
        if (GetSymbolTable(out var table)) return;
        LlvmValueName? newName = name.IsEmpty ? null : table is null
            ? new LlvmValueName(name, this) : table.CreateValueName(name, this);
        if (GetValueName() is { } oldName) table?.RemoveValueName(oldName);
        DestroyValueName();
        if (newName is not null) SetValueName(newName);
    }

    // LLVM Value.cpp::DoPHITranslation: only a PHI in the current block changes.
    public LlvmValue DoPHITranslation(LlvmBasicBlock currentBlock, LlvmBasicBlock predecessorBlock) =>
        this is LlvmPhiInstruction phi && phi.Parent == currentBlock
            ? phi.GetIncomingValue(phi.GetBasicBlockIndex(predecessorBlock)) : this;

    public void TakeName(LlvmValue source)
    {
        Debug.Assert(!ReferenceEquals(this, source));
        if (GetSymbolTable(out var table))
        {
            if (source.HasName) source.SetName("");
            return;
        }
        if (GetValueName() is { } oldName) table?.RemoveValueName(oldName);
        DestroyValueName();
        if (!source.HasName) return;
        source.GetSymbolTable(out var sourceTable);
        LlvmValueName entry = source.GetValueName()!;
        if (!ReferenceEquals(table, sourceTable)) sourceTable?.RemoveValueName(entry);
        source.SetValueName(null);
        SetValueName(entry);
        entry.Value = this;
        if (!ReferenceEquals(table, sourceTable)) table?.ReinsertValue(this);
    }

    public void ReverseUseList()
    {
        LlvmUse? previous = null;
        LlvmUse? use = FirstUse;
        while (use is not null)
        {
            LlvmUse? next = use.Next;
            use.Next = previous;
            use.Previous = next;
            previous = use;
            use = next;
        }
        FirstUse = previous;
    }
}
