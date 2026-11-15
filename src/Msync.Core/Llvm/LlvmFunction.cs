// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Function.h/.cpp (ownership and lazy arguments subset).
namespace Msync.Core.Llvm;

public sealed partial class LlvmFunction : LlvmGlobalObject
{
    private readonly List<LlvmBasicBlock> blocks = [];
    private IReadOnlyList<LlvmArgument>? arguments;
    private readonly IReadOnlyList<LlvmBasicBlock> blockView;
    internal uint NextBlockNum;
    public LlvmValueSymbolTable? ValueSymbolTable { get; }
    public LlvmFunctionType FunctionType => (LlvmFunctionType)ValueType;
    public LlvmType ReturnType => FunctionType.ReturnType;
    public bool IsVarArg => FunctionType.IsVarArg;
    public bool NoInline { get; set; }
    public bool NoDuplicate { get; set; }
    public bool Convergent { get; set; }
    public bool ReturnsTwice { get; set; }
    public bool NoMerge { get; set; }
    public bool NoReturn { get; set; }
    public bool OptimizeForSize { get; set; }
    public bool MinSize { get; set; }
    public bool NoJumpTables { get; set; }
    public bool IsPresplitCoroutine { get; set; }
    public bool NullPointerIsValid { get; set; }
    public static bool NullPointerIsDefined(LlvmFunction? function, uint addressSpace = 0) =>
        function?.NullPointerIsValid == true || addressSpace != 0;
    public bool HasMinSize => MinSize;
    public bool HasOptSize => OptimizeForSize || MinSize;
    public bool HasLLVMReservedName { get; private set; }
    public LlvmIntrinsicDescriptor? IntrinsicDescriptor { get; private set; }
    public IReadOnlyList<LlvmBasicBlock> BasicBlocks => blockView;
    public uint GetMaxBlockNumber() => NextBlockNum;
    public override bool IsDeclaration => blocks.Count == 0;
    public int ArgumentCount => FunctionType.NumParams;
    public IReadOnlyList<LlvmArgument> Arguments
    {
        get
        {
            if (arguments is null)
            {
                var storage = new LlvmArgument[ArgumentCount];
                for (int i = 0; i < storage.Length; ++i)
                    storage[i] = new LlvmArgument(FunctionType.GetParamType(i), parent: this, argNo: (uint)i);
                arguments = Array.AsReadOnly(storage);
            }
            return arguments;
        }
    }
    public LlvmFunction(LlvmFunctionType type, LlvmLinkage linkage, uint addressSpace,
        string name = "", LlvmModule? parent = null)
        : base(type, LlvmValueKind.FunctionVal, 0, linkage, name, addressSpace)
    {
        blockView = blocks.AsReadOnly();
        if (!Context.ShouldDiscardValueNames)
            ValueSymbolTable = new LlvmValueSymbolTable(Context.NonGlobalValueMaxNameSize);
        parent?.InsertFunction(this);
        UpdateAfterNameChange();
    }
    public static LlvmFunction Create(LlvmFunctionType type, LlvmLinkage linkage,
        string name = "", LlvmModule? parent = null) =>
        new(type, linkage, parent?.DataLayout.GetProgramAddressSpace() ?? 0, name, parent);

    internal void UpdateAfterNameChange()
    {
        HasLLVMReservedName = NameBytes.Span.StartsWith("llvm."u8);
        IntrinsicDescriptor = LlvmIntrinsic.LookupDescriptor(this);
    }

    public void Insert(LlvmBasicBlock block, LlvmBasicBlock? before = null)
    {
        System.Diagnostics.Debug.Assert(block.Parent is null);
        int index = before is null ? blocks.Count : blocks.IndexOf(before);
        LinkBlockBefore(block, before);
        block.SetParent(this);
        blocks.Insert(index, block);
        if (block.HasName) ValueSymbolTable?.ReinsertValue(block);
    }
    // Intra-function SymbolTableList::splice: ownership, number and names do not change.
    internal void MoveBlockBefore(LlvmBasicBlock block, LlvmBasicBlock? before)
    {
        System.Diagnostics.Debug.Assert(ReferenceEquals(block.Parent, this));
        System.Diagnostics.Debug.Assert(before is null || ReferenceEquals(before.Parent, this));
        if (ReferenceEquals(block, before) || ReferenceEquals(block.NextNode, before)) return;
        int source = blocks.IndexOf(block);
        int destination = before is null ? blocks.Count : blocks.IndexOf(before);
        UnlinkBlock(block);
        blocks.RemoveAt(source);
        if (source < destination) --destination;
        LinkBlockBefore(block, before);
        blocks.Insert(destination, block);
    }
    internal void MoveBlockAfter(LlvmBasicBlock block, LlvmBasicBlock after)
    {
        System.Diagnostics.Debug.Assert(ReferenceEquals(block.Parent, this) && ReferenceEquals(after.Parent, this));
        int index = blocks.IndexOf(after) + 1;
        MoveBlockBefore(block, index == blocks.Count ? null : blocks[index]);
    }
    public void Remove(LlvmBasicBlock block)
    {
        if (block.HasName) ValueSymbolTable?.RemoveValueName(block.GetValueName()!);
        UnlinkBlock(block);
        blocks.Remove(block);
        block.SetParent(null);
    }
    public void RemoveFromParent() => Parent!.RemoveFunction(this);
}
