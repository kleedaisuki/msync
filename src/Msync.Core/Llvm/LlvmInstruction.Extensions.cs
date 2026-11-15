// Project extension operations; these opcodes are not claimed to exist upstream.
namespace Msync.Core.Llvm;

/// <summary>
/// On successful completion, produces fresh non-null writable storage of ByteSize
/// bytes with Alignment. Bytes are uninitialized; live results are not invalidated
/// by user frees. The operation retains allocator-state effects and may-throw behavior.
/// </summary>
public sealed class LlvmHeapAllocInst : LlvmInstruction
{
    public LlvmHeapAllocInst(LlvmValue byteSize, LlvmAlign alignment, uint addressSpace = 0)
        : base(LlvmPointerType.Get(byteSize.Context, addressSpace), LlvmOpcode.HeapAlloc, [byteSize])
    {
        System.Diagnostics.Debug.Assert(byteSize.Type.IsIntegerTy());
        Alignment = alignment;
    }
    public LlvmValue ByteSize { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmAlign Alignment { get; }
    public int LogicalLayout { get; set; } = -1;
    public int LogicalAllocationSite { get; set; } = -1;
    public uint AddressSpace => ((LlvmPointerType)Type).AddressSpace;
    protected override LlvmInstruction CloneImpl() => new LlvmHeapAllocInst(ByteSize, Alignment, AddressSpace) { LogicalLayout = LogicalLayout, LogicalAllocationSite = LogicalAllocationSite };
}

public enum LlvmFileInFormat : byte { Text, Lines, Bytes }

public sealed class LlvmFileInInst : LlvmInstruction
{
    public LlvmFileInInst(LlvmType resultType, LlvmValue source, LlvmFileInFormat format)
        : base(resultType, LlvmOpcode.FileIn, [source]) => Format = format;
    public LlvmValue SourceOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmFileInFormat Format { get; }
    protected override LlvmInstruction CloneImpl() => new LlvmFileInInst(Type, SourceOperand, Format);
}

public enum LlvmFileOutMode : byte { Transfer, WriteText, AppendText, WriteBytes, AppendBytes }

public sealed class LlvmFileOutInst : LlvmInstruction
{
    public LlvmFileOutInst(LlvmType resultType, LlvmValue value, LlvmValue destination, LlvmFileOutMode mode)
        : base(resultType, LlvmOpcode.FileOut, [value, destination]) => Mode = mode;
    public LlvmValue Value { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue Destination { get => GetOperand(1)!; set => SetOperand(1, value); }
    public LlvmFileOutMode Mode { get; }
    protected override LlvmInstruction CloneImpl() => new LlvmFileOutInst(Type, Value, Destination, Mode);
}
